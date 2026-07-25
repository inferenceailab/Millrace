using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Millrace.Invocations;
using Millrace.Storage;

namespace Millrace.Workflows;

/// <summary>
/// Runs one graph node and computes the instance's next state (ARCHITECTURE.md §6.2).
/// </summary>
/// <remarks>
/// <para>
/// The whole method is arranged around one rule: <b>this class commits nothing.</b> It runs the
/// activity, computes the new document, cursor and follow-on jobs, and hands them to
/// <see cref="JobSideEffects"/> so the worker commits them with this job's own transition. Any
/// storage call of its own would reintroduce exactly the split §11.16 exists to remove.
/// </para>
/// <para>
/// A consequence worth stating: an activity that throws never checkpoints, so the retry re-runs it
/// against the same document it saw the first time. That is what makes at-least-once execution
/// safe to build on.
/// </para>
/// </remarks>
internal sealed class WorkflowDispatcher : IWorkflowDispatcher
{
    // Explicit fields rather than primary-constructor parameters: the nested walker reaches these
    // through its owner, and captured primary parameters are not visible to nested types.
    private readonly IWorkflowStorage _workflows;
    private readonly WorkflowRegistry _registry;
    private readonly JobSideEffects _effects;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly MillraceOptions _options;
    private readonly JsonSerializerOptions _json;

    public WorkflowDispatcher(
        IWorkflowStorage workflows,
        WorkflowRegistry registry,
        JobSideEffects effects,
        IServiceProvider services,
        TimeProvider time,
        IOptions<MillraceOptions> options)
    {
        _workflows = workflows;
        _registry = registry;
        _effects = effects;
        _services = services;
        _time = time;
        _options = options.Value;
        _json = _options.SerializerOptions;
    }

    private JobRecord BuildActivityJob(
        WorkflowInstanceRecord instance, string nodeId, string? joinKey, int loopIndex, TimeSpan? delay)
        => WorkflowJobFactory.Create(instance, nodeId, joinKey, loopIndex, delay, _options, _time);

    public async Task ExecuteAsync(
        Guid instanceId, string nodeId, string? joinKey, int loopIndex, CancellationToken ct)
    {
        var id = new WorkflowInstanceId(instanceId);
        var instance = await _workflows.GetInstanceAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Workflow instance '{id}' does not exist. It was deleted while an activity was in flight.");

        if (!_registry.TryGet(instance.DefinitionId, instance.DefinitionVersion, out var definition))
        {
            throw new InvalidOperationException(
                $"Workflow '{instance.DefinitionId}' version {instance.DefinitionVersion} is not registered. "
                + "In-flight instances finish on the version they started with, so old versions must stay "
                + "registered until they drain.");
        }

        await Walker.Create(definition, this).RunAsync(instance, nodeId, joinKey, loopIndex, ct).ConfigureAwait(false);
    }

    /// <summary>Bridges the non-generic dispatcher to a definition's data type.</summary>
    private abstract class Walker
    {
        public static Walker Create(WorkflowDefinition definition, WorkflowDispatcher owner)
            => (Walker)Activator.CreateInstance(
                typeof(Walker<>).MakeGenericType(definition.DataType), definition, owner)!;

        public abstract Task RunAsync(
            WorkflowInstanceRecord instance, string nodeId, string? joinKey, int loopIndex, CancellationToken ct);
    }

    private sealed class Walker<TData>(WorkflowDefinition definition, WorkflowDispatcher owner) : Walker
    {
        private readonly WorkflowDefinition<TData> _definition = (WorkflowDefinition<TData>)definition;

        public override async Task RunAsync(
            WorkflowInstanceRecord instance, string nodeId, string? joinKey, int loopIndex, CancellationToken ct)
        {
            var data = JsonSerializer.Deserialize<TData>(instance.DataJson, owner._json)
                ?? throw new InvalidOperationException(
                    $"Workflow instance '{instance.Id}' has a null data document.");

            var cursor = instance.CursorJson is null
                ? new WorkflowCursor()
                : JsonSerializer.Deserialize<WorkflowCursor>(instance.CursorJson, owner._json) ?? new WorkflowCursor();

            var node = _definition.Graph.Nodes.FirstOrDefault(n => n.Id == nodeId)
                ?? throw new InvalidOperationException(
                    $"Node '{nodeId}' is not part of workflow '{_definition.Id}' version {_definition.Version}. "
                    + "Node ids are generated from build order, so a definition changed without a version bump.");

            var plan = new Plan(owner, _definition, instance, joinKey);

            // Only Activity nodes execute code. Every other kind is pure routing, resolved here so
            // no job is ever spent on a decision.
            if (node.Kind == WorkflowNodeKind.Activity)
            {
                await RunActivityAsync(node, data, instance, loopIndex, ct).ConfigureAwait(false);
            }

            await plan.AdvanceAsync(node, data, cursor, loopIndex, ct).ConfigureAwait(false);
        }

