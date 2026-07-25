using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Millrace.Invocations;
using Millrace.Storage;

namespace Millrace.Workflows;

/// <summary>
/// Runs one graph step and computes the instance's next state (ARCHITECTURE.md §6.2, §6.3).
/// </summary>
/// <remarks>
/// <para>
/// Arranged around one rule: <b>this class commits nothing.</b> It does the work, computes the new
/// document, cursor, bookmarks and follow-on jobs, and hands them to <see cref="JobSideEffects"/> so
/// the worker commits them with this job's own transition. Any storage write of its own would
/// reintroduce exactly the split §11.16 exists to remove.
/// </para>
/// <para>
/// An activity that throws never checkpoints, so the retry re-runs it against the document it saw
/// the first time. That is what makes at-least-once execution safe to build on.
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

    public Task ExecuteAsync(Guid instanceId, string nodeId, string? joinKey, int loopIndex, CancellationToken ct)
        => RunAsync(instanceId, walker => walker.RunNodeAsync(nodeId, joinKey, loopIndex, ct), ct);

    public Task DeliverSignalAsync(
        Guid instanceId, string signalName, string correlationId, string? payloadJson, CancellationToken ct)
        => RunAsync(instanceId, walker => walker.ResumeAsync(signalName, correlationId, payloadJson, ct), ct);

    public async Task TimeoutSignalAsync(
        Guid instanceId, string signalName, string correlationId, CancellationToken ct)
    {
        // Racing the real signal, resolved by at-most-once consumption: if the signal already
        // arrived the bookmark is gone and there is nothing to do.
        var bookmark = await _workflows.ConsumeBookmarkAsync(signalName, correlationId, ct).ConfigureAwait(false);
        if (bookmark is null)
        {
            return;
        }

        await RunAsync(instanceId, walker => walker.ResumeAsync(signalName, correlationId, null, ct), ct)
            .ConfigureAwait(false);
    }

    private async Task RunAsync(Guid instanceId, Func<Walker, Task> run, CancellationToken ct)
    {
        var id = new WorkflowInstanceId(instanceId);
        var instance = await _workflows.GetInstanceAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Workflow instance '{id}' does not exist. It was deleted while a step was in flight.");

        if (!_registry.TryGet(instance.DefinitionId, instance.DefinitionVersion, out var definition))
        {
            throw new InvalidOperationException(
                $"Workflow '{instance.DefinitionId}' version {instance.DefinitionVersion} is not registered. "
                + "In-flight instances finish on the version they started with, so old versions must stay "
                + "registered until they drain.");
        }

        await run(Walker.Create(definition, this, instance)).ConfigureAwait(false);
    }

    /// <summary>Bridges the non-generic dispatcher to a definition's data type.</summary>
    private abstract class Walker
    {
        public static Walker Create(WorkflowDefinition definition, WorkflowDispatcher owner, WorkflowInstanceRecord instance)
            => (Walker)Activator.CreateInstance(
                typeof(Walker<>).MakeGenericType(definition.DataType), definition, owner, instance)!;

        public abstract Task RunNodeAsync(string nodeId, string? joinKey, int loopIndex, CancellationToken ct);

        public abstract Task ResumeAsync(
            string signalName, string correlationId, string? payloadJson, CancellationToken ct);
    }

    private sealed class Walker<TData> : Walker
    {
        private readonly WorkflowDefinition<TData> _definition;
        private readonly WorkflowDispatcher _owner;
        private readonly WorkflowInstanceRecord _instance;
        private readonly TData _data;
        private readonly WorkflowCursor _cursor;

        public Walker(WorkflowDefinition definition, WorkflowDispatcher owner, WorkflowInstanceRecord instance)
        {
            _definition = (WorkflowDefinition<TData>)definition;
            _owner = owner;
            _instance = instance;
            _data = JsonSerializer.Deserialize<TData>(instance.DataJson, owner._json)
                ?? throw new InvalidOperationException(
                    $"Workflow instance '{instance.Id}' has a null data document.");
            _cursor = instance.CursorJson is null
                ? new WorkflowCursor()
                : JsonSerializer.Deserialize<WorkflowCursor>(instance.CursorJson, owner._json) ?? new WorkflowCursor();
        }

        public override async Task RunNodeAsync(string nodeId, string? joinKey, int loopIndex, CancellationToken ct)
        {
            var node = Node(nodeId);

            // Only Activity nodes execute code. Everything else is routing, resolved without
            // spending a job on a decision.
            if (node.Kind == WorkflowNodeKind.Activity)
            {
                var binding = _definition.Bindings[node.Id];
                var activity = ActivatorUtilities.CreateInstance(_owner._services, binding.ActivityType!);
                var context = new ActivityContext<TData>(
                    _data, _instance.Id, node.Id, _instance.DefinitionId, _instance.DefinitionVersion)
                {
                    LoopIndex = loopIndex,
                };

                await ((IActivity<TData>)activity).ExecuteAsync(context, ct).ConfigureAwait(false);
            }

            Publish(node, joinKey, consumedWait: null);
        }

        public override Task ResumeAsync(
            string signalName, string correlationId, string? payloadJson, CancellationToken ct)
        {
            var key = WorkflowCursor.WaitKey(signalName, correlationId);
            if (!_cursor.Waits.TryGetValue(key, out var wait))
            {
                // Already resumed — a duplicate delivery, or a retry after the checkpoint committed.
                // Doing nothing is correct and keeps resume idempotent.
                return Task.CompletedTask;
            }

            if (payloadJson is not null)
            {
                _definition.Bindings[wait.NodeId].BindPayload?.Invoke(_data, payloadJson);
            }

            Publish(Node(wait.NodeId), wait.JoinKey, consumedWait: key);
            return Task.CompletedTask;
        }

        private WorkflowNode Node(string id)
            => _definition.Graph.Nodes.FirstOrDefault(n => n.Id == id)
               ?? throw new InvalidOperationException(
                   $"Node '{id}' is not part of workflow '{_definition.Id}' version {_definition.Version}. "
                   + "Node ids are generated from build order, so a definition changed without a version bump.");

        /// <summary>Computes the checkpoint for a completed step, and the remerge that rebases it.</summary>
        private void Publish(WorkflowNode from, string? joinKey, string? consumedWait)
        {
            Compute(from, _data, _instance, _cursor, joinKey, consumedWait);

            var producedJson = JsonSerializer.Serialize(_data, _owner._json);
            var originalJson = _instance.DataJson;

            // How the losing side of a checkpoint race gets back to a valid merge without repeating
            // its work: rebase the produced document onto whatever the winner left, and recompute
            // the joins and waits from the winner's cursor.
            _owner._effects.Remerge = async remergeCt =>
            {
                var latest = await _owner._workflows
                    .GetInstanceAsync(_instance.Id, remergeCt).ConfigureAwait(false);
                if (latest is null)
                {
                    return false;
                }

                var merged = JsonMerge.Apply(
                    JsonNode.Parse(originalJson), JsonNode.Parse(producedJson), JsonNode.Parse(latest.DataJson));

                var mergedData = merged.Deserialize<TData>(_owner._json)
                    ?? throw new InvalidOperationException(
                        $"Merging workflow instance '{_instance.Id}' produced a null document.");

                var latestCursor = latest.CursorJson is null
                    ? new WorkflowCursor()
                    : JsonSerializer.Deserialize<WorkflowCursor>(latest.CursorJson, _owner._json)
                      ?? new WorkflowCursor();

                _owner._effects.Enqueue.Clear();
                _owner._effects.Bookmarks.Clear();
                Compute(from, mergedData, latest, latestCursor, joinKey, consumedWait);
                return true;
            };
        }

        private void Compute(
            WorkflowNode from, TData data, WorkflowInstanceRecord basis, WorkflowCursor cursor,
            string? joinKey, string? consumedWait)
        {
            var plan = new Plan(_owner, _definition, basis, cursor, joinKey, consumedWait);
            plan.Route(from, data);

            // A scheduled timeout is not progress, so it does not keep an instance Running: an
            // instance whose only outstanding work is "wait for a signal, or give up at T" is
            // suspended in every sense an operator cares about.
            var suspended = plan.Waits.Count > 0 && plan.Scheduled.Count == 0 && plan.Joins.Count == 0;
            var completed = plan.Scheduled.Count == 0 && plan.Joins.Count == 0 && plan.Waits.Count == 0;

            var updated = basis with
            {
                DataJson = JsonSerializer.Serialize(data, _owner._json),
                CursorJson = JsonSerializer.Serialize(
                    new WorkflowCursor { Joins = plan.Joins, Waits = plan.Waits, Completed = completed },
                    _owner._json),
                State = completed
                    ? WorkflowInstanceState.Completed
                    : suspended
                        ? WorkflowInstanceState.Suspended
                        : WorkflowInstanceState.Running,
                UpdatedAt = _owner._time.GetUtcNow(),
            };

            _owner._effects.Checkpoint = new WorkflowCheckpoint
            {
                Instance = updated,
                ExpectedRevision = basis.Revision,
            };
            _owner._effects.Enqueue.AddRange(plan.Scheduled);
            _owner._effects.Enqueue.AddRange(plan.Timeouts);
            _owner._effects.Bookmarks.AddRange(plan.NewBookmarks);
        }

        /// <summary>Walks the graph from a completed step, collecting what must happen next.</summary>
        private sealed class Plan(
            WorkflowDispatcher owner,
            WorkflowDefinition<TData> definition,
            WorkflowInstanceRecord instance,
            WorkflowCursor cursor,
            string? joinKey,
            string? consumedWait)
        {
            public Dictionary<string, WorkflowJoin> Joins { get; } =
                new(cursor.Joins, StringComparer.Ordinal);

            public Dictionary<string, WorkflowWait> Waits { get; } =
                cursor.Waits
                    .Where(w => consumedWait is null || w.Key != consumedWait)
                    .ToDictionary(w => w.Key, w => w.Value, StringComparer.Ordinal);

            /// <summary>Work that advances the flow.</summary>
            public List<JobRecord> Scheduled { get; } = [];

            /// <summary>
            /// Wait timeouts, kept apart from <see cref="Scheduled"/> because they are not progress:
            /// an instance with only a timeout outstanding is suspended, not running.
            /// </summary>
            public List<JobRecord> Timeouts { get; } = [];

            public List<BookmarkRecord> NewBookmarks { get; } = [];

            public void Route(WorkflowNode from, TData data)
            {
                var current = Successor(from, data, out var currentJoin);

                while (current is not null)
                {
                    var next = definition.Graph.Nodes.First(n => n.Id == current);
                    switch (next.Kind)
                    {
                        case WorkflowNodeKind.Activity:
                            Schedule(next.Id, currentJoin, loopIndex: 0, delay: null);
                            return;

                        case WorkflowNodeKind.Delay:
                            // A scheduled job on the node itself: when it comes due the dispatcher
                            // runs for that node and routes straight past it.
                            Schedule(next.Id, currentJoin, loopIndex: 0, delay: next.Delay);
                            return;

                        case WorkflowNodeKind.WaitForSignal:
                            Park(next, data, currentJoin);
                            return;

                        default:
                            current = Successor(next, data, out currentJoin);
                            break;
                    }
                }
            }

            /// <summary>
            /// Suspends at a wait: records a bookmark and where to resume, and schedules the timeout
            /// if the definition set one. No job exists while waiting — a parked workflow costs a row.
            /// </summary>
            private void Park(WorkflowNode node, TData data, string? currentJoin)
            {
                var correlationId = definition.Bindings[node.Id].Correlate!(data);
                var key = WorkflowCursor.WaitKey(node.SignalName!, correlationId);

                Waits[key] = new WorkflowWait { NodeId = node.Id, JoinKey = currentJoin };
                NewBookmarks.Add(new BookmarkRecord
                {
                    Id = Guid.CreateVersion7(owner._time.GetUtcNow()),
                    InstanceId = instance.Id,
                    SignalName = node.SignalName!,
                    CorrelationId = correlationId,
                    PayloadTypeName = node.PayloadType,
                    CreatedAt = owner._time.GetUtcNow(),
                });

                if (node.Timeout is { } timeout)
                {
                    Timeouts.Add(WorkflowJobFactory.CreateWaitTimeout(
                        instance, node.Id, node.SignalName!, correlationId, timeout, owner._options, owner._time));
                }
            }

            private string? Successor(WorkflowNode node, TData data, out string? owningJoin)
            {
                owningJoin = joinKey;

                switch (node.Kind)
                {
                    case WorkflowNodeKind.If:
                    {
                        var predicate = definition.Bindings[node.Id].Condition!;
                        var arm = predicate(data) ? node.WhenTrue : node.WhenFalse;
                        // An empty arm is not an error: the flow simply continues after the If.
                        return arm ?? node.Next;
                    }

                    case WorkflowNodeKind.Parallel:
                        return OpenFanOut(node, node.Branches.Select(b => (b, 0)).ToList(), out owningJoin);

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
                        return OpenFanOut(node, entries, out owningJoin);
                    }

                    default:
                        // Activity, Delay, WaitForSignal: plain sequence, and a sequence that ends
                        // may close the join it was running inside.
                        return node.Next ?? CloseJoin(ref owningJoin);
                }
            }

            private string? OpenFanOut(WorkflowNode node, List<(string Entry, int Index)> entries, out string? owningJoin)
            {
                Joins[node.Id] = new WorkflowJoin
                {
                    Remaining = entries.Count,
                    ContinueAt = node.Next,
                    ParentJoin = joinKey,
                };

                foreach (var (entry, index) in entries)
                {
                    var target = definition.Graph.Nodes.First(n => n.Id == entry);
                    if (target.Kind == WorkflowNodeKind.WaitForSignal)
                    {
                        // A branch that opens with a wait parks immediately rather than dispatching.
                        Park(target, default!, node.Id);
                        continue;
                    }

                    Schedule(entry, node.Id, index, target.Kind == WorkflowNodeKind.Delay ? target.Delay : null);
                }

                owningJoin = null;
                return null;
            }

            /// <summary>
            /// Decrements the join this step belongs to, cascading outwards while joins close.
            /// </summary>
            /// <remarks>
            /// Concurrent branches need no coordination: each writes its decrement inside its own
            /// checkpoint, so the storage revision picks the winner and the loser rebases (§11.17).
            /// </remarks>
            private string? CloseJoin(ref string? owningJoin)
            {
                while (owningJoin is { } key && Joins.TryGetValue(key, out var join))
                {
                    if (join.Remaining > 1)
                    {
                        Joins[key] = join with { Remaining = join.Remaining - 1 };
                        owningJoin = null;
                        return null; // siblings still running
                    }

                    Joins.Remove(key);
                    owningJoin = join.ParentJoin;
                    if (join.ContinueAt is not null)
                    {
                        return join.ContinueAt;
                    }
                }

                return null;
            }

            private void Schedule(string nodeId, string? branchJoin, int loopIndex, TimeSpan? delay)
                => Scheduled.Add(WorkflowJobFactory.Create(
                    instance, nodeId, branchJoin, loopIndex, delay, owner._options, owner._time));
        }
    }
}
