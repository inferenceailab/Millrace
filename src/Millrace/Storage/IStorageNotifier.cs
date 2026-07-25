namespace Millrace.Storage;

/// <summary>A wakeup hint that jobs may be claimable on <paramref name="Queue"/>.</summary>
public readonly record struct QueueSignal(string Queue);

/// <summary>
/// Optional push channel (ARCHITECTURE.md §4.1). Providers with a native mechanism
/// (e.g. Postgres LISTEN/NOTIFY) implement this and advertise
/// <see cref="StorageCapabilities.Notifications"/>; otherwise workers poll adaptively.
/// Signals are best-effort — they may be dropped or duplicated; correctness never depends on
/// them, only wakeup latency.
/// </summary>
public interface IStorageNotifier
{
    IAsyncEnumerable<QueueSignal> ListenAsync(IReadOnlySet<string> queues, CancellationToken ct);
}