        private async Task RunActivityAsync(
            WorkflowNode node, TData data, WorkflowInstanceRecord instance, int loopIndex, CancellationToken ct)
        {
            var binding = _definition.Bindings[node.Id];
            var activity = ActivatorUtilities.CreateInstance(owner._services, binding.ActivityType!);
            var context = new ActivityContext<TData>(
                data, instance.Id, node.Id, instance.DefinitionId, instance.DefinitionVersion)
            {
                LoopIndex = loopIndex,
            };

            await ((IActivity<TData>)activity).ExecuteAsync(context, ct).ConfigureAwait(false);
        }

        /// <summary>Computes the checkpoint and follow-on jobs for one completed node.</summary>
        private sealed class Plan(
            WorkflowDispatcher owner,
            WorkflowDefinition<TData> definition,
            WorkflowInstanceRecord instance,
            string? joinKey)
        {
            private readonly List<JobRecord> _next = [];

            public async Task AdvanceAsync(
                WorkflowNode node, TData data, WorkflowCursor cursor, int loopIndex, CancellationToken ct)
            {
                Compute(node, data, instance, cursor);

                // How the losing side of a checkpoint race gets back to a valid merge without
                // re-running its activity: the produced document is rebased onto whatever the winner
                // left, and the join countdown is recomputed from the winner's cursor.
                var producedJson = JsonSerializer.Serialize(data, owner._json);
                var originalJson = instance.DataJson;

                owner._effects.Remerge = async remergeCt =>
                {
                    var latest = await owner._workflows
                        .GetInstanceAsync(instance.Id, remergeCt).ConfigureAwait(false);
                    if (latest is null)
                    {
                        return false;
                    }

                    var merged = JsonMerge.Apply(
                        JsonNode.Parse(originalJson),
                        JsonNode.Parse(producedJson),
                        JsonNode.Parse(latest.DataJson));

                    var mergedData = merged.Deserialize<TData>(owner._json)
                        ?? throw new InvalidOperationException(
                            $"Merging workflow instance '{instance.Id}' produced a null document.");

                    var latestCursor = latest.CursorJson is null
                        ? new WorkflowCursor()
                        : JsonSerializer.Deserialize<WorkflowCursor>(latest.CursorJson, owner._json)
                          ?? new WorkflowCursor();

                    _next.Clear();
                    owner._effects.Enqueue.Clear();
                    Compute(node, mergedData, latest, latestCursor);
                    return true;
                };

                await Task.CompletedTask.ConfigureAwait(false);
            }

            /// <summary>Routes from <paramref name="node"/> and publishes the resulting checkpoint.</summary>
            private void Compute(
                WorkflowNode node, TData data, WorkflowInstanceRecord basis, WorkflowCursor cursor)
            {
                var joins = new Dictionary<string, WorkflowJoin>(cursor.Joins, StringComparer.Ordinal);
                Route(node, data, joins, loopIndex: 0);

                var completed = _next.Count == 0 && joins.Count == 0;
                var updated = basis with
                {
                    DataJson = JsonSerializer.Serialize(data, owner._json),
                    CursorJson = JsonSerializer.Serialize(
                        new WorkflowCursor { Joins = joins, Completed = completed }, owner._json),
                    State = completed ? WorkflowInstanceState.Completed : WorkflowInstanceState.Running,
                    UpdatedAt = owner._time.GetUtcNow(),
                };

                owner._effects.Checkpoint = new WorkflowCheckpoint
                {
                    Instance = updated,
                    ExpectedRevision = basis.Revision,
                };
                owner._effects.Enqueue.AddRange(_next);
            }

            /// <summary>
            /// Follows routing nodes until real work is scheduled or the flow ends. Routing costs no
            /// jobs: a chain of conditions resolves inside the execution that reached it.
            /// </summary>
            private void Route(WorkflowNode node, TData data, Dictionary<string, WorkflowJoin> joins, int loopIndex)
            {
                var current = Successor(node, data, joins, loopIndex, out var currentJoin);

                while (current is not null)
                {
                    var next = definition.Graph.Nodes.First(n => n.Id == current);
                    switch (next.Kind)
                    {
                        case WorkflowNodeKind.Activity:
                            Schedule(next.Id, currentJoin, loopIndex: 0, delay: null);
                            return;

                        case WorkflowNodeKind.Delay:
                            // A delay is a scheduled job on the node itself: when it comes due, this
                            // dispatcher runs again for that node and simply routes past it.
                            Schedule(next.Id, currentJoin, loopIndex: 0, delay: next.Delay);
                            return;

                        case WorkflowNodeKind.WaitForSignal:
                            // Bookmarks land in 0.3's signal work; until then a wait is a dead end
                            // rather than a silent skip, which would run later steps too early.
                            throw new NotSupportedException(
                                $"Workflow '{definition.Id}' waits for signal '{next.SignalName}', which the "
                                + "engine does not deliver yet. Signals ship with the rest of 0.3.");

                        default:
                            current = Successor(next, data, joins, loopIndex: 0, out currentJoin);
                            break;
                    }
                }
            }

            /// <summary>
            /// The node to continue from after <paramref name="node"/>, opening or closing joins as
            /// the structure requires.
            /// </summary>
            private string? Successor(
                WorkflowNode node, TData data, Dictionary<string, WorkflowJoin> joins, int loopIndex,
                out string? owningJoin)
            {
                owningJoin = joinKey;

                switch (node.Kind)
                {
                    case WorkflowNodeKind.If:
                    {
                        var predicate = definition.Bindings[node.Id].Condition!;
                        var branch = predicate(data) ? node.WhenTrue : node.WhenFalse;
                        // An empty branch is not an error: the flow simply continues after the If.
                        return branch ?? node.Next;
                    }

                    case WorkflowNodeKind.Parallel:
                        return OpenFanOut(node, node.Branches.Select(b => (b, 0)).ToList(), joins, out owningJoin);

                    case WorkflowNodeKind.ForEach:
                    {
                        var items = definition.Bindings[node.Id].Collection!(data);
                        var count = items.Cast<object?>().Count();
                        if (count == 0 || node.Body is null)
                        {
                            // Nothing to iterate: skip the body rather than opening a join that
                            // could never close.
                            return node.Next;
                        }

                        var entries = Enumerable.Range(0, count).Select(i => (node.Body!, i)).ToList();
                        return OpenFanOut(node, entries, joins, out owningJoin);
                    }

                    default:
                        // Activity, Delay, WaitForSignal: plain sequence, and a sequence that ends
                        // may close the join it was running inside.
                        return node.Next ?? CloseJoin(joins, ref owningJoin);
                }
            }

            /// <summary>Schedules every branch and returns null, since this execution now has nothing to continue.</summary>
            private string? OpenFanOut(
                WorkflowNode node, List<(string Entry, int Index)> entries,
                Dictionary<string, WorkflowJoin> joins, out string? owningJoin)
            {
                joins[node.Id] = new WorkflowJoin
                {
                    Remaining = entries.Count,
                    ContinueAt = node.Next,
                    ParentJoin = joinKey,
                };

                foreach (var (entry, index) in entries)
                {
                    ScheduleEntry(entry, node.Id, index);
                }

                owningJoin = null;
                return null;
            }

            /// <summary>
            /// Decrements the join this execution belongs to, cascading outwards while joins close.
            /// </summary>
            /// <remarks>
            /// Concurrent branches race here. They do not need coordinating: each writes its
            /// decrement inside its own checkpoint, so the storage revision decides the winner and
            /// the loser retries the merge against fresh state (§6.2, §11.16).
            /// </remarks>
            private string? CloseJoin(Dictionary<string, WorkflowJoin> joins, ref string? owningJoin)
            {
                while (owningJoin is { } key && joins.TryGetValue(key, out var join))
                {
                    if (join.Remaining > 1)
                    {
                        joins[key] = join with { Remaining = join.Remaining - 1 };
                        owningJoin = null;
                        return null; // siblings still running
                    }

                    joins.Remove(key);
                    owningJoin = join.ParentJoin;
                    if (join.ContinueAt is not null)
                    {
                        return join.ContinueAt;
                    }
                }

                return null;
            }

            private void ScheduleEntry(string nodeId, string joinKeyForBranch, int loopIndex)
            {
                var node = definition.Graph.Nodes.First(n => n.Id == nodeId);
                Schedule(
                    nodeId, joinKeyForBranch, loopIndex,
                    node.Kind == WorkflowNodeKind.Delay ? node.Delay : null);
            }

            private void Schedule(string nodeId, string? joinKeyForBranch, int loopIndex, TimeSpan? delay)
                => _next.Add(owner.BuildActivityJob(instance, nodeId, joinKeyForBranch, loopIndex, delay));
        }
    }
}
