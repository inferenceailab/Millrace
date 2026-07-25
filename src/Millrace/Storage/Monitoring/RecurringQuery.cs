namespace Millrace.Storage.Monitoring;

/// <summary>
/// Filter and paging arguments for <see cref="IMonitoringStorage.QueryRecurringAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Ordered <c>NextFireTime ASC, Id ASC</c> — soonest first, the opposite of the job and instance
/// queries. A schedule view is read forwards in time ("what runs next"), where a job list is read
/// backwards ("what just happened").
/// </para>
/// <para>
/// There is no state filter because a definition has no state: it is either overdue or not, which
/// the caller derives by comparing <see cref="RecurringSummary.NextFireTime"/> to now.
/// </para>
/// </remarks>
public sealed record RecurringQuery
{
    /// <summary>Default page size when <see cref="Limit"/> is not set to something valid.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Upper bound a provider clamps <see cref="Limit"/> to.</summary>
    public const int MaxLimit = 200;

    /// <summary>Exact queue name, or null for every queue.</summary>
    public string? Queue { get; init; }

    /// <summary>Tenant constraint; defaults to <see cref="TenantFilter.Any"/>.</summary>
    public TenantFilter Tenant { get; init; } = TenantFilter.Any;

    /// <summary>
    /// Opaque continuation from a previous <see cref="Page{T}.NextCursor"/>; null starts at the
    /// first page.
    /// </summary>
    public string? Cursor { get; init; }

    /// <summary>
    /// Maximum rows to return. Values below 1 are treated as <see cref="DefaultLimit"/>; values
    /// above <see cref="MaxLimit"/> are clamped to it. Never an error.
    /// </summary>
    public int Limit { get; init; } = DefaultLimit;
}
