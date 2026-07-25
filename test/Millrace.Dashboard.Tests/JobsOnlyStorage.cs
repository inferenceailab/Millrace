using Millrace.Storage;

namespace Millrace.Dashboard.Tests;

/// <summary>
/// A storage provider implementing only the job contract — the "unsupported provider" case §11.14
/// exists to catch. Every member throws: the mount must fail before any of them could be called.
/// </summary>
internal sealed class JobsOnlyStorage : IJobStorage
{
    public StorageCapabilities Capabilities => StorageCapabilities.None;

    public ValueTask<IReadOnlyList<JobId>> EnqueueAsync(IReadOnlyList<JobRecord> jobs, CancellationToken ct)
        => throw new NotSupportedException();

    public ValueTask<IReadOnlyList<JobRecord>> ClaimAsync(ClaimRequest request, CancellationToken ct)
        => throw new NotSupportedException();

    public ValueTask<IReadOnlyList<JobId>> RenewLeasesAsync(
        string workerId, IReadOnlyList<JobId> jobs, TimeSpan lease, CancellationToken ct)
        => throw new NotSupportedException();

    public ValueTask<bool> ApplyAsync(JobTransition transition, CancellationToken ct)
        => throw new NotSupportedException();

    public ValueTask<bool> TryCancelAsync(JobId id, CancellationToken ct)
        => throw new NotSupportedException();

    public ValueTask<JobRecord?> GetJobAsync(JobId id, CancellationToken ct)
        => throw new NotSupportedException();

    public ValueTask<int> ActivateDueJobsAsync(DateTimeOffset now, int batchSize, CancellationToken ct)
        => throw new NotSupportedException();

    public ValueTask UpsertRecurringAsync(RecurringJobRecord record, CancellationToken ct)
        => throw new NotSupportedException();

    public ValueTask<RecurringJobRecord?> GetRecurringAsync(string id, CancellationToken ct)
        => throw new NotSupportedException();

    public ValueTask RemoveRecurringAsync(string id, CancellationToken ct)
        => throw new NotSupportedException();

    public ValueTask<IReadOnlyList<RecurringJobRecord>> GetDueRecurringAsync(
        DateTimeOffset now, int batchSize, CancellationToken ct)
        => throw new NotSupportedException();

    public ValueTask<bool> TryFireRecurringAsync(
        string id, DateTimeOffset expectedFireTime, DateTimeOffset nextFireTime,
        JobRecord job, CancellationToken ct)
        => throw new NotSupportedException();
}
