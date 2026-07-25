namespace Millrace.Storage;

/// <summary>
/// Optional powers a provider advertises; the engine adapts (ARCHITECTURE.md §4 P3). Batch
/// claiming is not a capability — <see cref="ClaimRequest.MaxCount"/> is always honored
/// best-effort (returning fewer jobs than requested is always conformant).
/// </summary>
[Flags]
public enum StorageCapabilities
{
    None = 0,

    /// <summary>
    /// The provider implements <see cref="IStorageNotifier"/> and pushes queue wakeups
    /// (e.g. Postgres LISTEN/NOTIFY). Without it workers use adaptive polling. Notifications
    /// are a best-effort latency hint — correctness never depends on them.
    /// </summary>
    Notifications = 1,
}
