using System.Text.RegularExpressions;
using Npgsql;

namespace Millrace.Benchmarks;

/// <summary>
/// Drops and recreates a system's database before every measured run.
/// </summary>
/// <remarks>
/// Each system gets its own database on the one server the compose file starts. Same disk, same
/// settings, same page cache pressure — which is what "on the same database" is for — but no run
/// inherits another's dead tuples or planner statistics. Without this the first system measured
/// leaves a warm cache and the second looks faster for it.
/// </remarks>
public static partial class Database
{
    /// <summary>Recreates <paramref name="database"/>, returning a connection string pointing at it.</summary>
    public static async Task<string> RecreateAsync(string adminConnectionString, string database, CancellationToken ct)
    {
        // The names are constants in this assembly rather than anything a caller supplies, but they
        // are interpolated into DDL that cannot be parameterised, so they are validated anyway —
        // the same rule PostgreSqlStorageOptions.Schema applies for the same reason.
        if (!Identifier().IsMatch(database))
        {
            throw new ArgumentException($"Database '{database}' must match [a-z_][a-z0-9_]*.", nameof(database));
        }

        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync(ct);
            await ExecuteAsync(admin, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)", ct);
            await ExecuteAsync(admin, $"CREATE DATABASE {database}", ct);

            // Then make the server finish what the drop started, before anything is timed.
            //
            // Without this the benchmark measures the *previous* run's cleanup. It showed up as
            // WorkflowCore degrading 348 → 101 → 69 inst/s across three supposedly identical runs:
            // its first run dropped the 100-instance warmup database and the rest dropped full
            // 2000-instance ones, so every run after the first was measured against a server still
            // flushing the last one. A checkpoint is the synchronous version of work PostgreSQL was
            // going to do anyway, and doing it here moves that cost outside the clock — the same
            // rule the schema DDL and the JIT already follow.
            await ExecuteAsync(admin, "CHECKPOINT", ct);
        }

        // Npgsql pools by connection string, so a pool opened against the previous incarnation of
        // this database would hand out connections to something that no longer exists.
        NpgsqlConnection.ClearAllPools();

        return new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = database }.ConnectionString;
    }

    /// <summary>Waits for the server to accept connections, so a just-started compose stack is not a failure.</summary>
    public static async Task WaitForServerAsync(string adminConnectionString, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(adminConnectionString);
                await connection.OpenAsync(ct);
                return;
            }
            catch (NpgsqlException) when (attempt < 30)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }

    /// <summary>The server version, reported alongside the results.</summary>
    public static async Task<string> ServerVersionAsync(string adminConnectionString, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync(ct);
        return connection.PostgreSqlVersion.ToString();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex Identifier();
}
