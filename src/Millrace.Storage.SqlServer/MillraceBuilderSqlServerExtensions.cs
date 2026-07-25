using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Millrace.Storage.SqlServer;

namespace Millrace;

/// <summary>Registers the SQL Server provider: <c>services.AddMillrace(w =&gt; w.UseSqlServerStorage(cs))</c>.</summary>
public static class MillraceBuilderSqlServerExtensions
{
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
