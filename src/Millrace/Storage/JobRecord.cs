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
    /// <summary>Engine-generated identity, stable for the life of the record.</summary>
    /// <remarks>
    /// Opaque to providers, which persist it verbatim and never mint or order by it. A requeue
    /// produces a new id rather than reusing this one — see <see cref="RequeuedFrom"/>.
    /// </remarks>
    public required JobId Id { get; init; }

    /// <summary>The queue this job is claimed from.</summary>
    /// <remarks>Fixed at enqueue; nothing moves a job between queues afterwards.</remarks>
    public required string Queue { get; init; }

    /// <summary>What to run.</summary>
    /// <remarks>
    /// Captured once, at enqueue. A requeue copies it verbatim rather than re-capturing, so the new
    /// job replays the arguments as they were evaluated originally — not as they would evaluate now.
    /// </remarks>
    public required JobInvocation Invocation { get; init; }

    /// <summary>Where the job is in its lifecycle.</summary>
    /// <remarks>
    /// Providers never compute this. It arrives already decided, through
    /// <see cref="JobTransition"/> or one of the named storage operations.
    /// </remarks>
    public required JobState State { get; init; }

    /// <summary>Higher priority is claimed first; FIFO by enqueue order within equal priority.</summary>
    public int Priority { get; init; }

    /// <summary>When this record was enqueued.</summary>
    /// <remarks>
    /// The record's own creation. A requeued job is stamped when the requeue happened, not when the
    /// job it came from was first enqueued.
    /// </remarks>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Activation time while <see cref="JobState.Scheduled"/> or <see cref="JobState.Failed"/>.</summary>
    public DateTimeOffset? DueAt { get; init; }

    /// <summary>The worker currently holding the claim; null when nothing holds it.</summary>
    /// <remarks>
    /// Also one of the three values a <see cref="JobTransition"/> fences on, which is what stops a
    /// worker that has lost its lease from writing an outcome for a job someone else now owns.
    /// </remarks>
    public string? WorkerId { get; init; }

    /// <summary>When the current claim lapses, after which the job may be reclaimed.</summary>
    /// <remarks>
    /// Pushed forward by heartbeat renewal while the job runs. A worker that dies stops renewing,
    /// and the job becomes claimable once this passes — so the gap between
    /// <see cref="MillraceOptions.LeaseDuration"/> and the heartbeat is how long a crash goes
    /// unnoticed.
    /// </remarks>
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

    /// <summary>The retry policy this job runs under.</summary>
    /// <remarks>
    /// Resolved at enqueue and stored, not read from configuration at failure time. Changing
    /// <see cref="MillraceOptions.DefaultRetry"/> therefore governs jobs enqueued afterwards and
    /// leaves everything already queued on the policy it was created with.
    /// </remarks>
    public required Retry Retry { get; init; }

    /// <summary>The deduplication key, if the enqueue supplied one.</summary>
    /// <remarks>
    /// Scoping and release are described above. Note that the field itself is never cleared: a
    /// terminal job keeps the key it held, and only stops participating in uniqueness.
    /// </remarks>
    public string? IdempotencyKey { get; init; }

    /// <summary>The owning tenant, or null.</summary>
    /// <remarks>
    /// Null is a scope of its own rather than a wildcard — untenanted jobs deduplicate against each
    /// other and never against a tenant's.
    /// </remarks>
    public string? TenantId { get; init; }

    /// <summary>Parent job for continuations (state <see cref="JobState.Awaiting"/>).</summary>
    public JobId? ParentId { get; init; }

    /// <summary>
    /// The job this one was requeued from, if any (§11.19).
    /// </summary>
    /// <remarks>
    /// Requeue mints a new job rather than reviving a terminal one, because every other part of the
    /// contract treats a terminal record as immutable. This is what keeps the two ends visible to
    /// each other in the dashboard. Distinct from <see cref="ParentId"/>, which means a continuation
    /// and carries the <see cref="JobState.Awaiting"/> activation and cancel-cascade semantics —
    /// a requeue inherits neither.
    /// </remarks>
    public JobId? RequeuedFrom { get; init; }

    /// <summary>
    /// W3C <c>traceparent</c> captured where the job was enqueued, so its execution continues that
    /// trace rather than starting a new one (§8).
    /// </summary>
    /// <remarks>
    /// Stored on the record because the enqueue and the execution are separated by a queue, a
    /// process and often a machine — there is no ambient context to inherit at the far end. Null
    /// when nothing was tracing at enqueue time.
    /// </remarks>
    public string? TraceParent { get; init; }

    /// <summary>
    /// The recurring definition that produced this job, if any (§11.26).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing linked a fired job back to its definition, so "did last night's run succeed" had no
    /// join to walk in either direction — §7 promised a last outcome the schema could not source.
    /// This is that link, and it makes the whole fired-job history queryable rather than only the
    /// most recent one.
    /// </para>
    /// <para>
    /// A <em>provenance</em> field, not a live reference: it records which definition produced the
    /// job, and survives the definition being edited or removed. The definition's own identity stays
    /// its id, so a job outlives its origin without dangling.
    /// </para>
    /// </remarks>
    public string? RecurringId { get; init; }

    /// <summary>Message from the most recent failure.</summary>
    /// <remarks>
    /// The latest one only — each failing transition overwrites it, so this is a current-state
    /// field and not a history. It survives into <see cref="JobState.Dead"/>, which is what makes a
    /// dead-lettered job explain itself.
    /// </remarks>
    public string? LastError { get; init; }

    /// <summary>When the job's execution ended, as stamped by the transition that ended it.</summary>
    /// <remarks>Null while the job has not yet reached a conclusion.</remarks>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>Correlation to the owning workflow instance, if any (workflow engine, 0.3).</summary>
    public WorkflowInstanceId? WorkflowInstanceId { get; init; }

    /// <summary>The workflow node this job executes, if any.</summary>
    /// <remarks>
    /// Always set together with <see cref="WorkflowInstanceId"/> — a workflow job names both its
    /// instance and its node, which is the correlation the dashboard reads to show a running
    /// instance's current step.
    /// </remarks>
    public string? ActivityNodeId { get; init; }
}
