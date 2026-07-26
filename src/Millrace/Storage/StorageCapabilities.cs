namespace Millrace.Storage;

/// <summary>
/// Optional powers a provider advertises; the engine adapts (ARCHITECTURE.md §4 P3). Batch
/// claiming is not a capability — <see cref="ClaimRequest.MaxCount"/> is always honored
/// best-effort (returning fewer jobs than requested is always conformant).
/// </summary>
[Flags]
public enum StorageCapabilities
{
    /// <summary>No optional powers.</summary>
    /// <remarks>
    /// Not a deficiency: a provider advertising None is fully conformant and loses no guarantee.
    /// The engine simply falls back — adaptive polling instead of pushed wakeups — so the
    /// difference is latency, never correctness.
    /// </remarks>
    None = 0,

    /// <summary>
    /// The provider implements <see cref="IStorageNotifier"/> and pushes queue wakeups
    /// (e.g. Postgres LISTEN/NOTIFY). Without it workers use adaptive polling. Notifications
    /// are a best-effort latency hint — correctness never depends on them.
    /// </summary>
    Notifications = 1,
}
