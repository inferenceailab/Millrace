namespace Millrace.Storage;

/// <summary>
/// The Layer 0 job storage contract (ARCHITECTURE.md §4). Providers implement a small surface
/// with strict atomicity guarantees (§4.2) — everything clever (state machines, retry math,
/// cron) lives in the engine. The conformance kit in <c>Millrace.Storage.Verification</c> enforces
/// every normative statement in these docs; a provider that passes is a supported provider.
/// </summary>
/// <remarks>
/// <para>
/// <b>Time.</b> Providers take a <see cref="TimeProvider"/> at construction and use it for
/// every <c>now</c> comparison (lease expiry, due checks, claims). They never read database
/// server time. Multi-node deployments therefore require clock synchronization; configuration
/// must satisfy <c>LeaseDuration &gt; HeartbeatInterval + clock skew + renewal margin</c>
/// (defaults tolerate ~4 minutes of skew). Excess skew degrades to duplicate execution or early
/// recurring fires — never state corruption; the transition fence holds at any skew.
/// </para>
/// </remarks>
public interface IJobStorage
{
    /// <summary>Optional powers this provider offers, which the engine adapts to.</summary>
    /// <remarks>
    /// Declared rather than probed, and read once — a provider that advertises a capability is
    /// expected to keep offering it for the lifetime of the process, because the engine wires
    /// itself differently depending on the answer rather than re-checking per operation.
    /// </remarks>
    StorageCapabilities Capabilities { get; }

    /// <summary>
    /// Inserts jobs all-or-nothing and returns their effective ids positionally.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Idempotency (§4.2.6).</b> A record whose (<see cref="JobRecord.TenantId"/>,
    /// <see cref="JobRecord.IdempotencyKey"/>) matches an <em>active</em> job inserts nothing;
    /// its position returns the existing job's id. This linearizes against terminal
    /// transitions: each position always returns either the previously-active holder's id or
    /// the new job's id, even when the holder goes terminal concurrently.
    /// </para>
    /// <para>
    /// <b>Continuation fixup.</b> An <see cref="JobState.Awaiting"/> record whose parent is
    /// already terminal resolves inside the same transaction: parent Succeeded ⇒ inserted as
    /// Enqueued; parent Dead/Cancelled ⇒ inserted as Cancelled; parent missing ⇒
    /// <see cref="MillraceParentJobNotFoundException"/> (and the whole batch rolls back). The
    /// insert MUST be mutually serializable with the parent's terminal <see cref="ApplyAsync"/>:
    /// once both commit, in either order, the child is Enqueued/Cancelled — never left
    /// Awaiting. (Relational guidance: lock the parent row during the fixup lookup.)
    /// </para>
    /// <para>
    /// <b>Caller contract</b> (engine-guaranteed; providers may reject with
    /// <see cref="ArgumentException"/>, the conformance kit does not test it): no two records in
    /// one batch share a non-null idempotency key; states are
    /// Scheduled/Enqueued/Awaiting only. A duplicate <see cref="JobId"/> throws and persists
    /// nothing.
    /// </para>
    /// </remarks>
    ValueTask<IReadOnlyList<JobId>> EnqueueAsync(IReadOnlyList<JobRecord> jobs, CancellationToken ct);

    /// <summary>
    /// Exclusively claims up to <see cref="ClaimRequest.MaxCount"/> jobs (§4.2.1–2).
    /// </summary>
    /// <remarks>
    /// A job is claimable iff <c>State == Enqueued || (State == Processing &amp;&amp;
    /// LeaseUntil &lt;= now)</c> — never Scheduled/Failed/Awaiting regardless of DueAt (due
    /// activation happens only via <see cref="ActivateDueJobsAsync"/>). Two concurrent claims
    /// never return the same job. Claiming sets <c>State = Processing</c>,
    /// <see cref="JobRecord.WorkerId"/>, <c>LeaseUntil = now + LeaseDuration</c>, and
    /// increments <see cref="JobRecord.Attempt"/>. Selection order: <c>Priority DESC</c>, then
    /// FIFO by enqueue completion order within equal priority (recommended: provider-local
    /// monotonic insertion sequence), across the union of requested queues; observable only for
    /// non-overlapping claims — interleaving under concurrency is permitted (SKIP LOCKED
    /// semantics). Returning fewer than MaxCount is always conformant.
    /// </remarks>
    ValueTask<IReadOnlyList<JobRecord>> ClaimAsync(ClaimRequest request, CancellationToken ct);

