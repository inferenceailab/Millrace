using Npgsql;
using Testcontainers.PostgreSql;
using Millrace.Storage;
using Millrace.Storage.PostgreSql;
using Millrace.Storage.Verification;
using Xunit;

namespace Millrace.Storage.PostgreSql.Tests;

/// <summary>
/// One PostgreSQL for the whole test run: <c>MILLRACE_POSTGRES_CONNECTION</c> if set (CI-provided
/// database), otherwise a Testcontainers postgres:17. When neither is available every
/// conformance fact skips with an explanation instead of failing.
/// </summary>
internal static class PostgresTestDatabase
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _connectionString;
    private static bool _unavailable;

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
                catch when (attempt == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10));
                }
                catch
                {
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
            Assert.Skip("PostgreSQL unavailable — start Docker or set MILLRACE_POSTGRES_CONNECTION.");
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
