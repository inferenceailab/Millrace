namespace Millrace.Storage.Monitoring;

/// <summary>
/// One keyset page of a monitoring query (ARCHITECTURE.md §11.12).
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no total count, deliberately.</b> Counting a filtered job list is the expensive part
/// of the query, and on a table whose rows change state continuously the number is stale before it
/// renders. Aggregates come from <see cref="IMonitoringStorage.GetStatisticsAsync"/> instead.
/// The consequence is binding on every client: no page numbers, no "showing 1–50 of 4,102" —
/// next/previous only.
/// </para>
/// <para>
/// <b>The cursor is opaque.</b> Its encoding is provider-defined and callers must round-trip it
/// unmodified. Clients must never parse, construct, or persist meaning into one.
/// </para>
/// </remarks>
/// <typeparam name="T">The summary projection being paged.</typeparam>
public sealed record Page<T>
{
    /// <summary>
    /// The rows in this page, in the query's defined order. Never null; empty when nothing matched.
    /// May contain fewer than the requested limit even when further pages exist.
    /// </summary>
    public required IReadOnlyList<T> Items { get; init; }

    /// <summary>
    /// Continuation token for the next page, or <see langword="null"/> when this is the last page.
    /// </summary>
    /// <remarks>
    /// Non-null does not promise a non-empty next page: rows matching the filter can go terminal or
    /// be removed between calls. Callers page until this is <see langword="null"/>.
    /// </remarks>
    public string? NextCursor { get; init; }
}
