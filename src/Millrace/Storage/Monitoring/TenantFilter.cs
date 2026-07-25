namespace Millrace.Storage.Monitoring;

/// <summary>
/// How a monitoring query treats the tenant dimension.
/// </summary>
/// <remarks>
/// <para>
/// A plain <c>string?</c> cannot express this: <see langword="null"/> would have to mean both
/// "every tenant" (an operator's cross-tenant view) and "the untenanted scope" (jobs enqueued with
/// no ambient tenant). Those are different result sets in any deployment that mixes the two, and
/// §11.8 already treats the null tenant as a scope in its own right for idempotency. Leaving the
/// distinction to provider interpretation would guarantee divergence between providers.
/// </para>
/// <para>
/// Single-tenant applications never set a tenant, so for them <see cref="Any"/> and
/// <see cref="Untenanted"/> select the same rows; they can use either.
/// </para>
/// </remarks>
public readonly record struct TenantFilter
{
    private TenantFilter(bool isConstrained, string? tenantId)
    {
        IsConstrained = isConstrained;
        TenantId = tenantId;
    }

    /// <summary>No tenant constraint — rows from every tenant, including untenanted rows.</summary>
    public static TenantFilter Any => new(isConstrained: false, tenantId: null);

    /// <summary>Only rows with no tenant (<c>TenantId IS NULL</c>).</summary>
    public static TenantFilter Untenanted => new(isConstrained: true, tenantId: null);

    /// <summary>Only rows belonging to <paramref name="tenantId"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="tenantId"/> is null or whitespace.</exception>
    public static TenantFilter For(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return new TenantFilter(isConstrained: true, tenantId);
    }

    /// <summary>
    /// Whether the query constrains the tenant at all. When <see langword="false"/>,
    /// <see cref="TenantId"/> is meaningless.
    /// </summary>
    public bool IsConstrained { get; }

    /// <summary>
    /// The tenant to match when <see cref="IsConstrained"/> is <see langword="true"/>;
    /// <see langword="null"/> then means the untenanted scope.
    /// </summary>
    public string? TenantId { get; }
}
