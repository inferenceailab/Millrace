namespace Millrace.Storage;

/// <summary>Lifecycle states of a workflow instance (ARCHITECTURE.md §6).</summary>
public enum WorkflowInstanceState
{
    /// <summary>Activities are in flight, or ready to be dispatched.</summary>
    /// <remarks>
    /// Includes a saga that is unwinding: compensations are dispatched as ordinary activities, so an
    /// instance busy undoing its work is still Running. What distinguishes the two is
    /// <c>SagaState.Compensating</c> on the cursor, not this field.
    /// </remarks>
    Running = 0,

    /// <summary>Waiting on a signal bookmark or parked for operator action; no job exists.</summary>
    Suspended = 1,

    /// <summary>Reached the end of its graph. Terminal.</summary>
    Completed = 2,

    /// <summary>Stopped on a failure, with whatever had already been done left in place. Terminal.</summary>
    /// <remarks>
    /// Reached three ways: there was nothing to undo, a step's failure policy said the earlier work
    /// should stand, or an operator abandoned a stalled unwind. The common thread is that partial
    /// work survives — which is exactly what separates this from <see cref="Compensated"/>.
    /// </remarks>
    Failed = 3,

    /// <summary>A saga unwound completely: every step it had finished was compensated. Terminal.</summary>
    /// <remarks>
    /// Kept distinct from <see cref="Failed"/> because the two differ in the only way that matters
    /// to whoever reads the record later — one leaves partial work behind and the other does not.
    /// </remarks>
    Compensated = 4,

    /// <summary>Cancelled before reaching an end of its own. Terminal.</summary>
    Cancelled = 5,
}

/// <summary>
/// A workflow instance as persisted by a provider. The engine (0.3) checkpoints progress by
/// replacing the record under optimistic concurrency (<see cref="Revision"/>).
/// </summary>
public sealed record WorkflowInstanceRecord
{
    /// <summary>Engine-generated identity; opaque to providers, which never mint or order by it.</summary>
    public required WorkflowInstanceId Id { get; init; }

    /// <summary>Which workflow this is an instance of.</summary>
    /// <remarks>Identifies the graph together with <see cref="DefinitionVersion"/>, not alone.</remarks>
    public required string DefinitionId { get; init; }

    /// <summary>The definition version this instance is pinned to.</summary>
    /// <remarks>
    /// An instance always finishes on the version it started with, so a deployment must keep every
    /// version that still has instances in flight registered. Dropping one strands them: the
    /// instance names a graph the process can no longer produce.
    /// </remarks>
    public required int DefinitionVersion { get; init; }

    /// <summary>Where the instance is in its lifecycle.</summary>
    public required WorkflowInstanceState State { get; init; }

    /// <summary>
    /// The serialized <c>TData</c> document. This is a JSON <em>document</em>, not an opaque
    /// string: providers may store it in a native JSON column (jsonb etc.) and must preserve
    /// semantic content — lexical formatting (whitespace, object key order) need not survive.
    /// </summary>
    public required string DataJson { get; init; }

    /// <summary>
    /// Engine cursor state (graph positions); null before the first checkpoint. Same
    /// JSON-document semantics as <see cref="DataJson"/>.
    /// </summary>
    public string? CursorJson { get; init; }

    /// <summary>
    /// Optimistic concurrency token. <c>CreateInstanceAsync</c> stores 1; each successful
    /// <c>UpdateInstanceAsync(expectedRevision)</c> stores <c>expectedRevision + 1</c>.
    /// </summary>
    public required long Revision { get; init; }

    /// <summary>The owning tenant, or null.</summary>
    public string? TenantId { get; init; }

    /// <summary>When the instance was started.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the instance was last checkpointed.</summary>
    /// <remarks>
    /// Moves on every successful checkpoint, so it tracks progress rather than completion — an
    /// instance whose UpdatedAt has stopped advancing while still Running is one worth looking at.
    /// </remarks>
    public required DateTimeOffset UpdatedAt { get; init; }
}
