using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Millrace.Storage.PostgreSql;

namespace Millrace;

/// <summary>Registers the PostgreSQL provider: <c>services.AddMillrace(w =&gt; w.UsePostgreSqlStorage(cs))</c>.</summary>
public static class MillraceBuilderPostgreSqlExtensions
{
    /// <summary>Registers PostgreSQL as the storage provider, from a connection string.</summary>
    /// <remarks>
    /// Creates and owns an <see cref="NpgsqlDataSource"/> built from
    /// <paramref name="connectionString"/>. An application that already builds its own — to
    /// configure logging, type mappings or pooling — should use the overload taking a factory
    /// instead, so there is one data source rather than two.
    /// <para>
    /// Registration is last-wins, so calling this after another provider replaces it rather than
    /// conflicting.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is empty.</exception>
    public static MillraceBuilder UsePostgreSqlStorage(
        this MillraceBuilder builder, string connectionString, Action<PostgreSqlStorageOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return builder.UsePostgreSqlStorage(_ => NpgsqlDataSource.Create(connectionString), configure);
    }

    /// <summary>Overload for consumers who manage their own <see cref="NpgsqlDataSource"/>.</summary>
    public static MillraceBuilder UsePostgreSqlStorage(
        this MillraceBuilder builder, Func<IServiceProvider, NpgsqlDataSource> dataSourceFactory,
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
            sp => sp.GetRequiredService<PostgreSqlStorage>(),
            sp => sp.GetRequiredService<PostgreSqlStorage>());
    }
}
