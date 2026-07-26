namespace Millrace.Storage;

/// <summary>
/// An engine-computed state transition applied atomically by the provider — the provider's only
/// duty is to apply it all-or-nothing behind the fence; retry policy, backoff math, and
/// continuation logic never live in providers (ARCHITECTURE.md §4.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Fence.</b> The transition applies iff the job's <c>State == Processing</c>,
/// <c>WorkerId == ExpectedWorkerId</c>, and <c>Attempt == ExpectedAttempt</c>; otherwise
/// <c>ApplyAsync</c> returns <see langword="false"/> and changes nothing. Because every claim
/// increments <see cref="JobRecord.Attempt"/>, the fence is ABA-safe even when the same worker
/// reclaims its own expired job.
/// </para>
/// <para>
/// <b>Atomic effects.</b> State change, <see cref="Enqueue"/> inserts, continuation
/// activation/cancellation, and idempotency-key release commit together or not at all.
/// <see cref="Enqueue"/> inserts follow <c>EnqueueAsync</c> semantics (active-key duplicates
/// skip as no-ops; the Awaiting parent fixup applies); this transition's own terminal key
/// release is visible to its inserts.
/// </para>
/// <para>
/// <b>Continuations.</b> <see cref="CancelContinuations"/> cancels the <em>entire transitive
/// closure</em> of Awaiting descendants (via <see cref="JobRecord.ParentId"/>), each released
/// from its idempotency scope. <see cref="ActivateContinuations"/> is deliberately one level
/// deep — activated children later apply their own transitions; cancelled ones never do.
/// </para>
/// </remarks>
public sealed record JobTransition
{
    /// <summary>The job this transition applies to.</summary>
    public required JobId JobId { get; init; }

    /// <summary>The worker the caller believes holds the claim.</summary>
    /// <remarks>
    /// Part of the fence described above. A worker whose lease expired and whose job was reclaimed
    /// elsewhere fails this check and writes nothing, rather than overwriting the outcome recorded
    /// by whoever holds the job now.
    /// </remarks>
    public required string ExpectedWorkerId { get; init; }

    /// <summary>The attempt number the caller believes it is completing.</summary>
    /// <remarks>
    /// The other half of the fence, and the half that makes it ABA-safe: every claim increments
    /// <see cref="JobRecord.Attempt"/>, so a worker that lost and then reclaimed the same job is
    /// still holding a stale number here and cannot apply an outcome computed for the earlier run.
    /// </remarks>
    public required int ExpectedAttempt { get; init; }

    /// <summary>
    /// <see cref="JobState.Succeeded"/>, <see cref="JobState.Failed"/>,
    /// <see cref="JobState.Dead"/>, <see cref="JobState.Cancelled"/>, or
    /// <see cref="JobState.Enqueued"/> (release — an interrupted job returned to the queue
    /// without consuming retry budget).
    /// </summary>
    public required JobState TargetState { get; init; }

    /// <summary>New <see cref="JobRecord.Failures"/> value (engine-computed).</summary>
    public required int Failures { get; init; }

    /// <summary>Retry activation time when <see cref="TargetState"/> is <see cref="JobState.Failed"/>.</summary>
    public DateTimeOffset? DueAt { get; init; }

    /// <summary>Failure message to record, becoming <see cref="JobRecord.LastError"/>.</summary>
    public string? Error { get; init; }

    /// <summary>Completion time to stamp onto <see cref="JobRecord.FinishedAt"/>.</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>Records inserted atomically with the transition (workflow engine, 0.3).</summary>
    public IReadOnlyList<JobRecord> Enqueue { get; init; } = [];

    /// <summary>
    /// A workflow instance update applied in the same transaction (workflow engine, 0.3).
    /// </summary>
    /// <remarks>
    /// Together with <see cref="Enqueue"/> this makes the §6.2 checkpoint one atom: the instance
    /// advances, the next activity is enqueued, and this job completes, or none of it happens.
    /// A stale revision throws <see cref="MillraceConcurrencyException"/> and changes nothing —
    /// see <see cref="WorkflowCheckpoint"/> for why that is an exception rather than a false return.
    /// </remarks>
    public WorkflowCheckpoint? Checkpoint { get; init; }

    /// <summary>
    /// Bookmarks inserted in the same transaction (workflow engine, 0.3).
    /// </summary>
    /// <remarks>
    /// A wait must appear exactly when the cursor says the instance is waiting. Inserted separately
    /// and it could exist without the cursor knowing (a duplicate wait on retry) or the cursor could
    /// say "suspended" with no bookmark to wake it — an instance parked forever. Same reasoning as
    /// <see cref="Checkpoint"/>, same solution.
    /// </remarks>
    public IReadOnlyList<BookmarkRecord> Bookmarks { get; init; } = [];

    /// <summary>Direct Awaiting children of <see cref="JobId"/> become <see cref="JobState.Enqueued"/>.</summary>
    public bool ActivateContinuations { get; init; }

    /// <summary>The transitive Awaiting-descendant closure of <see cref="JobId"/> becomes <see cref="JobState.Cancelled"/>.</summary>
    public bool CancelContinuations { get; init; }
}
