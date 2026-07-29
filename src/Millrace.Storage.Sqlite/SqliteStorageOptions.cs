namespace Millrace.Storage.Sqlite;

/// <summary>Options for the SQLite provider.</summary>
public sealed class SqliteStorageOptions
{
    /// <summary>
    /// Creates the tables on first use (idempotent DDL). Disable when migrations are managed at
    /// deploy time; call <c>SqliteStorage.InitializeAsync</c> instead.
    /// </summary>
    public bool AutoCreateSchema { get; set; } = true;

    /// <summary>
    /// How long a connection waits for the write lock before giving up with <c>SQLITE_BUSY</c>
    /// (default 30 seconds).
    /// </summary>
    /// <remarks>
    /// <para>
    /// SQLite has one writer at a time, so this is the provider's contention budget rather than a
    /// timeout on any individual statement. The default is generous on purpose: every write path
    /// here opens a short transaction, so waiting is nearly always better than surfacing a busy
    /// error to a worker that would only retry anyway.
    /// </para>
    /// <para>
    /// Raise it if claims start failing under a high <c>MaxParallelism</c>; that is also the signal
    /// that a server-backed provider fits the workload better.
    /// </para>
    /// </remarks>
    public TimeSpan BusyTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Enables write-ahead logging on the database (default <see langword="true"/>).
    /// </summary>
    /// <remarks>
    /// WAL lets readers run concurrently with the single writer, which is what keeps the dashboard
    /// and the monitoring queries from queueing behind a claim. It is a persistent property of the
    /// database file, so this is applied once at initialization rather than per connection. Left
    /// configurable because WAL needs shared memory and therefore does not work on every network
    /// file system.
    /// </remarks>
    public bool UseWriteAheadLog { get; set; } = true;
}
