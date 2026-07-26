using System.Linq.Expressions;

namespace Millrace;

/// <summary>
/// The public enqueue API (ARCHITECTURE.md §5.2). Expressions capture the declared service
/// type, method, and serialized arguments; the target is resolved from the consumer's DI
/// container inside a scope at execution time. Instance methods returning <see cref="Task"/>
/// only in 0.1; keep job signatures stable and pass ids, not entities.
/// </summary>
public interface IJobClient
{
    /// <summary>Fire-and-forget: claimable immediately.</summary>
    ValueTask<JobId> EnqueueAsync<T>(
        Expression<Func<T, Task>> call, EnqueueOptions? options = null, CancellationToken ct = default)
        where T : class;

    /// <summary>Runs after <paramref name="delay"/>.</summary>
    ValueTask<JobId> ScheduleAsync<T>(
        Expression<Func<T, Task>> call, TimeSpan delay, EnqueueOptions? options = null, CancellationToken ct = default)
        where T : class;

    /// <summary>Runs at <paramref name="at"/> (UTC).</summary>
    ValueTask<JobId> ScheduleAsync<T>(
        Expression<Func<T, Task>> call, DateTimeOffset at, EnqueueOptions? options = null, CancellationToken ct = default)
        where T : class;

    /// <summary>
    /// Runs after the parent job succeeds; cancelled (transitively) if the parent dies or is
    /// cancelled.
    /// </summary>
    ValueTask<JobId> ContinueWithAsync<T>(
        JobId parentId, Expression<Func<T, Task>> call, EnqueueOptions? options = null, CancellationToken ct = default)
        where T : class;

    /// <summary>
    /// Creates or updates a cron definition (five-field vixie syntax, UTC). The recurring id is
    /// the definition's identity; <see cref="EnqueueOptions.IdempotencyKey"/> is not supported
    /// here in 0.1 and throws.
    /// </summary>
    ValueTask UpsertRecurringAsync<T>(
        string recurringId, string cron, Expression<Func<T, Task>> call,
        EnqueueOptions? options = null, CancellationToken ct = default)
        where T : class;

    ValueTask RemoveRecurringAsync(string recurringId, CancellationToken ct = default);

    /// <summary>
    /// Cancels a job, returning whether anything was cancelled.
    /// </summary>
    /// <remarks>
    /// The storage surface for this was frozen in 0.1 (§11.8) with the client API deliberately left
    /// until now. Pre-active states cancel outright, along with their transitive continuation
    /// closure. A job already running is asked to stop cooperatively — the flag reaches it through
    /// lease renewal — so a worker about to finish may still succeed, and this returning
    /// <see langword="true"/> is not a promise that the work did not happen.
    /// </remarks>
    ValueTask<bool> CancelAsync(JobId id, CancellationToken ct = default);

    /// <summary>
    /// Runs a job that is waiting out its retry backoff now, returning whether anything changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the case a deployed fix creates: the cause is gone at 09:00 and the retry is not due
    /// until 09:40. This shortens the wait and nothing else — <b>no retry budget is consumed</b>,
    /// because nothing was attempted (§11.32).
    /// </para>
    /// <para>
    /// Distinct from <see cref="RequeueAsync"/>, which mints a <em>new</em> job. That is right for a
    /// job that has finished and wrong here: this one has retries left and a history worth keeping.
    /// </para>
    /// <para>
    /// Returns false for anything not awaiting a retry — already running, terminal, or scheduled
    /// but never yet run — which is the ordinary answer for a stale dashboard button.
    /// </para>
    /// </remarks>
    ValueTask<bool> RunNowAsync(JobId id, CancellationToken ct = default);

    /// <summary>
    /// Fires a recurring definition immediately, without disturbing its schedule.
    /// </summary>
    /// <remarks>
    /// An extra occurrence rather than a rescheduled one: <c>NextFireTime</c> is untouched, so the
    /// normal cadence continues. Returns false if no such definition is registered.
    /// </remarks>
    ValueTask<bool> TriggerRecurringAsync(string recurringId, CancellationToken ct = default);

    /// <summary>
    /// Runs a finished job again, as a new job carrying a link back to it. Returns the new id, or
    /// null if <paramref name="id"/> does not exist.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A new job, not a revived one</b> (§11.19). Every other part of the contract treats a
    /// terminal record as immutable, and rewriting one to make it runnable again would break that
    /// for the sake of a single operator action. The new job records
    /// <see cref="Storage.JobRecord.RequeuedFrom"/> so the two ends stay visible to each other.
    /// </para>
    /// <para>
    /// Three consequences fall out rather than being chosen: the retry budget starts fresh because
    /// the job is new; the idempotency key is carried, so if another active job already holds it the
    /// existing enqueue semantics make this a no-op returning that job's id; and the original's
    /// continuations are <em>not</em> revived, because they were cancelled when it died and nothing
    /// about a new job resurrects them.
    /// </para>
    /// <para>
    /// Requeueing a job that is still running is refused — that is what
    /// <see cref="CancelAsync"/> is for.
    /// </para>
    /// </remarks>
    ValueTask<JobId?> RequeueAsync(JobId id, CancellationToken ct = default);

    /// <summary>
    /// Enqueues a batch in one round trip and one transaction, returning the effective ids
    /// positionally.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All-or-nothing: every job lands or none does. A partially-enqueued fan-out is worse than
    /// none, because the caller cannot tell which half landed and retrying duplicates the rest.
    /// </para>
    /// <para>
    /// Ids are returned by position, and a position whose idempotency key was already held returns
    /// the existing job's id rather than a new one — the same rule a single enqueue follows
    /// (§4.2.6).
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var batch = new JobBatch();
    /// foreach (var id in orderIds)
    /// {
    ///     batch.Enqueue&lt;IEmailSender&gt;(s =&gt; s.SendAsync(id));
    /// }
    ///
    /// await jobs.EnqueueBatchAsync(batch);
    /// </code>
    /// </example>
    ValueTask<IReadOnlyList<JobId>> EnqueueBatchAsync(JobBatch batch, CancellationToken ct = default);
}
