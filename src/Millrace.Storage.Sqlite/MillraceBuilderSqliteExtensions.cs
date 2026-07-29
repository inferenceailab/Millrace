using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Millrace.Storage.Sqlite;

namespace Millrace;

/// <summary>Registers the SQLite provider: <c>services.AddMillrace(w =&gt; w.UseSqliteStorage(cs))</c>.</summary>
public static class MillraceBuilderSqliteExtensions
{
    /// <summary>Registers SQLite as the storage provider, from a connection string.</summary>
    /// <remarks>
    /// <para>
    /// Takes a connection string rather than a connection: SQLite connections are cheap and pooled,
    /// and the provider opens a short one per operation. It does hold a single connection open for
    /// its lifetime so that an in-memory database survives between operations — see
    /// <c>SqliteStorage</c>.
    /// </para>
    /// <para>
    /// Registration is last-wins, so calling this after another provider replaces it rather than
    /// conflicting.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is empty.</exception>
    public static MillraceBuilder UseSqliteStorage(
        this MillraceBuilder builder, string connectionString, Action<SqliteStorageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = new SqliteStorageOptions();
        configure?.Invoke(options);

        builder.Services.Replace(ServiceDescriptor.Singleton(sp => new SqliteStorage(
            connectionString,
            sp.GetRequiredService<TimeProvider>(),
            options)));
        return builder.UseStorage(
            sp => sp.GetRequiredService<SqliteStorage>(),
            sp => sp.GetRequiredService<SqliteStorage>(),
            sp => sp.GetRequiredService<SqliteStorage>());
    }
}
