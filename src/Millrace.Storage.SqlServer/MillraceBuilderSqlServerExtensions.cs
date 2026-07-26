using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Millrace.Storage.SqlServer;

namespace Millrace;

/// <summary>Registers the SQL Server provider: <c>services.AddMillrace(w =&gt; w.UseSqlServerStorage(cs))</c>.</summary>
public static class MillraceBuilderSqlServerExtensions
{
    /// <summary>Registers SQL Server as the storage provider.</summary>
    /// <remarks>
    /// Registration is last-wins, so calling this after another provider replaces it rather than
    /// conflicting — which is what lets a test host override whatever the composition root
    /// configured.
    /// <para>
    /// The provider advertises no notification capability, because SQL Server has no
    /// <c>LISTEN/NOTIFY</c>; workers fall back to adaptive polling, and wakeup latency rather than
    /// correctness is what differs from PostgreSQL.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is empty.</exception>
    public static MillraceBuilder UseSqlServerStorage(
        this MillraceBuilder builder, string connectionString, Action<SqlServerStorageOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var options = new SqlServerStorageOptions();
        configure?.Invoke(options);

        builder.Services.Replace(ServiceDescriptor.Singleton(sp => new SqlServerStorage(
            connectionString, sp.GetRequiredService<TimeProvider>(), options)));

        return builder.UseStorage(
            sp => sp.GetRequiredService<SqlServerStorage>(),
            sp => sp.GetRequiredService<SqlServerStorage>(),
            sp => sp.GetRequiredService<SqlServerStorage>());
    }
}
