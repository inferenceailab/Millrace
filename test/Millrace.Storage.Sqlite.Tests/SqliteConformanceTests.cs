using Millrace.Storage;
using Millrace.Storage.Sqlite;
using Millrace.Storage.Verification;

namespace Millrace.Storage.Sqlite.Tests;

/// <summary>
/// One temporary database file per harness, deleted on dispose.
/// </summary>
/// <remarks>
/// <para>
/// A file rather than <c>Mode=Memory</c>, deliberately. The provider is meant to survive a restart,
/// and a suite that only ever exercised memory mode would never touch the journal, the WAL files or
/// the on-disk write lock — which is where a serialised-writer design either holds or does not.
/// </para>
/// <para>
/// <b>There is no strictness policy here, and that is worth saying out loud.</b> The other two
/// provider suites fail rather than skip when no database is reachable, and §11.42 notes that this
/// is what let a major driver bump be accepted on evidence rather than optimism. SQLite is a file,
/// so it is always reachable and the policy would assert nothing. A green run here therefore carries
/// less weight than a green PostgreSQL run does — it proves the suite ran, which for the others was
/// the part in doubt.
/// </para>
/// </remarks>
internal sealed class SqliteHarness : IStorageHarness
{
    private readonly SqliteStorage _storage;
    private readonly string _path;

    private SqliteHarness(SqliteStorage storage, string path)
    {
        _storage = storage;
        _path = path;
    }

    public IJobStorage Jobs => _storage;

    public IWorkflowStorage Workflows => _storage;

    public Millrace.Storage.Monitoring.IMonitoringStorage Monitoring => _storage;

    public static async ValueTask<IStorageHarness> CreateAsync(TimeProvider time)
    {
        var path = Path.Combine(Path.GetTempPath(), $"millrace-{Guid.NewGuid():n}.db");
        var storage = new SqliteStorage($"Data Source={path}", time);
        await storage.InitializeAsync(CancellationToken.None);
        return new SqliteHarness(storage, path);
    }

    public async ValueTask DisposeAsync()
    {
        await _storage.DisposeAsync();

        // Pooled connections outlive the storage object, and Windows will not delete a file they
        // still hold open.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A leaked handle is a test-hygiene problem, not a conformance failure — the temp
                // directory is the right place for it to be someone else's problem.
            }
        }
    }
}

public sealed class SqliteJobStorageConformanceTests : JobStorageConformanceSuite
{
    protected override ValueTask<IStorageHarness> CreateHarnessAsync(TimeProvider time)
        => SqliteHarness.CreateAsync(time);
}

public sealed class SqliteWorkflowStorageConformanceTests : WorkflowStorageConformanceSuite
{
    protected override ValueTask<IStorageHarness> CreateHarnessAsync(TimeProvider time)
        => SqliteHarness.CreateAsync(time);
}

public sealed class SqliteMonitoringConformanceTests : MonitoringConformanceSuite
{
    protected override ValueTask<IStorageHarness> CreateHarnessAsync(TimeProvider time)
        => SqliteHarness.CreateAsync(time);
}
