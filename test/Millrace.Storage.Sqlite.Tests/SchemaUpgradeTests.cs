using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Millrace.Storage;
using Xunit;

namespace Millrace.Storage.Sqlite.Tests;

/// <summary>
/// Upgrading a database created by an older release (§11.25).
/// </summary>
/// <remarks>
/// <para>
/// <c>CREATE TABLE IF NOT EXISTS</c> does nothing at all to a table that already exists, so a column
/// added to that statement reaches new databases and silently never reaches upgraded ones. That is
/// how <c>requeued_from</c> and <c>trace_parent</c> shipped in 0.4 unable to load on any 0.3
/// database, and no test caught it because every other test starts from an empty schema.
/// </para>
/// <para>
/// SQLite makes this sharper than PostgreSQL did, because it has no
/// <c>ADD COLUMN IF NOT EXISTS</c>: the provider has to read <c>pragma_table_info</c> and decide.
/// A wrong decision is a broken upgrade in one direction and a hard failure on every start in the
/// other, so both are tested here.
/// </para>
/// </remarks>
public sealed class SchemaUpgradeTests : IAsyncDisposable
{
    /// <summary>The jobs table as it stood before the post-0.1 columns — nothing added since.</summary>
    private const string LegacyJobsTable = """
        CREATE TABLE jobs (
            seq INTEGER PRIMARY KEY AUTOINCREMENT,
            id TEXT NOT NULL UNIQUE,
            queue TEXT NOT NULL,
            state INTEGER NOT NULL,
            priority INTEGER NOT NULL,
            invocation TEXT NOT NULL,
            retry TEXT NOT NULL,
            created_at TEXT NOT NULL,
            due_at TEXT,
            worker_id TEXT,
            lease_until TEXT,
            attempt INTEGER NOT NULL DEFAULT 0,
            failures INTEGER NOT NULL DEFAULT 0,
            cancel_requested INTEGER NOT NULL DEFAULT 0,
            idempotency_key TEXT,
            tenant_id TEXT,
            parent_id TEXT,
            last_error TEXT,
            finished_at TEXT,
            workflow_instance_id TEXT,
            activity_node_id TEXT);
        """;

    private readonly List<string> _paths = [];

    private string NewPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"millrace-upgrade-{Guid.NewGuid():n}.db");
        _paths.Add(path);
        return path;
    }

    private static FakeTimeProvider NewTime()
        => new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Initializing_over_an_old_schema_adds_every_column_since()
    {
        var path = NewPath();
        var ct = TestContext.Current.CancellationToken;

        await using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = LegacyJobsTable;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var time = NewTime();
        await using var storage = new SqliteStorage($"Data Source={path}", time);
        await storage.InitializeAsync(ct);

        // The assertion that would have failed before §11.25: initializing over an existing table
        // has to reach it, not skip it.
        var columns = new List<string>();
        await using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM pragma_table_info('jobs')";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                columns.Add(reader.GetString(0));
            }
        }

        Assert.Contains("requeued_from", columns);
        Assert.Contains("trace_parent", columns);
        Assert.Contains("recurring_id", columns);

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

        await storage.EnqueueAsync([job], ct);
        var stored = await storage.GetJobAsync(job.Id, ct);

        Assert.Equal("nightly", stored!.RecurringId);
        Assert.Equal("00-trace-span-01", stored.TraceParent);
    }

    [Fact]
    public async Task Initializing_twice_over_the_same_database_is_a_no_op()
    {
        // Every added column is probed before it is added, so a second run must not fail with
        // "duplicate column name" — initialization is lazy and can happen at any time.
        var ct = TestContext.Current.CancellationToken;
        await using var storage = new SqliteStorage($"Data Source={NewPath()}", NewTime());

        await storage.InitializeAsync(ct);
        await storage.InitializeAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        // Pooled connections outlive the storage object, and Windows will not delete a file they
        // still hold open.
        SqliteConnection.ClearAllPools();
        await Task.Yield();

        foreach (var path in _paths)
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Temp-directory hygiene, not a conformance failure.
                }
            }
        }
    }
}
