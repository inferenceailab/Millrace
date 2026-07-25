namespace Millrace.Storage;

/// <summary>
/// A job as persisted by a storage provider. Immutable — providers store snapshots and the
/// engine derives new states via <see cref="JobTransition"/>, never by mutation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Attempt vs Failures.</b> <see cref="Attempt"/> counts executions <em>started</em> — every
/// claim increments it, including lease-expiry reclaims — and exists for transition fencing and
/// poison detection. <see cref="Failures"/> counts recorded failures — only Failed/Dead
/// transitions increment it — and drives retry math. An interruption (crash, deploy, lease
/// loss) therefore never consumes retry budget.
/// </para>
/// <para>
/// <b>Idempotency.</b> Key uniqueness is scoped to (<see cref="TenantId"/>,
/// <see cref="IdempotencyKey"/>) among active (non-terminal) jobs; a null tenant forms its own
/// single scope; uniqueness is global across queues within a scope. Release on terminal
/// transition is a uniqueness-scope rule, not a field mutation — the record keeps its key.
/// </para>
/// </remarks>
public sealed record JobRecord
{
    public required JobId Id { get; init; }

    public required string Queue { get; init; }

    public required JobInvocation Invocation { get; init; }

    public required JobState State { get; init; }

    /// <summary>Higher priority is claimed first; FIFO by enqueue order within equal priority.</summary>
    public int Priority { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Activation time while <see cref="JobState.Scheduled"/> or <see cref="JobState.Failed"/>.</summary>
    public DateTimeOffset? DueAt { get; init; }

    public string? WorkerId { get; init; }

    public DateTimeOffset? LeaseUntil { get; init; }

    /// <summary>Executions started (incremented by every claim). Fencing/poison detection only.</summary>
    public int Attempt { get; init; }

    /// <summary>Recorded failures. Retry math consumes this, never <see cref="Attempt"/>.</summary>
    public int Failures { get; init; }

    /// <summary>
    /// Set by <c>TryCancelAsync</c> on a <see cref="JobState.Processing"/> job. Cooperative: the
    /// worker observes it via lease renewal and cancels; a completing worker may still win with
    /// a fenced terminal transition.
    /// </summary>
    public bool CancelRequested { get; init; }

    public required Retry Retry { get; init; }

    public string? IdempotencyKey { get; init; }

    public string? TenantId { get; init; }

    /// <summary>Parent job for continuations (state <see cref="JobState.Awaiting"/>).</summary>
    public JobId? ParentId { get; init; }

    public string? LastError { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>Correlation to the owning workflow instance, if any (workflow engine, 0.3).</summary>
    public WorkflowInstanceId? WorkflowInstanceId { get; init; }

    public string? ActivityNodeId { get; init; }
}
