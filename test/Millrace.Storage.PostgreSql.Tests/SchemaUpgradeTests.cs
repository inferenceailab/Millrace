using Microsoft.Extensions.Time.Testing;
using Millrace.Storage;
using Npgsql;
using Xunit;

namespace Millrace.Storage.PostgreSql.Tests;

/// <summary>
/// Upgrading a database created by an older release (§11.25).
/// </summary>
/// <remarks>
/// <para>
/// <c>CREATE TABLE IF NOT EXISTS</c> does nothing at all to a table that already exists, so for two
/// releases a column added to that statement reached new databases and silently never reached
/// upgraded ones. <c>requeued_from</c> and <c>trace_parent</c> shipped in 0.4 that way: anyone
/// running 0.3 against PostgreSQL would have got a missing-column error on the first enqueue after
/// upgrading, and nothing here would have caught it, because every test starts from an empty schema.
/// </para>
/// <para>
/// This one deliberately does not. It builds the 0.1-era table by hand and then asks the provider to
/// initialize over it, which is the only shape of test that can see the bug.
/// </para>
/// </remarks>
public sealed class SchemaUpgradeTests
{
    /// <summary>The jobs table exactly as 0.1 created it — no columns added since.</summary>
    private const string LegacyJobsTable = """
        CREATE TABLE {0}.jobs (
            id uuid PRIMARY KEY,
            seq bigint GENERATED ALWAYS AS IDENTITY,
            queue text NOT NULL,
            state integer NOT NULL,
            priority integer NOT NULL,
            invocation jsonb NOT NULL,
            retry jsonb NOT NULL,
            created_at timestamptz NOT NULL,
            due_at timestamptz,
            worker_id text,
            lease_until timestamptz,
            attempt integer NOT NULL DEFAULT 0,
            failures integer NOT NULL DEFAULT 0,
            cancel_requested boolean NOT NULL DEFAULT FALSE,
            idempotency_key text,
            tenant_id text,
            parent_id uuid,
            last_error text,
            finished_at timestamptz,
            workflow_instance_id uuid,
            activity_node_id text);
        """;

    [Fact]
    public async Task Initializing_over_an_old_schema_adds_every_column_since()
    {
        var connectionString = await PostgresTestDatabase.GetConnectionStringAsync();
        if (connectionString is null)
        {
            if (PostgresTestDatabase.IsRequired)
            {
                throw new InvalidOperationException("PostgreSQL was required for this run but none could be reached.");
            }

            Assert.Skip("PostgreSQL unavailable.");
        }

        var schema = $"millrace_upgrade_{Guid.NewGuid():n}";
        await using var dataSource = NpgsqlDataSource.Create(connectionString!);

        await using (var conn = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"CREATE SCHEMA {schema};" + string.Format(LegacyJobsTable, schema);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var storage = new PostgreSqlStorage(dataSource, time, new PostgreSqlStorageOptions { Schema = schema });
        await storage.InitializeAsync(TestContext.Current.CancellationToken);

        // The assertion that would have failed before §11.25: initializing over an existing table
        // has to reach it, not skip it.
        await using (var conn = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT column_name FROM information_schema.columns
                WHERE table_schema = '{schema}' AND table_name = 'jobs'
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

        // And the provider works against it — a column that exists but is not written or read would
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
        var connectionString = await PostgresTestDatabase.GetConnectionStringAsync();
        if (connectionString is null)
        {
            Assert.Skip("PostgreSQL unavailable.");
        }

        // Every ALTER is guarded, so a second run must not fail — initialization is lazy and can
        // happen on any node, at any time, concurrently.
        var schema = $"millrace_twice_{Guid.NewGuid():n}";
        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var storage = new PostgreSqlStorage(dataSource, time, new PostgreSqlStorageOptions { Schema = schema });

        await storage.InitializeAsync(TestContext.Current.CancellationToken);
        await storage.InitializeAsync(TestContext.Current.CancellationToken);
    }
}
