namespace Weft.Storage;

/// <summary>
/// A worker's request to claim jobs. <paramref name="Queues"/> is an unordered filter set —
/// selection order (Priority DESC, then FIFO by enqueue order) applies across the union of the
/// requested queues with no queue precedence.
/// </summary>
public sealed record ClaimRequest(
    string WorkerId,
    IReadOnlyList<string> Queues,
    int MaxCount,
    TimeSpan LeaseDuration);