    /// <summary>
    /// Extends leases for in-flight jobs; returns the ids actually renewed.
    /// </summary>
    /// <remarks>
    /// Renewed iff <c>State == Processing &amp;&amp; WorkerId == workerId</c> — LeaseUntil is
    /// NOT consulted: an expired-but-unreclaimed lease IS renewable (resurrection), atomically
    /// per job against concurrent <see cref="ClaimAsync"/> (whichever commits first wins).
    /// Ownership ends only when another claim changes WorkerId/Attempt. The result additionally
    /// excludes ids whose <see cref="JobRecord.CancelRequested"/> is set — the worker
    /// disambiguates via <see cref="GetJobAsync"/>.
    /// </remarks>
    ValueTask<IReadOnlyList<JobId>> RenewLeasesAsync(
        string workerId, IReadOnlyList<JobId> jobs, TimeSpan lease, CancellationToken ct);

    /// <summary>
    /// Applies an engine-computed transition atomically behind the fence (§4.2.3);
    /// <see langword="false"/> = fence rejected, nothing changed.
    /// </summary>
    /// <remarks>
    /// Fence: <c>State == Processing &amp;&amp; WorkerId == ExpectedWorkerId &amp;&amp; Attempt
    /// == ExpectedAttempt</c>. Effects (all-or-nothing): target state with field effects
    /// (terminal ⇒ FinishedAt set, WorkerId/LeaseUntil cleared, idempotency key released from
    /// its uniqueness scope — the field is retained; Failed ⇒ DueAt/LastError/Failures set,
    /// WorkerId/LeaseUntil cleared; Enqueued release ⇒ WorkerId/LeaseUntil cleared);
    /// <see cref="JobTransition.Enqueue"/> inserts (EnqueueAsync semantics, this transition's
    /// key release visible to them); one-level continuation activation; transitive continuation
    /// cancellation; and <see cref="JobTransition.Checkpoint"/>, the workflow instance update.
    /// <para>
    /// <b>Checkpoint ordering.</b> The fence is evaluated first: if it rejects, the call returns
    /// <see langword="false"/> and no checkpoint is attempted — a worker that no longer owns the job
    /// has no business advancing the instance. If the fence holds but
    /// <see cref="WorkflowCheckpoint.ExpectedRevision"/> does not match the stored revision (or the
    /// instance is missing), the whole transition rolls back and
    /// <see cref="MillraceConcurrencyException"/> is thrown: the caller reloads and retries the
    /// merge. The two outcomes stay distinguishable because they demand different reactions.
    /// </para>
    /// </remarks>
    /// <exception cref="MillraceConcurrencyException">
    /// A checkpoint was supplied and its revision was stale, or its instance does not exist.
    /// Nothing changed.
    /// </exception>
    ValueTask<bool> ApplyAsync(JobTransition transition, CancellationToken ct);

    /// <summary>
    /// Cancels a job. Atomic: Scheduled/Enqueued/Failed/Awaiting ⇒ Cancelled (FinishedAt set,
    /// key released, transitive Awaiting-descendant cascade) and returns <see langword="true"/>;
    /// Processing ⇒ sets <see cref="JobRecord.CancelRequested"/> only (state and fence
    /// untouched) and returns <see langword="true"/>; terminal or unknown ⇒
    /// <see langword="false"/>, no mutation. CancelRequested never blocks a fenced
    /// <see cref="ApplyAsync"/> — a completing worker may still win with Succeeded.
    /// </summary>
    ValueTask<bool> TryCancelAsync(JobId id, CancellationToken ct);

