namespace Millrace.Storage.Monitoring;

/// <summary>
/// Filter and paging arguments for <see cref="IMonitoringStorage.QueryInstancesAsync"/>.
/// Frozen with the dashboard API contract (ARCHITECTURE.md §11.12).
/// </summary>
/// <remarks>
/// Same rules as <see cref="JobQuery"/>: filters combine with AND, null or empty adds no
/// constraint, and the order is <c>CreatedAt DESC, Id DESC</c>.
/// </remarks>
public sealed record InstanceQuery
{
    /// <summary>Default page size when <see cref="Limit"/> is not set to something valid.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Upper bound a provider clamps <see cref="Limit"/> to.</summary>
    public const int MaxLimit = 200;

    /// <summary>States to include. Null or empty means any state.</summary>
    public IReadOnlyList<WorkflowInstanceState>? States { get; init; }

    /// <summary>Exact definition id, or null for every definition.</summary>
    public string? DefinitionId { get; init; }

    /// <summary>
    /// Definition version, or null for every version. Ignored unless <see cref="DefinitionId"/> is
    /// also set — a version number means nothing on its own.
    /// </summary>
    public int? DefinitionVersion { get; init; }

    /// <summary>Tenant constraint; defaults to <see cref="TenantFilter.Any"/>.</summary>
    public TenantFilter Tenant { get; init; } = TenantFilter.Any;

    /// <summary>Inclusive lower bound on <see cref="WorkflowInstanceRecord.CreatedAt"/>.</summary>
    public DateTimeOffset? CreatedAfter { get; init; }

    /// <summary>Exclusive upper bound on <see cref="WorkflowInstanceRecord.CreatedAt"/>.</summary>
    public DateTimeOffset? CreatedBefore { get; init; }

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
