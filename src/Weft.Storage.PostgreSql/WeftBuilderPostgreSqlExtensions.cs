using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Weft.Storage.PostgreSql;

namespace Weft;

/// <summary>Registers the PostgreSQL provider: <c>services.AddWeft(w =&gt; w.UsePostgreSqlStorage(cs))</c>.</summary>
public static class WeftBuilderPostgreSqlExtensions
{
    public static WeftBuilder UsePostgreSqlStorage(
        this WeftBuilder builder, string connectionString, Action<PostgreSqlStorageOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return builder.UsePostgreSqlStorage(_ => NpgsqlDataSource.Create(connectionString), configure);
    }

    /// <summary>Overload for consumers who manage their own <see cref="NpgsqlDataSource"/>.</summary>
    public static WeftBuilder UsePostgreSqlStorage(
        this WeftBuilder builder, Func<IServiceProvider, NpgsqlDataSource> dataSourceFactory,
        Action<PostgreSqlStorageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(dataSourceFactory);
        var options = new PostgreSqlStorageOptions();
        configure?.Invoke(options);

        builder.Services.Replace(ServiceDescriptor.Singleton(dataSourceFactory));
        builder.Services.Replace(ServiceDescriptor.Singleton(sp => new PostgreSqlStorage(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<TimeProvider>(),
            options)));
        return builder.UseStorage(
            sp => sp.GetRequiredService<PostgreSqlStorage>(),
            sp => sp.GetRequiredService<PostgreSqlStorage>());
    }
}