    /// <summary>
    /// Makes a job that is waiting out its retry backoff claimable immediately (§11.32).
    /// Atomic: <see cref="JobState.Failed"/> ⇒ <see cref="JobState.Enqueued"/> with
    /// <see cref="JobRecord.DueAt"/> cleared, returning <see langword="true"/>; any other state,
    /// or unknown, ⇒ <see langword="false"/> with no mutation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only <see cref="JobState.Failed"/>.</b> That state means "an attempt failed and the next
    /// one is waiting on a clock", which is the only situation where bringing the job forward is
    /// well defined. A <see cref="JobState.Scheduled"/> job has never run and its due time is the
    /// caller's intent rather than a backoff; a <see cref="JobState.Processing"/> one is already
    /// running; a terminal one needs <c>RequeueAsync</c>, which mints a new job (§11.18).
    /// </para>
    /// <para>
    /// <b>Consumes no retry budget.</b> <see cref="JobRecord.Attempt"/> and
    /// <see cref="JobRecord.Failures"/> are untouched, because nothing was attempted — only the
    /// wait was shortened. An operator who fixes the cause and runs the job now must not find it
    /// dead-lettered a step earlier than it would otherwise have been.
    /// </para>
    /// </remarks>
    ValueTask<bool> TryRunNowAsync(JobId id, CancellationToken ct);

    /// <summary>Reads one job, or null if no such job exists.</summary>
    /// <remarks>
    /// A plain read with no fencing and no lock: the record it returns is a snapshot that may be
    /// stale the moment it arrives. Anything acting on it must go through a fenced operation rather
    /// than trusting what it saw here.
    /// </remarks>
    ValueTask<JobRecord?> GetJobAsync(JobId id, CancellationToken ct);

    /// <summary>
    /// Moves due <see cref="JobState.Scheduled"/>/<see cref="JobState.Failed"/> jobs
    /// (<c>DueAt &lt;= now</c>) to <see cref="JobState.Enqueued"/>, clearing DueAt, in
    /// <c>DueAt ASC</c> order (oldest first; ties by enqueue order), up to
    /// <paramref name="batchSize"/>; returns the number moved. Safe to run concurrently on
    /// every node — each job activates exactly once.
    /// </summary>
    ValueTask<int> ActivateDueJobsAsync(DateTimeOffset now, int batchSize, CancellationToken ct);

    /// <summary>
    /// Single atomic upsert. Insert stores the record as given. Update overwrites
    /// Cron/Queue/Invocation/Retry/Priority/TenantId/UpdatedAt; takes
    /// <see cref="RecurringJobRecord.NextFireTime"/> from the record iff the stored Cron
    /// differs from the record's (else preserves the stored value — the engine always passes a
    /// freshly computed NextFireTime, conditionally unused); always preserves
    /// LastFireTime/CreatedAt.
    /// </summary>
    ValueTask UpsertRecurringAsync(RecurringJobRecord record, CancellationToken ct);

    /// <summary>Reads one recurring definition, or null if none is registered under that id.</summary>
    ValueTask<RecurringJobRecord?> GetRecurringAsync(string id, CancellationToken ct);

    /// <summary>Removes a recurring definition.</summary>
    /// <remarks>
    /// The definition only. Jobs it already fired are ordinary jobs by then and are left alone,
    /// keeping the <see cref="JobRecord.RecurringId"/> that names it — the link is provenance, so a
    /// removed schedule leaves a readable history rather than dangling references or a cascade.
    /// </remarks>
    ValueTask RemoveRecurringAsync(string id, CancellationToken ct);

    /// <summary>
    /// Plain read of records with <c>NextFireTime &lt;= now</c>, ordered
    /// <c>NextFireTime ASC</c> (most overdue first, so a backlog cannot starve old
    /// definitions), up to <paramref name="batchSize"/>.
    /// </summary>
    ValueTask<IReadOnlyList<RecurringJobRecord>> GetDueRecurringAsync(
        DateTimeOffset now, int batchSize, CancellationToken ct);

    /// <summary>
    /// Fenced fire (§4.2.5, strengthened): compare-and-set on (<paramref name="id"/>,
    /// <paramref name="expectedFireTime"/>) advancing NextFireTime to
    /// <paramref name="nextFireTime"/> and setting LastFireTime = expected, inserting
    /// <paramref name="job"/> in the same atomic operation iff the CAS wins. Returns whether
    /// this caller won — exactly one node enqueues each occurrence, with no crash window
    /// between fence and enqueue.
    /// </summary>
    ValueTask<bool> TryFireRecurringAsync(
        string id, DateTimeOffset expectedFireTime, DateTimeOffset nextFireTime,
        JobRecord job, CancellationToken ct);
}
