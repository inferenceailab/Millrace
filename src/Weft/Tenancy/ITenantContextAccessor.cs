namespace Weft.Tenancy;

/// <summary>
/// Captures the ambient tenant at enqueue/start time and restores it inside worker execution
/// scopes, so consumer code (and its data filters) sees the right tenant (ARCHITECTURE.md §8).
/// Single-tenant applications never touch this — the default implementation flows
/// <see langword="null"/> and jobs carry no tenant id.
/// </summary>
public interface ITenantContextAccessor
{
    /// <summary>The current ambient tenant id, or <see langword="null"/> when single-tenant.</summary>
    string? TenantId { get; }

    /// <summary>
    /// Makes <paramref name="tenantId"/> ambient until the returned scope is disposed.
    /// Workers call this before resolving a job's target so execution observes the tenant the
    /// job was enqueued under.
    /// </summary>
    IDisposable BeginScope(string? tenantId);
}

/// <summary>Default <see cref="ITenantContextAccessor"/>: an <see cref="AsyncLocal{T}"/> ambient value.</summary>
public sealed class AmbientTenantContextAccessor : ITenantContextAccessor
{
    private static readonly AsyncLocal<string?> Current = new();

    public string? TenantId => Current.Value;

    public IDisposable BeginScope(string? tenantId)
    {
        var previous = Current.Value;
        Current.Value = tenantId;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Current.Value = previous;
            }
        }
    }
}
