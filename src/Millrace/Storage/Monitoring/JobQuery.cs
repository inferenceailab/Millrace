namespace Millrace.Storage.Monitoring;

/// <summary>
/// Filter and paging arguments for <see cref="IMonitoringStorage.QueryJobsAsync"/>.
/// Frozen with the dashboard API contract (ARCHITECTURE.md §11.12).
/// </summary>
/// <remarks>
/// <para>
/// <b>Filters combine with AND.</b> A property left null — or an empty <see cref="States"/> — adds
/// no constraint. There is no OR and no free-text search; both would push query planning into
/// providers, which §4 P2 keeps out of them.
/// </para>
/// <para>
/// <b>Order is <c>CreatedAt DESC, Id DESC</c></b> — newest first, which is what an operator wants
/// and what makes the keyset stable. Ties are impossible in practice because ids are UUIDv7
/// (§11.8), but the id is part of the key so the order is total regardless.
/// </para>
/// </remarks>
public sealed record JobQuery
{
    /// <summary>Default page size when <see cref="Limit"/> is not set to something valid.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Upper bound a provider clamps <see cref="Limit"/> to.</summary>
    public const int MaxLimit = 200;

    /// <summary>
    /// States to include. Null or empty means any state.
    /// </summary>
    public IReadOnlyList<JobState>? States { get; init; }

    /// <summary>Exact queue name, or null for every queue.</summary>
    public string? Queue { get; init; }

    /// <summary>Tenant constraint; defaults to <see cref="TenantFilter.Any"/>.</summary>
    public TenantFilter Tenant { get; init; } = TenantFilter.Any;

    /// <summary>Inclusive lower bound on <see cref="JobRecord.CreatedAt"/>.</summary>
    public DateTimeOffset? CreatedAfter { get; init; }

    /// <summary>Exclusive upper bound on <see cref="JobRecord.CreatedAt"/>.</summary>
    public DateTimeOffset? CreatedBefore { get; init; }

    /// <summary>
    /// Opaque continuation from a previous <see cref="Page{T}.NextCursor"/>; null starts at the
    /// first page.
    /// </summary>
    /// <remarks>
    /// A cursor is only meaningful alongside the filters that produced it. Presenting one with
    /// different filters is a caller error — see <see cref="IMonitoringStorage.QueryJobsAsync"/>.
    /// </remarks>
    public string? Cursor { get; init; }

    /// <summary>
    /// Maximum rows to return. Values below 1 are treated as <see cref="DefaultLimit"/>; values
    /// above <see cref="MaxLimit"/> are clamped to it. Never an error.
    /// </summary>
    public int Limit { get; init; } = DefaultLimit;
}
