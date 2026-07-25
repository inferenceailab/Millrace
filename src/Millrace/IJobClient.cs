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
}
