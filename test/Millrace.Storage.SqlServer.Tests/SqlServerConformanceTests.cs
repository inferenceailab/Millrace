using Microsoft.Data.SqlClient;
using Millrace.Storage;
using Millrace.Storage.SqlServer;
using Millrace.Storage.Verification;
using Testcontainers.MsSql;
using Xunit;

namespace Millrace.Storage.SqlServer.Tests;

/// <summary>
/// One SQL Server for the whole test run: <c>MILLRACE_SQLSERVER_CONNECTION</c> if set (CI-provided
/// database), otherwise a Testcontainers instance.
/// </summary>
/// <remarks>
/// Same strictness rule as the PostgreSQL suite: an unreachable database fails a run that expected
/// one and skips a run that did not, so CI can never report success for a provider it did not
/// actually exercise.
/// </remarks>
internal static class SqlServerTestDatabase
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _connectionString;
    private static bool _unavailable;
    private static Exception? _lastFailure;

    public static bool IsRequired { get; } = ResolveRequired(
        Environment.GetEnvironmentVariable("MILLRACE_REQUIRE_SQLSERVER"),
        Environment.GetEnvironmentVariable("CI"));

    public static Exception? LastFailure => _lastFailure;

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
        if (Environment.GetEnvironmentVariable("MILLRACE_SQLSERVER_CONNECTION") is { Length: > 0 } external)
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

            // SQL Server takes far longer to become ready than postgres, so the retry window is
            // wider rather than the same one tuned for a lighter image.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    // Testcontainers 4.13 obsoleted the parameterless constructor, and warnings are
                    // errors here. This is the version the library defaulted to before, stated
                    // explicitly — so the conformance run pins its database rather than inheriting
                    // whatever a future package version decides to default to.
                    var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();
                    await container.StartAsync();
                    _connectionString = container.GetConnectionString();
                    return _connectionString;
                }
                catch (Exception e) when (attempt == 0)
                {
                    _lastFailure = e;
                    await Task.Delay(TimeSpan.FromSeconds(20));
                }
                catch (Exception e)
                {
                    _lastFailure = e;
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

internal sealed class SqlServerHarness(SqlServerStorage storage, string connectionString, string schema)
    : IStorageHarness
{
    public IJobStorage Jobs => storage;

    public IWorkflowStorage Workflows => storage;

    public Millrace.Storage.Monitoring.IMonitoringStorage Monitoring => storage;

    public async ValueTask DisposeAsync()
    {
        // A fresh schema per harness gives the isolated, empty store the suite requires; dropping
        // it keeps one container usable for the whole run.
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        // Foreign keys are dropped first, then tables. job_attempts references jobs (§11.27), and
        // sys.tables comes back in no useful order, so dropping tables blind fails on whichever
        // side of the reference it reaches first.
        cmd.CommandText = $"""
            DECLARE @drop nvarchar(max) = N'';
            SELECT @drop = @drop + 'ALTER TABLE [{schema}].[' + OBJECT_NAME(parent_object_id)
                                 + '] DROP CONSTRAINT [' + name + '];'
                FROM sys.foreign_keys WHERE schema_id = SCHEMA_ID('{schema}');
            IF @drop <> N'' EXEC sp_executesql @drop;

            DECLARE @sql nvarchar(max) = N'';
            SELECT @sql = @sql + 'DROP TABLE [{schema}].[' + name + '];'
                FROM sys.tables WHERE schema_id = SCHEMA_ID('{schema}');
            IF @sql <> N'' EXEC sp_executesql @sql;
            IF SCHEMA_ID('{schema}') IS NOT NULL EXEC('DROP SCHEMA [{schema}]');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public static async ValueTask<IStorageHarness> CreateAsync(TimeProvider time)
    {
        var connectionString = await SqlServerTestDatabase.GetConnectionStringAsync();
        if (connectionString is null)
        {
            if (SqlServerTestDatabase.IsRequired)
            {
                throw new InvalidOperationException(
                    "SQL Server was required for this run but no database could be reached. "
                    + "Testcontainers could not start it and MILLRACE_SQLSERVER_CONNECTION was not set. "
                    + "Set MILLRACE_REQUIRE_SQLSERVER=false on a runner that genuinely cannot provide one.",
                    SqlServerTestDatabase.LastFailure);
            }

            Assert.Skip(
                "SQL Server unavailable — start Docker or set MILLRACE_SQLSERVER_CONNECTION. "
                + "Set MILLRACE_REQUIRE_SQLSERVER=true to make this a failure instead. "
                + $"Last container-start failure: {SqlServerTestDatabase.LastFailure?.Message ?? "none recorded"}");
        }

        var schema = $"millrace_{Guid.NewGuid():n}";
        var storage = new SqlServerStorage(
            connectionString, time, new SqlServerStorageOptions { Schema = schema });
        await storage.InitializeAsync(CancellationToken.None);
        return new SqlServerHarness(storage, connectionString, schema);
    }
}

public sealed class SqlServerJobStorageConformanceTests : JobStorageConformanceSuite
{
    protected override ValueTask<IStorageHarness> CreateHarnessAsync(TimeProvider time)
        => SqlServerHarness.CreateAsync(time);
}

public sealed class SqlServerWorkflowStorageConformanceTests : WorkflowStorageConformanceSuite
{
    protected override ValueTask<IStorageHarness> CreateHarnessAsync(TimeProvider time)
        => SqlServerHarness.CreateAsync(time);
}

public sealed class SqlServerMonitoringConformanceTests : MonitoringConformanceSuite
{
    protected override ValueTask<IStorageHarness> CreateHarnessAsync(TimeProvider time)
        => SqlServerHarness.CreateAsync(time);
}
