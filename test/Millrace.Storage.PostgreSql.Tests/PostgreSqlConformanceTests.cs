using Npgsql;
using Testcontainers.PostgreSql;
using Millrace.Storage;
using Millrace.Storage.PostgreSql;
using Millrace.Storage.Verification;
using Xunit;

namespace Millrace.Storage.PostgreSql.Tests;

/// <summary>
/// One PostgreSQL for the whole test run: <c>MILLRACE_POSTGRES_CONNECTION</c> if set (CI-provided
/// database), otherwise a Testcontainers postgres:17.
/// </summary>
/// <remarks>
/// When no database can be reached the suite either skips or fails, depending on whether this run
/// was <em>expected</em> to have one — see <see cref="IsRequired"/>. Skipping is right on a
/// developer machine without Docker; in CI it would mean reporting success for a run that proved
/// nothing.
/// </remarks>
internal static class PostgresTestDatabase
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _connectionString;
    private static bool _unavailable;
    private static Exception? _lastFailure;

    /// <summary>
    /// Whether an unreachable database must fail the run rather than skip it.
    /// </summary>
    /// <remarks>
    /// An explicit <c>MILLRACE_REQUIRE_POSTGRES</c> wins. Otherwise this defaults to whether
    /// <c>CI</c> is set, so <em>every</em> CI job is strict unless it opts out deliberately. The
    /// inverse default — opt in per job — would leave the next job someone adds free to skip the
    /// whole suite and still report success.
    /// </remarks>
    public static bool IsRequired { get; } = ResolveRequired(
        Environment.GetEnvironmentVariable("MILLRACE_REQUIRE_POSTGRES"),
        Environment.GetEnvironmentVariable("CI"));

    /// <summary>The reason the last container start failed, for diagnostics.</summary>
    public static Exception? LastFailure => _lastFailure;

    /// <summary>
    /// The strictness policy, kept pure so it can be tested without a Docker daemon.
    /// </summary>
    /// <param name="explicitFlag">Value of <c>MILLRACE_REQUIRE_POSTGRES</c>, if any.</param>
    /// <param name="ciFlag">Value of <c>CI</c>, if any.</param>
    internal static bool ResolveRequired(string? explicitFlag, string? ciFlag)
        => string.IsNullOrWhiteSpace(explicitFlag) ? IsTruthy(ciFlag) : IsTruthy(explicitFlag);

    private static bool IsTruthy(string? value)
    {
        if (value is null)
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed is "1" or "yes" || (bool.TryParse(trimmed, out var parsed) && parsed);
    }

    public static async Task<string?> GetConnectionStringAsync()
    {
        if (Environment.GetEnvironmentVariable("MILLRACE_POSTGRES_CONNECTION") is { Length: > 0 } external)
        {
            return external;
        }

        await Gate.WaitAsync();
        try
        {
            if (_connectionString is not null)
            {
                return _connectionString;
            }

            if (_unavailable)
            {
                return null;
            }

            // Two attempts: Docker Desktop may still be starting up.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var container = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();
                    await container.StartAsync();
                    // Not disposed deliberately: Testcontainers' reaper removes it when the
                    // test process exits.
                    _connectionString = container.GetConnectionString();
                    return _connectionString;
                }
                catch (Exception ex) when (attempt == 0)
                {
                    _lastFailure = ex;
                    await Task.Delay(TimeSpan.FromSeconds(10));
                }
                catch (Exception ex)
                {
                    _lastFailure = ex;
                    _unavailable = true;
                    return null;
                }
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}

internal sealed class PostgreSqlHarness : IStorageHarness
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgreSqlStorage _storage;
    private readonly string _schema;

    public PostgreSqlHarness(NpgsqlDataSource dataSource, PostgreSqlStorage storage, string schema)
    {
        _dataSource = dataSource;
        _storage = storage;
        _schema = schema;
    }

    public IJobStorage Jobs => _storage;

    public IWorkflowStorage Workflows => _storage;

    public Millrace.Storage.Monitoring.IMonitoringStorage Monitoring => _storage;

    public async ValueTask DisposeAsync()
    {
        await using (var conn = await _dataSource.OpenConnectionAsync())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP SCHEMA IF EXISTS {_schema} CASCADE";
            await cmd.ExecuteNonQueryAsync();
        }

        await _dataSource.DisposeAsync();
    }

    public static async ValueTask<IStorageHarness> CreateAsync(TimeProvider time)
    {
        var connectionString = await PostgresTestDatabase.GetConnectionStringAsync();
        if (connectionString is null)
        {
            // Skipping here would report success for a run that verified nothing, so a run that was
            // expected to have a database fails loudly instead.
            if (PostgresTestDatabase.IsRequired)
            {
                throw new InvalidOperationException(
                    "PostgreSQL was required for this run but no database could be reached. "
                    + "Testcontainers could not start postgres:17-alpine and MILLRACE_POSTGRES_CONNECTION was not set. "
                    + "If this runner genuinely cannot provide one (for example a Windows runner, which cannot run "
                    + "Linux containers), set MILLRACE_REQUIRE_POSTGRES=false on that job so the skip is deliberate.",
                    PostgresTestDatabase.LastFailure);
            }

            Assert.Skip(
                "PostgreSQL unavailable — start Docker or set MILLRACE_POSTGRES_CONNECTION. "
                + "Set MILLRACE_REQUIRE_POSTGRES=true to make this a failure instead. "
                + $"Last container-start failure: {PostgresTestDatabase.LastFailure?.Message ?? "none recorded"}");
        }

        // A fresh schema per harness gives the required isolated, empty store cheaply.
        var schema = $"millrace_{Guid.NewGuid():n}";
        var dataSource = NpgsqlDataSource.Create(connectionString);
        var storage = new PostgreSqlStorage(dataSource, time, new PostgreSqlStorageOptions { Schema = schema });
        await storage.InitializeAsync(CancellationToken.None);
        return new PostgreSqlHarness(dataSource, storage, schema);
    }
}

public sealed class PostgreSqlJobStorageConformanceTests : JobStorageConformanceSuite
{
    protected override ValueTask<IStorageHarness> CreateHarnessAsync(TimeProvider time)
        => PostgreSqlHarness.CreateAsync(time);
}

public sealed class PostgreSqlWorkflowStorageConformanceTests : WorkflowStorageConformanceSuite
{
    protected override ValueTask<IStorageHarness> CreateHarnessAsync(TimeProvider time)
        => PostgreSqlHarness.CreateAsync(time);
}

public sealed class PostgreSqlMonitoringConformanceTests : MonitoringConformanceSuite
{
    protected override ValueTask<IStorageHarness> CreateHarnessAsync(TimeProvider time)
        => PostgreSqlHarness.CreateAsync(time);
}
