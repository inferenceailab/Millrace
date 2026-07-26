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

    public Task FailActivityAsync(Guid instanceId, string nodeId, CancellationToken ct)
        => RunAsync(instanceId, walker => walker.FailAsync(nodeId), ct);

    public Task CompensateAsync(Guid instanceId, string sagaId, string stepNodeId, CancellationToken ct)
        => RunAsync(instanceId, walker => walker.CompensateAsync(sagaId, stepNodeId, ct), ct);

    public Task RecoverCompensationAsync(Guid instanceId, CompensationRecovery action, CancellationToken ct)
        => RunAsync(instanceId, walker => walker.RecoverAsync(action, ct), ct);

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

        public abstract Task FailAsync(string nodeId);

        public abstract Task CompensateAsync(string sagaId, string stepNodeId, CancellationToken ct);

        public abstract Task RecoverAsync(CompensationRecovery action, CancellationToken ct);
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

        /// <summary>
        /// An activity exhausted its retries: unwind its saga, or record the instance as failed.
        /// </summary>
        public override Task FailAsync(string nodeId)
        {
            var node = Node(nodeId);
            var sagas = new Dictionary<string, SagaState>(_cursor.Sagas, StringComparer.Ordinal);

            // The step's own policy is consulted before the saga's default, and only when the saga
            // is not already unwinding — once compensation has started, a failure is a *failed
            // compensation*, and the policy on the step that originally failed has nothing to say
            // about it (§11.28).
            var unwinding = node.SagaId is { } current
                && sagas.TryGetValue(current, out var running)
                && running.Compensating;

            if (!unwinding && node.OnFailure is StepFailurePolicy.Suspend or StepFailurePolicy.Terminate)
            {
                // Suspend leaves the completed steps completed, so an operator can still start the
                // unwind. Terminate says the earlier work should stand — undoing it would be worse
                // than leaving it — so nothing is scheduled either way.
                Checkpoint(
                    node.OnFailure == StepFailurePolicy.Suspend
                        ? WorkflowInstanceState.Suspended
                        : WorkflowInstanceState.Failed,
                    joins: new Dictionary<string, WorkflowJoin>(_cursor.Joins, StringComparer.Ordinal),
                    waits: new Dictionary<string, WorkflowWait>(_cursor.Waits, StringComparer.Ordinal),
                    sagas: sagas,
                    scheduled: [],
                    bookmarks: []);

                return Task.CompletedTask;
            }

            if (node.SagaId is { } sagaId
                && sagas.TryGetValue(sagaId, out var saga)
                && !saga.Compensating
                && saga.Completed.Count > 0)
            {
                // Unwind from the most recent completed step backwards. Marked compensating first,
                // so a second failure arriving later cannot restart the unwind from the top.
                sagas[sagaId] = saga with { Compensating = true };
                Checkpoint(
                    WorkflowInstanceState.Running,
                    joins: new Dictionary<string, WorkflowJoin>(_cursor.Joins, StringComparer.Ordinal),
                    waits: new Dictionary<string, WorkflowWait>(_cursor.Waits, StringComparer.Ordinal),
                    sagas: sagas,
                    scheduled: [WorkflowJobFactory.CreateCompensation(
                        _instance, sagaId, saga.Completed[^1], _owner._options, _owner._time)],
                    bookmarks: []);

                return Task.CompletedTask;
            }

            // A failure while already compensating is a compensation that failed. Parking rather
            // than forcing a terminal state is deliberate: a half-undone saga is exactly where an
            // operator should look before anything else happens to it.
            var compensationFailed = node.SagaId is { } id
                && sagas.TryGetValue(id, out var open)
                && open.Compensating;

            // This saga had nothing of its own to undo, but a saga around it may. Its failure is
            // still the enclosing saga's failure, so the unwind continues outward rather than
            // stopping at the innermost one (§11.29).
            if (!compensationFailed && node.SagaId is { } failed)
            {
                sagas.Remove(failed);
                if (TryPropagateOutward(failed, sagas, out var outward))
                {
                    Checkpoint(
                        WorkflowInstanceState.Running,
                        joins: new Dictionary<string, WorkflowJoin>(_cursor.Joins, StringComparer.Ordinal),
                        waits: new Dictionary<string, WorkflowWait>(_cursor.Waits, StringComparer.Ordinal),
                        sagas: sagas,
                        scheduled: outward,
                        bookmarks: []);

                    return Task.CompletedTask;
                }
            }

            // Otherwise there was nothing to undo, and recording the failure is what keeps a dead
            // activity from leaving the instance Running forever.
            Checkpoint(
                compensationFailed ? WorkflowInstanceState.Suspended : WorkflowInstanceState.Failed,
                joins: new Dictionary<string, WorkflowJoin>(_cursor.Joins, StringComparer.Ordinal),
                waits: new Dictionary<string, WorkflowWait>(_cursor.Waits, StringComparer.Ordinal),
                sagas: sagas,
                scheduled: [],
                bookmarks: []);

            return Task.CompletedTask;
        }

        /// <summary>Runs one compensation and schedules the next one backwards.</summary>
        public override async Task CompensateAsync(string sagaId, string stepNodeId, CancellationToken ct)
        {
            var sagas = new Dictionary<string, SagaState>(_cursor.Sagas, StringComparer.Ordinal);
            if (!sagas.TryGetValue(sagaId, out var saga) || saga.Completed.Count == 0)
            {
                return; // already unwound — a duplicate delivery or a retry after the checkpoint
            }

            // The step being undone is itself a saga: hand the unwind inward rather than looking for
            // a compensation activity it does not have. The outer's entry is consumed now, so when
            // the inner finishes it resumes the outer one step further back (§11.35).
            if (Node(stepNodeId).Kind == WorkflowNodeKind.Saga
                && sagas.TryGetValue(stepNodeId, out var nested)
                && nested.Completed.Count > 0)
            {
                sagas[sagaId] = saga with
                {
                    Completed = saga.Completed.Take(saga.Completed.Count - 1).ToList(),
                    Compensating = true,
                };
                sagas[stepNodeId] = nested with { Compensating = true };

                Checkpoint(
                    WorkflowInstanceState.Running,
                    joins: new Dictionary<string, WorkflowJoin>(_cursor.Joins, StringComparer.Ordinal),
                    waits: new Dictionary<string, WorkflowWait>(_cursor.Waits, StringComparer.Ordinal),
                    sagas: sagas,
                    scheduled: [WorkflowJobFactory.CreateCompensation(
                        _instance, stepNodeId, nested.Completed[^1], _owner._options, _owner._time)],
                    bookmarks: []);

                return;
            }

            if (_definition.Compensations.TryGetValue(stepNodeId, out var compensationType))
            {
                var activity = ActivatorUtilities.CreateInstance(_owner._services, compensationType);
                var context = new ActivityContext<TData>(
                    _data, _instance.Id, stepNodeId, _instance.DefinitionId, _instance.DefinitionVersion);
                await ((IActivity<TData>)activity).ExecuteAsync(context, ct).ConfigureAwait(false);
            }

            var remaining = saga.Completed.Take(saga.Completed.Count - 1).ToList();
            sagas[sagaId] = saga with { Completed = remaining, Compensating = true };

            var next = remaining.Count > 0
                ? new List<JobRecord>
                {
                    WorkflowJobFactory.CreateCompensation(
                        _instance, sagaId, remaining[^1], _owner._options, _owner._time),
                }
                : [];

            if (next.Count == 0)
            {
                // Undone completely. If something encloses this saga, its failure is now the outer
                // saga's failure and the unwind carries on there — the instance is only compensated
                // once the outermost one has nothing left (§11.29).
                sagas.Remove(sagaId);
                if (TryPropagateOutward(sagaId, sagas, out var outward))
                {
                    next = outward;
                }
            }

            Checkpoint(
                next.Count == 0 ? WorkflowInstanceState.Compensated : WorkflowInstanceState.Running,
                joins: new Dictionary<string, WorkflowJoin>(_cursor.Joins, StringComparer.Ordinal),
                waits: new Dictionary<string, WorkflowWait>(_cursor.Waits, StringComparer.Ordinal),
                sagas: sagas,
                scheduled: next,
                bookmarks: []);
        }

        /// <summary>
        /// Moves a suspended unwind forward on an operator's instruction (§11.30).
        /// </summary>
        /// <remarks>
        /// Deliberately expressed as a checkpoint like every other transition, rather than as a
        /// special repair path: the instance is advanced by the same optimistic revision write that
        /// the engine uses, so an operator clicking twice, or two operators clicking at once, loses
        /// the race exactly as a duplicate job delivery would.
        /// </remarks>
        public override Task RecoverAsync(CompensationRecovery action, CancellationToken ct)
        {
            var sagas = new Dictionary<string, SagaState>(_cursor.Sagas, StringComparer.Ordinal);

            // Only a saga that is mid-unwind can be recovered. A suspended instance that is not
            // compensating was parked by a Suspend policy (§11.28) and has nothing to resume from
            // here — which is why this reports false rather than inventing a state to move to.
            if (_instance.State != WorkflowInstanceState.Suspended
                || sagas.FirstOrDefault(s => s.Value.Compensating) is not { Value.Completed.Count: > 0 } entry)
            {
                return Task.CompletedTask;
            }

            var (sagaId, saga) = (entry.Key, entry.Value);
            var joins = new Dictionary<string, WorkflowJoin>(_cursor.Joins, StringComparer.Ordinal);
            var waits = new Dictionary<string, WorkflowWait>(_cursor.Waits, StringComparer.Ordinal);

            switch (action)
            {
                case CompensationRecovery.Abandon:
                    // The remaining steps stay done, and the saga state is dropped so nothing later
                    // mistakes this for an unwind still in progress.
                    sagas.Remove(sagaId);
                    Checkpoint(WorkflowInstanceState.Failed, joins, waits, sagas, scheduled: [], bookmarks: []);
                    return Task.CompletedTask;

                case CompensationRecovery.Skip:
                {
                    // Drops the step without running its compensation: the operator is asserting it
                    // is undone, which the engine cannot verify and must not pretend to.
                    var remaining = saga.Completed.Take(saga.Completed.Count - 1).ToList();
                    if (remaining.Count == 0)
                    {
                        sagas.Remove(sagaId);
                        Checkpoint(
                            WorkflowInstanceState.Compensated, joins, waits, sagas,
                            scheduled: [], bookmarks: []);
                        return Task.CompletedTask;
                    }

                    sagas[sagaId] = saga with { Completed = remaining };
                    Checkpoint(
                        WorkflowInstanceState.Running, joins, waits, sagas,
                        scheduled: [WorkflowJobFactory.CreateCompensation(
                            _instance, sagaId, remaining[^1], _owner._options, _owner._time)],
                        bookmarks: []);
                    return Task.CompletedTask;
                }

                default:
                    // Retry: the same step again, with the saga state untouched. A fresh job, so it
                    // gets a fresh retry budget — the previous one is spent, and refusing to reset it
                    // would make the button useless the moment it was needed.
                    Checkpoint(
                        WorkflowInstanceState.Running, joins, waits, sagas,
                        scheduled: [WorkflowJobFactory.CreateCompensation(
                            _instance, sagaId, saga.Completed[^1], _owner._options, _owner._time)],
                        bookmarks: []);
                    return Task.CompletedTask;
            }
        }

        /// <summary>Publishes a checkpoint with an explicit state, for the paths that do not route.</summary>
        private void Checkpoint(
            WorkflowInstanceState state,
            Dictionary<string, WorkflowJoin> joins,
            Dictionary<string, WorkflowWait> waits,
            Dictionary<string, SagaState> sagas,
            List<JobRecord> scheduled,
            List<BookmarkRecord> bookmarks)
        {
            var updated = _instance with
            {
                DataJson = JsonSerializer.Serialize(_data, _owner._json),
                CursorJson = JsonSerializer.Serialize(
                    new WorkflowCursor
                    {
                        Joins = joins,
                        Waits = waits,
                        Sagas = sagas,
                        Completed = state == WorkflowInstanceState.Completed,
                    },
                    _owner._json),
                State = state,
                UpdatedAt = _owner._time.GetUtcNow(),
            };

            _owner._effects.Checkpoint = new WorkflowCheckpoint
            {
                Instance = updated,
                ExpectedRevision = _instance.Revision,
            };
            _owner._effects.Enqueue.AddRange(scheduled);
            _owner._effects.Bookmarks.AddRange(bookmarks);
        }

        /// <summary>
        /// Whether <paramref name="nodeSagaId"/> is <paramref name="sagaId"/> or sits inside it.
        /// </summary>
        /// <remarks>
        /// A saga is finished when nothing it owns is still scheduled, and work inside a nested saga
        /// is still work the outer one owns. Comparing ids directly was equivalent while nesting was
        /// impossible; with it, an outer saga was forgotten the moment control entered the inner
        /// one, and its steps then had nothing left to undo them.
        /// </remarks>
        private bool WithinSaga(string? nodeSagaId, string sagaId)
        {
            for (var id = nodeSagaId; id is not null; id = Node(id).SagaId)
            {
                if (string.Equals(id, sagaId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Drops a saga's record, and any nested saga records kept underneath it.
        /// </summary>
        /// <remarks>
        /// A nested saga declared <see cref="NestedSagaPolicy.Unwind"/> outlives its own commit so
        /// the enclosing saga can replay it. Once the enclosing saga commits, nothing can reach
        /// either of them again, and a record left behind would be a saga the engine believes is
        /// still open.
        /// </remarks>
        private void Forget(string sagaId, Dictionary<string, SagaState> sagas)
        {
            sagas.Remove(sagaId);

            foreach (var nested in _definition.Graph.Nodes
                .Where(n => n.Kind == WorkflowNodeKind.Saga && n.SagaId == sagaId))
            {
                Forget(nested.Id, sagas);
            }
        }

        /// <summary>
        /// Continues the unwind in the enclosing saga, if there is one with anything left to undo.
        /// </summary>
        /// <remarks>
        /// The single place §11.29's "propagates outward" is implemented, and it serves both routes
        /// to it: a nested saga that failed and finished undoing itself, and a nested saga being
        /// replayed because the outer one failed. Innermost-first falls out of it either way.
        /// </remarks>
        private bool TryPropagateOutward(
            string finishedSagaId, Dictionary<string, SagaState> sagas, out List<JobRecord> scheduled)
        {
            scheduled = [];

            if (Node(finishedSagaId).SagaId is not { } enclosing
                || !sagas.TryGetValue(enclosing, out var outer)
                || outer.Completed.Count == 0)
            {
                return false;
            }

            sagas[enclosing] = outer with { Compensating = true };
            scheduled =
            [
                WorkflowJobFactory.CreateCompensation(
                    _instance, enclosing, outer.Completed[^1], _owner._options, _owner._time),
            ];

            return true;
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

            // Record the step before routing: a saga has to know what it did in order to undo it,
            // and the failure that triggers the undo arrives long after this execution is gone.
            if (from.SagaId is { } stepSaga && from.Kind == WorkflowNodeKind.Activity)
            {
                plan.RecordSagaStep(stepSaga, from.Id);
            }

            plan.Route(from, data);

            // A saga whose steps have all run has nothing left to undo, so it stops being tracked —
            // unless it is nested and declared Unwind, in which case "nothing left to undo" is only
            // true until the saga around it fails (§11.35).
            if (from.SagaId is { } finishedSaga
                && !plan.Scheduled.Any(j => WithinSaga(
                    _definition.Graph.Nodes.First(n => n.Id == j.ActivityNodeId).SagaId,
                    finishedSaga)))
            {
                var sagaNode = Node(finishedSaga);
                if (sagaNode.SagaId is { } enclosing && sagaNode.Nesting == NestedSagaPolicy.Unwind)
                {
                    // Kept, so the outer has something to replay, and recorded as a step of the
                    // outer so the replay happens in the right place in the reverse order.
                    plan.RecordSagaStep(enclosing, finishedSaga);
                }
                else
                {
                    Forget(finishedSaga, plan.Sagas);
                }
            }

            // A scheduled timeout is not progress, so it does not keep an instance Running: an
            // instance whose only outstanding work is "wait for a signal, or give up at T" is
            // suspended in every sense an operator cares about.
            var suspended = plan.Waits.Count > 0 && plan.Scheduled.Count == 0 && plan.Joins.Count == 0;
            var completed = plan.Scheduled.Count == 0 && plan.Joins.Count == 0 && plan.Waits.Count == 0;

            var updated = basis with
            {
                DataJson = JsonSerializer.Serialize(data, _owner._json),
                CursorJson = JsonSerializer.Serialize(
                    new WorkflowCursor
                    {
                        Joins = plan.Joins, Waits = plan.Waits, Sagas = plan.Sagas, Completed = completed,
                    },
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

            public Dictionary<string, SagaState> Sagas { get; } =
                new(cursor.Sagas, StringComparer.Ordinal);

            public List<BookmarkRecord> NewBookmarks { get; } = [];

            /// <summary>Appends a completed step to its saga's undo list.</summary>
            public void RecordSagaStep(string sagaId, string stepNodeId)
            {
                var saga = Sagas.GetValueOrDefault(sagaId) ?? new SagaState();
                if (saga.Compensating || saga.Completed.Contains(stepNodeId))
                {
                    return; // a retry re-running a step must not record it twice
                }

                Sagas[sagaId] = saga with { Completed = [.. saga.Completed, stepNodeId] };
            }

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

                    case WorkflowNodeKind.Saga:
                        // Entering a saga is pure routing: the body is an ordinary sequence whose
                        // steps happen to be recorded as they complete.
                        return node.Body ?? node.Next;

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
