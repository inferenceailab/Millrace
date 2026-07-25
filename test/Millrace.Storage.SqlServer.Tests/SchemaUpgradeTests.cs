using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Time.Testing;
using Millrace.Storage;
using Xunit;

namespace Millrace.Storage.SqlServer.Tests;

/// <summary>
/// Upgrading a database created by an older release (§11.25).
/// </summary>
/// <remarks>
/// The PostgreSQL twin of this file explains the bug. It is worth having both because the guards
/// differ — <c>ADD COLUMN IF NOT EXISTS</c> there, <c>COL_LENGTH(...) IS NULL</c> here — and a
/// migration that works on one database and not the other is precisely the drift the conformance
/// kit exists to prevent, except that schema evolution is not something the kit can reach.
/// </remarks>
public sealed class SchemaUpgradeTests
{
    /// <summary>The jobs table exactly as 0.1 created it — no columns added since.</summary>
    private const string LegacyJobsTable = """
        CREATE TABLE {0}.jobs (
            id uniqueidentifier NOT NULL PRIMARY KEY,
            seq bigint IDENTITY(1,1) NOT NULL,
            queue nvarchar(200) NOT NULL,
            state int NOT NULL,
            priority int NOT NULL,
            invocation nvarchar(max) NOT NULL,
            retry nvarchar(max) NOT NULL,
            created_at datetimeoffset NOT NULL,
            due_at datetimeoffset NULL,
            worker_id nvarchar(200) NULL,
            lease_until datetimeoffset NULL,
            attempt int NOT NULL DEFAULT 0,
            failures int NOT NULL DEFAULT 0,
            cancel_requested bit NOT NULL DEFAULT 0,
            idempotency_key nvarchar(400) NULL,
            tenant_id nvarchar(200) NULL,
            parent_id uniqueidentifier NULL,
            last_error nvarchar(max) NULL,
            finished_at datetimeoffset NULL,
            workflow_instance_id uniqueidentifier NULL,
            activity_node_id nvarchar(200) NULL);
        """;

    [Fact]
    public async Task Initializing_over_an_old_schema_adds_every_column_since()
    {
        var connectionString = await SqlServerTestDatabase.GetConnectionStringAsync();
        if (connectionString is null)
        {
            if (SqlServerTestDatabase.IsRequired)
            {
                throw new InvalidOperationException("SQL Server was required for this run but none could be reached.");
            }

            Assert.Skip("SQL Server unavailable.");
        }

        var schema = $"millrace_upgrade_{Guid.NewGuid():n}";

        await using (var conn = new SqlConnection(connectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"EXEC('CREATE SCHEMA [{schema}]');" + string.Format(LegacyJobsTable, schema);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var storage = new SqlServerStorage(
            connectionString!, time, new SqlServerStorageOptions { Schema = schema });
        await storage.InitializeAsync(TestContext.Current.CancellationToken);

        await using (var conn = new SqlConnection(connectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT name FROM sys.columns WHERE object_id = OBJECT_ID('{schema}.jobs')
                """;
            var columns = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                columns.Add(reader.GetString(0));
            }

            Assert.Contains("requeued_from", columns);
            Assert.Contains("trace_parent", columns);
            Assert.Contains("recurring_id", columns);
        }

        // And the provider works against it: a column that exists but is never written or read would
        // pass the check above and still be useless.
        var job = new JobRecord
        {
            Id = JobId.New(time),
            Queue = "default",
            State = JobState.Enqueued,
            Invocation = new JobInvocation
            {
                TypeName = "Sample.IService, Sample",
                MethodName = "RunAsync",
                ParameterTypes = [],
                ArgumentsJson = [],
            },
            Retry = Retry.None,
            CreatedAt = time.GetUtcNow(),
            RecurringId = "nightly",
            TraceParent = "00-trace-span-01",
        };

        await storage.EnqueueAsync([job], TestContext.Current.CancellationToken);
        var stored = await storage.GetJobAsync(job.Id, TestContext.Current.CancellationToken);

        Assert.Equal("nightly", stored!.RecurringId);
        Assert.Equal("00-trace-span-01", stored.TraceParent);
    }

    [Fact]
    public async Task Initializing_twice_over_the_same_database_is_a_no_op()
    {
        var connectionString = await SqlServerTestDatabase.GetConnectionStringAsync();
        if (connectionString is null)
        {
            Assert.Skip("SQL Server unavailable.");
        }

        // Every guard is idempotent, so a second run must not fail — initialization is lazy and can
        // happen on any node, at any time, concurrently. The filtered index runs in its own batch
        // for the same reason it has to on a fresh database.
        var schema = $"millrace_twice_{Guid.NewGuid():n}";
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var storage = new SqlServerStorage(
            connectionString!, time, new SqlServerStorageOptions { Schema = schema });

        await storage.InitializeAsync(TestContext.Current.CancellationToken);
        await storage.InitializeAsync(TestContext.Current.CancellationToken);
    }
}
