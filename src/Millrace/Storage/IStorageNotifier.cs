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
    /// <summary>Streams wakeup hints for <paramref name="queues"/> until cancelled.</summary>
    /// <remarks>
    /// A latency optimisation and nothing more. Signals may be dropped, duplicated, or arrive for a
    /// queue with nothing claimable — a worker treats each as "look now", never as "there is work".
    /// A provider whose channel drops every signal is still correct; its workers just fall back to
    /// the poll interval.
    /// </remarks>
    IAsyncEnumerable<QueueSignal> ListenAsync(IReadOnlySet<string> queues, CancellationToken ct);
}
