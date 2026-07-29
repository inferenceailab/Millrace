using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Millrace.Storage.Sqlite;

/// <summary>
/// The SQLite provider: a durable queue with no server behind it, for single-node deployments,
/// local development and tests that want restarts to survive.
/// </summary>
/// <remarks>
/// <para>
/// <b>Claims serialise instead of skipping locks.</b> The other two providers lean on
/// <c>FOR UPDATE SKIP LOCKED</c> to let concurrent claimers step over each other's rows. SQLite has
/// no row locks and exactly one writer, so every mutating path here opens a <c>BEGIN IMMEDIATE</c>
/// transaction and takes that writer lock up front. Two concurrent claims therefore cannot see the
/// same candidate row — not because they skip it, but because the second one waits. The contract
/// asks only that they never return the same job, and permits returning fewer than requested, so
/// this is conformant; what it costs is throughput under a high <c>MaxParallelism</c>, which is the
/// point at which a server-backed provider is the right answer.
/// </para>
/// <para>
/// <b>That write lock also removes work.</b> The PostgreSQL provider resolves an idempotency
/// conflict by inserting, losing, looking up the holder, and retrying when the holder went terminal
/// in the read-committed window between the two. Here the whole insert happens under the writer
/// lock, so a plain look-then-insert is already atomic and the retry loop has nothing to guard
/// against.
/// </para>
/// <para>
/// <b>Types.</b> SQLite has no date, uuid or boolean storage classes, so timestamps are
/// fixed-width UTC text, ids are canonical lowercase uuid text, and flags are integers. Both text
/// encodings sort the way the contract needs their values to sort, which is what lets ordering and
/// keyset paging happen in the database rather than in memory — see
/// <see cref="Timestamp(DateTimeOffset)"/> and <see cref="Id(Guid)"/> for which part of each is
/// load-bearing and which is merely tidy.
/// </para>
/// <para>
/// Every <c>now</c> comparison uses the injected <see cref="TimeProvider"/>, never
/// <c>CURRENT_TIMESTAMP</c>, which is what lets the conformance kit drive this provider with a fake
/// clock exactly as it drives the in-memory one.
/// </para>
/// </remarks>
public sealed partial class SqliteStorage
    : IJobStorage, IWorkflowStorage, IStorageNotifier, Monitoring.IMonitoringStorage, IAsyncDisposable
{
    private const string ActiveStates = "0, 1, 2, 4, 7"; // Scheduled, Enqueued, Processing, Failed, Awaiting

    private const string JobColumns =
        "id, queue, state, priority, invocation, retry, created_at, due_at, worker_id, " +
        "lease_until, attempt, failures, cancel_requested, idempotency_key, tenant_id, " +
        "parent_id, last_error, finished_at, workflow_instance_id, activity_node_id, requeued_from, " +
        "trace_parent, recurring_id";

    private const string RecurringColumns =
        "id, cron, queue, invocation, retry, priority, tenant_id, next_fire_time, " +
        "last_fire_time, created_at, updated_at";

    /// <summary>
    /// Fixed-width UTC text: one instant has exactly one encoding, and lexicographic order is
    /// chronological order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every ordering the contract states (<c>DueAt ASC</c> activation, oldest bookmark first, the
    /// keyset cursor's row-value comparison) is a comparison on one of these columns, and the
    /// recurring fence matches one exactly. Both need the encoding to be a function of the instant
    /// and to sort the way time does.
    /// </para>
    /// <para>
    /// <b>Constant width is not what buys the ordering, though</b> — worth recording, because the
    /// obvious argument for it is wrong. Dropping trailing zeros (what the default
    /// <c>DateTimeOffset</c> mapping's <c>FFFFFFF</c> does) is prefix-preserving on a fixed-position
    /// fraction, so it sorts correctly too: swapping this constant to <c>FFFFFFF</c> leaves all 126
    /// conformance facts green, which was checked rather than assumed. What constant width actually
    /// buys is that the stored form is unambiguous to anything that does not share this formatter — a
    /// migration, an ad-hoc query, a future reader of the same file. That is a smaller claim than
    /// "otherwise it sorts wrong", and it is the true one.
    /// </para>
    /// </remarks>
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fffffff";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);

    private readonly string _connectionString;
    private readonly TimeProvider _time;
    private readonly SqliteStorageOptions _options;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly Lock _listenerGate = new();
    private readonly List<Channel<QueueSignal>> _listeners = [];

    /// <summary>
    /// Held open for the provider's lifetime so an in-memory database survives between operations.
    /// </summary>
    /// <remarks>
    /// A <c>Mode=Memory</c> database exists only while a connection to it is open, so pooling it the
    /// way a file database is pooled would discard the schema and every row the moment the last
    /// operation finished. One idle connection is a small price for making the memory mode behave
    /// like the file mode, and it costs a file database nothing.
    /// </remarks>
    private SqliteConnection? _keepAlive;
    private volatile bool _initialized;

    /// <summary>Creates the provider over a SQLite connection string.</summary>
    /// <remarks>
    /// <paramref name="time"/> defaults to <see cref="TimeProvider.System"/> and every <c>now</c>
    /// comparison goes through it — database time is never read.
    /// </remarks>
    public SqliteStorage(string connectionString, TimeProvider? time = null, SqliteStorageOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _time = time ?? TimeProvider.System;
        _options = options ?? new SqliteStorageOptions();
    }

    /// <inheritdoc />
    /// <remarks>
    /// In-process only: the wakeup is published to listeners in this application, because SQLite has
    /// no cross-process notification channel. A second process sharing the file therefore falls back
    /// to its poll interval, which is a latency difference and nothing more — the contract already
    /// permits every signal to be dropped.
    /// </remarks>
    public StorageCapabilities Capabilities => StorageCapabilities.Notifications;

    /// <summary>Creates the tables (idempotent). Called lazily unless disabled.</summary>
    public async ValueTask InitializeAsync(CancellationToken ct)
    {
        _keepAlive ??= await OpenCoreAsync(ct).ConfigureAwait(false);

        if (_options.UseWriteAheadLog)
        {
            // A persistent property of the file, so this is set once here rather than per connection.
            await using var pragma = _keepAlive.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode = WAL";
            await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var cmd = _keepAlive.CreateCommand();
        cmd.CommandText = """
            -- seq is the primary key and id is merely unique, which inverts the other two providers.
            -- The contract orders equal-priority claims by "enqueue completion order", and in SQLite
            -- INTEGER PRIMARY KEY AUTOINCREMENT is the only monotonic counter that never reuses a
            -- value after a delete — reuse would let a new job sort ahead of an older one.
            CREATE TABLE IF NOT EXISTS jobs (
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

            -- The idempotency scope, where a null tenant must collide with another null tenant.
            -- PostgreSQL says NULLS NOT DISTINCT; SQLite has no such modifier and treats every NULL
            -- as unique, so the leading `tenant_id IS NULL` expression carries the distinction
            -- instead: null tenants land in bucket 1 and collide with each other, and a tenant
            -- literally named '' lands in bucket 0 and stays a scope of its own. COALESCE alone
            -- would have merged those two.
            CREATE UNIQUE INDEX IF NOT EXISTS ux_jobs_active_key
                ON jobs (tenant_id IS NULL, COALESCE(tenant_id, ''), idempotency_key)
                WHERE idempotency_key IS NOT NULL AND state IN (0, 1, 2, 4, 7);
            CREATE INDEX IF NOT EXISTS ix_jobs_claim
                ON jobs (queue, priority DESC, seq) WHERE state IN (1, 2);
            CREATE INDEX IF NOT EXISTS ix_jobs_due
                ON jobs (due_at, seq) WHERE state IN (0, 4);
            CREATE INDEX IF NOT EXISTS ix_jobs_parent
                ON jobs (parent_id) WHERE state = 7;

            CREATE TABLE IF NOT EXISTS recurring (
                id TEXT PRIMARY KEY,
                cron TEXT NOT NULL,
                queue TEXT NOT NULL,
                invocation TEXT NOT NULL,
                retry TEXT NOT NULL,
                priority INTEGER NOT NULL DEFAULT 0,
                tenant_id TEXT,
                next_fire_time TEXT NOT NULL,
                last_fire_time TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_recurring_due ON recurring (next_fire_time, id);

            CREATE TABLE IF NOT EXISTS workflow_instances (
                id TEXT PRIMARY KEY,
                definition_id TEXT NOT NULL,
                definition_version INTEGER NOT NULL,
                state INTEGER NOT NULL,
                data_json TEXT NOT NULL,
                cursor_json TEXT,
                revision INTEGER NOT NULL,
                tenant_id TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL);

            CREATE TABLE IF NOT EXISTS bookmarks (
                id TEXT PRIMARY KEY,
                instance_id TEXT NOT NULL,
                signal_name TEXT NOT NULL,
                correlation_id TEXT NOT NULL,
                payload_type_name TEXT,
                created_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_bookmarks_lookup
                ON bookmarks (signal_name, correlation_id, created_at, id);

            -- Monitoring read model (§11.12): keyset order is (created_at DESC, id DESC).
            CREATE INDEX IF NOT EXISTS ix_jobs_monitor ON jobs (created_at DESC, id DESC);
            CREATE INDEX IF NOT EXISTS ix_jobs_monitor_state ON jobs (state, created_at DESC, id DESC);
            CREATE INDEX IF NOT EXISTS ix_instances_monitor
                ON workflow_instances (created_at DESC, id DESC);
            CREATE INDEX IF NOT EXISTS ix_instances_monitor_state
                ON workflow_instances (state, created_at DESC, id DESC);

            CREATE TABLE IF NOT EXISTS job_attempts (
                job_id TEXT NOT NULL REFERENCES jobs (id) ON DELETE CASCADE,
                attempt INTEGER NOT NULL,
                outcome INTEGER NOT NULL,
                recorded_at TEXT NOT NULL,
                worker_id TEXT,
                error TEXT,
                PRIMARY KEY (job_id, attempt));
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Columns added after the first release live here, for the reason §11.25 records: CREATE
        // TABLE IF NOT EXISTS does nothing at all to a table that already exists, so a column added
        // to the statement above reaches new databases and silently never reaches upgraded ones.
        // SQLite has no ADD COLUMN IF NOT EXISTS, so the existing columns are read first.
        await AddColumnIfMissingAsync("jobs", "requeued_from", "TEXT", ct).ConfigureAwait(false);
        await AddColumnIfMissingAsync("jobs", "trace_parent", "TEXT", ct).ConfigureAwait(false);
        await AddColumnIfMissingAsync("jobs", "recurring_id", "TEXT", ct).ConfigureAwait(false);

        // After the column it indexes, necessarily — and that ordering is the whole reason the
        // added columns are a separate step. Partial, because only fired jobs carry a recurring id
        // and there are few definitions, so this indexes a small slice and leaves the claim path's
        // main table untouched (§11.26).
        await using var recurringIndex = _keepAlive.CreateCommand();
        recurringIndex.CommandText = """
            CREATE INDEX IF NOT EXISTS ix_jobs_recurring
                ON jobs (recurring_id, created_at DESC, id DESC) WHERE recurring_id IS NOT NULL
            """;
        await recurringIndex.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        _initialized = true;
    }

    // ---------------------------------------------------------------- IJobStorage

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<JobId>> EnqueueAsync(IReadOnlyList<JobRecord> jobs, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var ids = new JobId[jobs.Count];
        var wakeups = new HashSet<string>(StringComparer.Ordinal);

        await using (var conn = await OpenAsync(ct).ConfigureAwait(false))
        {
            await using var tx = BeginImmediate(conn);
            for (var i = 0; i < jobs.Count; i++)
            {
                ids[i] = await InsertCoreAsync(conn, jobs[i], wakeups, ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        Publish(wakeups);
        return ids;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<JobRecord>> ClaimAsync(ClaimRequest request, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        if (request.Queues.Count == 0 || request.MaxCount <= 0)
        {
            return [];
        }

        var now = _time.GetUtcNow();
        var queueParams = Enumerable.Range(0, request.Queues.Count).Select(i => $"@q{i}").ToArray();

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = BeginImmediate(conn);

        // The whole claim runs under the writer lock, so this select-then-update is exactly as
        // exclusive as SKIP LOCKED is elsewhere: a second claimer blocks at BEGIN IMMEDIATE and
        // sees the rows already Processing when it gets in.
        var candidates = new List<(string Id, int State, int Attempt, string? WorkerId)>();
        await using (var pick = conn.CreateCommand())
        {
            pick.CommandText = $"""
                SELECT id, state, attempt, worker_id FROM jobs
                WHERE queue IN ({string.Join(", ", queueParams)})
                  AND (state = 1 OR (state = 2 AND lease_until <= @now))
                ORDER BY priority DESC, seq
                LIMIT @max
                """;
            for (var i = 0; i < request.Queues.Count; i++)
            {
                pick.Parameters.AddWithValue($"@q{i}", request.Queues[i]);
            }

            pick.Parameters.AddWithValue("@now", Timestamp(now));
            pick.Parameters.AddWithValue("@max", request.MaxCount);

            await using var reader = await pick.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                candidates.Add((
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        foreach (var candidate in candidates)
        {
            // Still Processing when it is claimed again means the previous attempt ended without
            // ever reporting a verdict — the lease simply expired. That is the only moment an
            // interruption becomes observable, because nothing arrives to record it (§11.27).
            if (candidate.State == 2)
            {
                await using var history = conn.CreateCommand();
                history.CommandText = """
                    INSERT OR IGNORE INTO job_attempts
                        (job_id, attempt, outcome, recorded_at, worker_id, error)
                    VALUES (@id, @attempt, 1, @now, @worker, NULL)
                    """;
                history.Parameters.AddWithValue("@id", candidate.Id);
                history.Parameters.AddWithValue("@attempt", candidate.Attempt);
                history.Parameters.AddWithValue("@now", Timestamp(now));
                history.Parameters.AddWithValue("@worker", Db(candidate.WorkerId));
                await history.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using var prune = conn.CreateCommand();
            prune.CommandText = $"""
                DELETE FROM job_attempts
                WHERE job_id = @id AND attempt <= @attempt - {JobAttemptRules.HistoryLimit}
                """;
            prune.Parameters.AddWithValue("@id", candidate.Id);
            prune.Parameters.AddWithValue("@attempt", candidate.Attempt);
            await prune.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var idParams = Enumerable.Range(0, candidates.Count).Select(i => $"@id{i}").ToArray();
        var claimed = new List<(JobRecord Job, long Seq)>();
        await using (var update = conn.CreateCommand())
        {
            update.CommandText = $"""
                UPDATE jobs
                SET state = 2, worker_id = @worker, lease_until = @until, attempt = attempt + 1
                WHERE id IN ({string.Join(", ", idParams)})
                RETURNING {JobColumns}, seq
                """;
            for (var i = 0; i < candidates.Count; i++)
            {
                update.Parameters.AddWithValue($"@id{i}", candidates[i].Id);
            }

            update.Parameters.AddWithValue("@worker", request.WorkerId);
            update.Parameters.AddWithValue("@until", Timestamp(now + request.LeaseDuration));

            await using var reader = await update.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                claimed.Add((ReadJob(reader), reader.GetInt64(reader.GetOrdinal("seq"))));
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);

        // RETURNING row order is not guaranteed — restore the contract order.
        return claimed
            .OrderByDescending(c => c.Job.Priority)
            .ThenBy(c => c.Seq)
            .Select(c => c.Job)
            .ToList();
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<JobId>> RenewLeasesAsync(
        string workerId, IReadOnlyList<JobId> jobs, TimeSpan lease, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        if (jobs.Count == 0)
        {
            return [];
        }

        var until = _time.GetUtcNow() + lease;
        var idParams = Enumerable.Range(0, jobs.Count).Select(i => $"@id{i}").ToArray();

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE jobs SET lease_until = @until
            WHERE id IN ({string.Join(", ", idParams)}) AND state = 2 AND worker_id = @worker
            RETURNING id, cancel_requested
            """;
        for (var i = 0; i < jobs.Count; i++)
        {
            cmd.Parameters.AddWithValue($"@id{i}", Id(jobs[i].Value));
        }

        cmd.Parameters.AddWithValue("@until", Timestamp(until));
        cmd.Parameters.AddWithValue("@worker", workerId);

        var renewed = new List<JobId>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            // Cancel-requested jobs keep their (renewed) lease but are omitted from the result —
            // the worker disambiguates via GetJobAsync.
            if (!reader.GetBoolean(1))
            {
                renewed.Add(new JobId(Guid.Parse(reader.GetString(0))));
            }
        }

        return renewed;
    }

    /// <inheritdoc />
    public async ValueTask<bool> ApplyAsync(JobTransition transition, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var wakeups = new HashSet<string>(StringComparer.Ordinal);

        string set = transition.TargetState switch
        {
            JobState.Succeeded or JobState.Dead or JobState.Cancelled =>
                "state = @target, failures = @failures, last_error = COALESCE(@error, last_error), " +
                "finished_at = @finished, worker_id = NULL, lease_until = NULL",
            JobState.Failed =>
                "state = @target, failures = @failures, last_error = @error, due_at = @due, " +
                "worker_id = NULL, lease_until = NULL",
            JobState.Enqueued => // release
                "state = @target, failures = @failures, worker_id = NULL, lease_until = NULL, due_at = NULL",
            _ => throw new ArgumentException(
                $"Invalid transition target state {transition.TargetState}.", nameof(transition)),
        };

        await using (var conn = await OpenAsync(ct).ConfigureAwait(false))
        {
            await using var tx = BeginImmediate(conn);

            // The prior failure count separates a job that just failed from one dead-lettered
            // without executing — a poison pill leaves the count untouched, and there is no attempt
            // to record for it. Read before the update, under the writer lock (§11.27).
            int priorFailures;
            await using (var prior = conn.CreateCommand())
            {
                prior.CommandText = "SELECT failures FROM jobs WHERE id = @id";
                prior.Parameters.AddWithValue("@id", Id(transition.JobId.Value));
                if (await prior.ExecuteScalarAsync(ct).ConfigureAwait(false) is not long failures)
                {
                    return false; // no such job — the fence cannot hold
                }

                priorFailures = (int)failures;
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""
                    UPDATE jobs SET {set}
                    WHERE id = @id AND state = 2 AND worker_id = @worker AND attempt = @attempt
                    RETURNING queue
                    """;
                cmd.Parameters.AddWithValue("@target", (int)transition.TargetState);
                cmd.Parameters.AddWithValue("@failures", transition.Failures);
                cmd.Parameters.AddWithValue("@error", Db(transition.Error));
                if (transition.TargetState is JobState.Succeeded or JobState.Dead or JobState.Cancelled)
                {
                    cmd.Parameters.AddWithValue("@finished", Timestamp(transition.FinishedAt ?? now));
                }
                else if (transition.TargetState == JobState.Failed)
                {
                    cmd.Parameters.AddWithValue("@due", Db(Timestamp(transition.DueAt)));
                }

                cmd.Parameters.AddWithValue("@id", Id(transition.JobId.Value));
                cmd.Parameters.AddWithValue("@worker", transition.ExpectedWorkerId);
                cmd.Parameters.AddWithValue("@attempt", transition.ExpectedAttempt);

                if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string queue)
                {
                    return false; // fence rejected — nothing changed, transaction discarded
                }

                if (transition.TargetState == JobState.Enqueued)
                {
                    wakeups.Add(queue);
                }
            }

            // Inside the same transaction as the fenced update, so the timeline can never disagree
            // with the counters it explains: either both land or neither does.
            if (JobAttemptRules.OutcomeFor(transition, priorFailures) is { } outcome)
            {
                await using var history = conn.CreateCommand();
                history.CommandText = $"""
                    INSERT OR IGNORE INTO job_attempts
                        (job_id, attempt, outcome, recorded_at, worker_id, error)
                    VALUES (@id, @attempt, @outcome, @now, @worker, @error);
                    DELETE FROM job_attempts
                    WHERE job_id = @id AND attempt <= @attempt - {JobAttemptRules.HistoryLimit};
                    """;
                history.Parameters.AddWithValue("@id", Id(transition.JobId.Value));
                history.Parameters.AddWithValue("@attempt", transition.ExpectedAttempt);
                history.Parameters.AddWithValue("@outcome", (int)outcome);
                history.Parameters.AddWithValue("@now", Timestamp(now));
                history.Parameters.AddWithValue("@worker", transition.ExpectedWorkerId);
                history.Parameters.AddWithValue(
                    "@error", outcome == JobAttemptOutcome.Failed ? Db(transition.Error) : DBNull.Value);
                await history.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // The fence has held, so this worker still owns the job and may advance the instance. A
            // stale revision throws, the transaction is never committed, and the fenced update above
            // rolls back with it — the whole transition is all-or-nothing.
            if (transition.Checkpoint is { } checkpoint)
            {
                await UpdateInstanceCoreAsync(conn, checkpoint.Instance, checkpoint.ExpectedRevision, ct)
                    .ConfigureAwait(false);
            }

            foreach (var bookmark in transition.Bookmarks)
            {
                await InsertBookmarkAsync(conn, bookmark, ct).ConfigureAwait(false);
            }

            foreach (var record in transition.Enqueue)
            {
                await InsertCoreAsync(conn, record, wakeups, ct).ConfigureAwait(false);
            }

            if (transition.ActivateContinuations)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE jobs SET state = 1 WHERE parent_id = @id AND state = 7
                    RETURNING queue
                    """;
                cmd.Parameters.AddWithValue("@id", Id(transition.JobId.Value));
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    wakeups.Add(reader.GetString(0));
                }
            }

            if (transition.CancelContinuations)
            {
                await CancelAwaitingClosureAsync(conn, transition.JobId, now, ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        Publish(wakeups);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryRunNowAsync(JobId id, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        string queue;
        await using (var conn = await OpenAsync(ct).ConfigureAwait(false))
        {
            await using var cmd = conn.CreateCommand();

            // The state predicate is the fence: a job claimed between the operator's click and this
            // statement is no longer Failed, so it is left alone rather than yanked out from under
            // the worker running it. Attempt and failures are untouched (§11.32).
            cmd.CommandText = """
                UPDATE jobs SET state = 1, due_at = NULL WHERE id = @id AND state = 4
                RETURNING queue
                """;
            cmd.Parameters.AddWithValue("@id", Id(id.Value));
            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string found)
            {
                return false;
            }

            queue = found;
        }

        Publish([queue]);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryCancelAsync(JobId id, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);

        // Immediate, so the read below is serialised against concurrent claims and applies exactly
        // as the other providers' SELECT ... FOR UPDATE is.
        await using var tx = BeginImmediate(conn);

        JobState jobState;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT state FROM jobs WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", Id(id.Value));
            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not long state)
            {
                return false;
            }

            jobState = (JobState)(int)state;
        }

        if (jobState.IsTerminal())
        {
            return false;
        }

        await using (var cmd = conn.CreateCommand())
        {
            if (jobState == JobState.Processing)
            {
                cmd.CommandText = "UPDATE jobs SET cancel_requested = 1 WHERE id = @id";
            }
            else
            {
                cmd.CommandText = """
                    UPDATE jobs
                    SET state = 6, finished_at = @now, worker_id = NULL, lease_until = NULL, due_at = NULL
                    WHERE id = @id
                    """;
                cmd.Parameters.AddWithValue("@now", Timestamp(now));
            }

            cmd.Parameters.AddWithValue("@id", Id(id.Value));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (jobState != JobState.Processing)
        {
            await CancelAwaitingClosureAsync(conn, id, now, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<JobRecord?> GetJobAsync(JobId id, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {JobColumns} FROM jobs WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", Id(id.Value));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadJob(reader) : null;
    }

    /// <inheritdoc />
    public async ValueTask<int> ActivateDueJobsAsync(DateTimeOffset now, int batchSize, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var wakeups = new HashSet<string>(StringComparer.Ordinal);
        int activated;

        await using (var conn = await OpenAsync(ct).ConfigureAwait(false))
        {
            await using var tx = BeginImmediate(conn);
            await using var cmd = conn.CreateCommand();

            // "Each job activates exactly once" across concurrent schedulers is the writer lock's
            // doing here, rather than SKIP LOCKED's.
            cmd.CommandText = """
                UPDATE jobs SET state = 1, due_at = NULL
                WHERE id IN (
                    SELECT id FROM jobs
                    WHERE state IN (0, 4) AND due_at <= @now
                    ORDER BY due_at, seq
                    LIMIT @max)
                RETURNING queue
                """;
            cmd.Parameters.AddWithValue("@now", Timestamp(now));
            cmd.Parameters.AddWithValue("@max", batchSize);

            activated = 0;
            await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    activated++;
                    wakeups.Add(reader.GetString(0));
                }
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        Publish(wakeups);
        return activated;
    }

    /// <inheritdoc />
    public async ValueTask UpsertRecurringAsync(RecurringJobRecord record, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        // `IS NOT` is SQLite's null-safe inequality, standing in for PostgreSQL's IS DISTINCT FROM.
        cmd.CommandText = """
            INSERT INTO recurring
                (id, cron, queue, invocation, retry, priority, tenant_id,
                 next_fire_time, last_fire_time, created_at, updated_at)
            VALUES (@id, @cron, @queue, @invocation, @retry, @priority, @tenant,
                    @next, @last, @created, @updated)
            ON CONFLICT (id) DO UPDATE SET
                cron = excluded.cron,
                queue = excluded.queue,
                invocation = excluded.invocation,
                retry = excluded.retry,
                priority = excluded.priority,
                tenant_id = excluded.tenant_id,
                updated_at = excluded.updated_at,
                next_fire_time = CASE
                    WHEN recurring.cron IS NOT excluded.cron
                    THEN excluded.next_fire_time
                    ELSE recurring.next_fire_time END
            """;
        cmd.Parameters.AddWithValue("@id", record.Id);
        cmd.Parameters.AddWithValue("@cron", record.Cron);
        cmd.Parameters.AddWithValue("@queue", record.Queue);
        cmd.Parameters.AddWithValue("@invocation", JsonSerializer.Serialize(record.Invocation, Json));
        cmd.Parameters.AddWithValue("@retry", JsonSerializer.Serialize(record.Retry, Json));
        cmd.Parameters.AddWithValue("@priority", record.Priority);
        cmd.Parameters.AddWithValue("@tenant", Db(record.TenantId));
        cmd.Parameters.AddWithValue("@next", Timestamp(record.NextFireTime));
        cmd.Parameters.AddWithValue("@last", Db(Timestamp(record.LastFireTime)));
        cmd.Parameters.AddWithValue("@created", Timestamp(record.CreatedAt));
        cmd.Parameters.AddWithValue("@updated", Timestamp(record.UpdatedAt));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<RecurringJobRecord?> GetRecurringAsync(string id, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {RecurringColumns} FROM recurring WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRecurring(reader) : null;
    }

    /// <inheritdoc />
    public async ValueTask RemoveRecurringAsync(string id, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM recurring WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<RecurringJobRecord>> GetDueRecurringAsync(
        DateTimeOffset now, int batchSize, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {RecurringColumns} FROM recurring
            WHERE next_fire_time <= @now
            ORDER BY next_fire_time, id
            LIMIT @max
            """;
        cmd.Parameters.AddWithValue("@now", Timestamp(now));
        cmd.Parameters.AddWithValue("@max", batchSize);

        var due = new List<RecurringJobRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            due.Add(ReadRecurring(reader));
        }

        return due;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryFireRecurringAsync(
        string id, DateTimeOffset expectedFireTime, DateTimeOffset nextFireTime,
        JobRecord job, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var wakeups = new HashSet<string>(StringComparer.Ordinal);

        await using (var conn = await OpenAsync(ct).ConfigureAwait(false))
        {
            await using var tx = BeginImmediate(conn);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    UPDATE recurring
                    SET next_fire_time = @next, last_fire_time = @expected, updated_at = @now
                    WHERE id = @id AND next_fire_time = @expected
                    """;
                cmd.Parameters.AddWithValue("@next", Timestamp(nextFireTime));
                cmd.Parameters.AddWithValue("@expected", Timestamp(expectedFireTime));
                cmd.Parameters.AddWithValue("@now", Timestamp(_time.GetUtcNow()));
                cmd.Parameters.AddWithValue("@id", id);
                if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
                {
                    return false; // fence lost — another node fired this occurrence
                }
            }

            await InsertCoreAsync(conn, job, wakeups, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        Publish(wakeups);
        return true;
    }

    // ---------------------------------------------------------------- IWorkflowStorage

    /// <inheritdoc />
    public async ValueTask CreateInstanceAsync(WorkflowInstanceRecord instance, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO workflow_instances
                (id, definition_id, definition_version, state, data_json, cursor_json,
                 revision, tenant_id, created_at, updated_at)
            VALUES (@id, @def, @ver, @state, @data, @cursor, 1, @tenant, @created, @updated)
            """;
        cmd.Parameters.AddWithValue("@id", Id(instance.Id.Value));
        cmd.Parameters.AddWithValue("@def", instance.DefinitionId);
        cmd.Parameters.AddWithValue("@ver", instance.DefinitionVersion);
        cmd.Parameters.AddWithValue("@state", (int)instance.State);
        cmd.Parameters.AddWithValue("@data", instance.DataJson);
        cmd.Parameters.AddWithValue("@cursor", Db(instance.CursorJson));
        cmd.Parameters.AddWithValue("@tenant", Db(instance.TenantId));
        cmd.Parameters.AddWithValue("@created", Timestamp(instance.CreatedAt));
        cmd.Parameters.AddWithValue("@updated", Timestamp(instance.UpdatedAt));
        try
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException e) when (e.SqliteErrorCode == SqliteConstraintError)
        {
            throw new MillraceConcurrencyException($"Workflow instance '{instance.Id}' already exists.");
        }
    }

    /// <inheritdoc />
    public async ValueTask<WorkflowInstanceRecord?> GetInstanceAsync(WorkflowInstanceId id, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, definition_id, definition_version, state, data_json, cursor_json,
                   revision, tenant_id, created_at, updated_at
            FROM workflow_instances WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", Id(id.Value));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new WorkflowInstanceRecord
        {
            Id = new WorkflowInstanceId(Guid.Parse(reader.GetString(0))),
            DefinitionId = reader.GetString(1),
            DefinitionVersion = reader.GetInt32(2),
            State = (WorkflowInstanceState)reader.GetInt32(3),
            DataJson = reader.GetString(4),
            CursorJson = reader.IsDBNull(5) ? null : reader.GetString(5),
            Revision = reader.GetInt64(6),
            TenantId = reader.IsDBNull(7) ? null : reader.GetString(7),
            CreatedAt = ParseTimestamp(reader.GetString(8)),
            UpdatedAt = ParseTimestamp(reader.GetString(9)),
        };
    }

    /// <inheritdoc />
    public async ValueTask UpdateInstanceAsync(
        WorkflowInstanceRecord instance, long expectedRevision, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await UpdateInstanceCoreAsync(conn, instance, expectedRevision, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask AddBookmarkAsync(BookmarkRecord bookmark, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await InsertBookmarkAsync(conn, bookmark, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<BookmarkRecord?> ConsumeBookmarkAsync(
        string signalName, string correlationId, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        // One statement, so at-most-once needs nothing beyond SQLite's single writer. The tie-break
        // on id is byte order, which canonical lowercase uuid text sorts in — see Id().
        cmd.CommandText = """
            DELETE FROM bookmarks
            WHERE id = (
                SELECT id FROM bookmarks
                WHERE signal_name = @signal AND correlation_id = @correlation
                ORDER BY created_at, id
                LIMIT 1)
            RETURNING id, instance_id, signal_name, correlation_id, payload_type_name, created_at
            """;
        cmd.Parameters.AddWithValue("@signal", signalName);
        cmd.Parameters.AddWithValue("@correlation", correlationId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new BookmarkRecord
        {
            Id = Guid.Parse(reader.GetString(0)),
            InstanceId = new WorkflowInstanceId(Guid.Parse(reader.GetString(1))),
            SignalName = reader.GetString(2),
            CorrelationId = reader.GetString(3),
            PayloadTypeName = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedAt = ParseTimestamp(reader.GetString(5)),
        };
    }

    // ---------------------------------------------------------------- IStorageNotifier

    /// <inheritdoc />
    public async IAsyncEnumerable<QueueSignal> ListenAsync(
        IReadOnlySet<string> queues, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<QueueSignal>(new UnboundedChannelOptions { SingleReader = true });
        lock (_listenerGate)
        {
            _listeners.Add(channel);
        }

        try
        {
            await foreach (var signal in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (queues.Contains(signal.Queue))
                {
                    yield return signal;
                }
            }
        }
        finally
        {
            lock (_listenerGate)
            {
                _listeners.Remove(channel);
            }
        }
    }

    /// <summary>Publishes wakeups after the transaction that produced them has committed.</summary>
    /// <remarks>
    /// After, never inside: PostgreSQL can enqueue a notification mid-transaction because it delivers
    /// on commit and discards on rollback. An in-process channel has no such interlock, so a signal
    /// written before the commit would survive a rollback and point listeners at work that does not
    /// exist. Harmless by the contract — a signal is only ever "look now" — but it would be a lie
    /// this provider has no reason to tell.
    /// </remarks>
    private void Publish(IReadOnlyCollection<string> queues)
    {
        if (queues.Count == 0)
        {
            return;
        }

        lock (_listenerGate)
        {
            foreach (var listener in _listeners)
            {
                foreach (var queue in queues)
                {
                    listener.Writer.TryWrite(new QueueSignal(queue));
                }
            }
        }
    }

    // ---------------------------------------------------------------- internals

    private const int SqliteConstraintError = 19;

    /// <summary>
    /// Inserts one record with full <c>EnqueueAsync</c> semantics inside the ambient transaction,
    /// which the caller must have opened with <see cref="BeginImmediate"/>.
    /// </summary>
    /// <remarks>
    /// Both the Awaiting parent fixup and the idempotency dedup are read-then-write sequences that
    /// have to be indivisible. Under the writer lock they simply are, which is why neither the
    /// parent row lock nor the dedup retry loop the PostgreSQL provider needs appears here.
    /// </remarks>
    private async Task<JobId> InsertCoreAsync(
        SqliteConnection conn, JobRecord record, HashSet<string> wakeups, CancellationToken ct)
    {
        var effective = record;

        if (record.State == JobState.Awaiting)
        {
            if (record.ParentId is not { } parentId)
            {
                throw new ArgumentException($"Job '{record.Id}' is Awaiting but has no ParentId.", nameof(record));
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT state FROM jobs WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", Id(parentId.Value));
            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not long parentState)
            {
                throw new MillraceParentJobNotFoundException(parentId);
            }

            effective = (JobState)(int)parentState switch
            {
                JobState.Succeeded => effective with { State = JobState.Enqueued },
                JobState.Dead or JobState.Cancelled => effective with
                {
                    State = JobState.Cancelled,
                    FinishedAt = _time.GetUtcNow(),
                },
                _ => effective,
            };
        }
        else if (effective.State is not (JobState.Scheduled or JobState.Enqueued))
        {
            throw new ArgumentException(
                $"Job '{effective.Id}' has non-insertable state {effective.State}.", nameof(record));
        }

        if (effective.IdempotencyKey is not null && !effective.State.IsTerminal())
        {
            await using var cmd = conn.CreateCommand();

            // `IS` is null-safe equality in SQLite, so a null tenant matches only another null
            // tenant — the same scoping the partial unique index enforces.
            cmd.CommandText = $"""
                SELECT id FROM jobs
                WHERE idempotency_key = @key AND tenant_id IS @tenant AND state IN ({ActiveStates})
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("@key", effective.IdempotencyKey);
            cmd.Parameters.AddWithValue("@tenant", Db(effective.TenantId));
            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is string holder)
            {
                return new JobId(Guid.Parse(holder));
            }
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                INSERT INTO jobs ({JobColumns})
                VALUES (@id, @queue, @state, @priority, @invocation, @retry, @created, @due,
                        @worker, @lease, @attempt, @failures, @cancel, @key, @tenant,
                        @parent, @error, @finished, @wf, @activity, @requeued, @trace, @recurring)
                """;
            cmd.Parameters.AddWithValue("@id", Id(effective.Id.Value));
            cmd.Parameters.AddWithValue("@queue", effective.Queue);
            cmd.Parameters.AddWithValue("@state", (int)effective.State);
            cmd.Parameters.AddWithValue("@priority", effective.Priority);
            cmd.Parameters.AddWithValue("@invocation", JsonSerializer.Serialize(effective.Invocation, Json));
            cmd.Parameters.AddWithValue("@retry", JsonSerializer.Serialize(effective.Retry, Json));
            cmd.Parameters.AddWithValue("@created", Timestamp(effective.CreatedAt));
            cmd.Parameters.AddWithValue("@due", Db(Timestamp(effective.DueAt)));
            cmd.Parameters.AddWithValue("@worker", Db(effective.WorkerId));
            cmd.Parameters.AddWithValue("@lease", Db(Timestamp(effective.LeaseUntil)));
            cmd.Parameters.AddWithValue("@attempt", effective.Attempt);
            cmd.Parameters.AddWithValue("@failures", effective.Failures);
            cmd.Parameters.AddWithValue("@cancel", effective.CancelRequested ? 1 : 0);
            cmd.Parameters.AddWithValue("@key", Db(effective.IdempotencyKey));
            cmd.Parameters.AddWithValue("@tenant", Db(effective.TenantId));
            cmd.Parameters.AddWithValue("@parent", Db(Id(effective.ParentId?.Value)));
            cmd.Parameters.AddWithValue("@error", Db(effective.LastError));
            cmd.Parameters.AddWithValue("@finished", Db(Timestamp(effective.FinishedAt)));
            cmd.Parameters.AddWithValue("@wf", Db(Id(effective.WorkflowInstanceId?.Value)));
            cmd.Parameters.AddWithValue("@activity", Db(effective.ActivityNodeId));
            cmd.Parameters.AddWithValue("@requeued", Db(Id(effective.RequeuedFrom?.Value)));
            cmd.Parameters.AddWithValue("@trace", Db(effective.TraceParent));
            cmd.Parameters.AddWithValue("@recurring", Db(effective.RecurringId));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (effective.State == JobState.Enqueued)
        {
            wakeups.Add(effective.Queue);
        }

        return effective.Id;
    }

    private async Task CancelAwaitingClosureAsync(
        SqliteConnection conn, JobId rootId, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();

        // MATERIALIZED on purpose: the CTE reads the table the UPDATE writes, and a lazily evaluated
        // one would be walking rows this statement is changing underneath it.
        cmd.CommandText = """
            WITH RECURSIVE descendants(id) AS MATERIALIZED (
                SELECT id FROM jobs WHERE parent_id = @root AND state = 7
                UNION ALL
                SELECT j.id FROM jobs j JOIN descendants d ON j.parent_id = d.id
                WHERE j.state = 7)
            UPDATE jobs
            SET state = 6, finished_at = @now, worker_id = NULL, lease_until = NULL, due_at = NULL
            WHERE id IN (SELECT id FROM descendants)
            """;
        cmd.Parameters.AddWithValue("@root", Id(rootId.Value));
        cmd.Parameters.AddWithValue("@now", Timestamp(now));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The single instance-update implementation, shared by the standalone call and the
    /// transition-carried checkpoint, so the two cannot diverge.
    /// </summary>
    private static async Task UpdateInstanceCoreAsync(
        SqliteConnection conn, WorkflowInstanceRecord instance, long expectedRevision, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE workflow_instances
            SET definition_id = @def, definition_version = @ver, state = @state,
                data_json = @data, cursor_json = @cursor, tenant_id = @tenant,
                updated_at = @updated, revision = @expected + 1
            WHERE id = @id AND revision = @expected
            """;
        cmd.Parameters.AddWithValue("@def", instance.DefinitionId);
        cmd.Parameters.AddWithValue("@ver", instance.DefinitionVersion);
        cmd.Parameters.AddWithValue("@state", (int)instance.State);
        cmd.Parameters.AddWithValue("@data", instance.DataJson);
        cmd.Parameters.AddWithValue("@cursor", Db(instance.CursorJson));
        cmd.Parameters.AddWithValue("@tenant", Db(instance.TenantId));
        cmd.Parameters.AddWithValue("@updated", Timestamp(instance.UpdatedAt));
        cmd.Parameters.AddWithValue("@expected", expectedRevision);
        cmd.Parameters.AddWithValue("@id", Id(instance.Id.Value));

        if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
        {
            // Stale revision and missing instance are deliberately indistinguishable.
            throw new MillraceConcurrencyException(
                $"Workflow instance '{instance.Id}' revision conflict (expected {expectedRevision}).");
        }
    }

    private static async Task InsertBookmarkAsync(
        SqliteConnection conn, BookmarkRecord bookmark, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO bookmarks
                (id, instance_id, signal_name, correlation_id, payload_type_name, created_at)
            VALUES (@id, @instance, @signal, @correlation, @payload, @created)
            """;
        cmd.Parameters.AddWithValue("@id", Id(bookmark.Id));
        cmd.Parameters.AddWithValue("@instance", Id(bookmark.InstanceId.Value));
        cmd.Parameters.AddWithValue("@signal", bookmark.SignalName);
        cmd.Parameters.AddWithValue("@correlation", bookmark.CorrelationId);
        cmd.Parameters.AddWithValue("@payload", Db(bookmark.PayloadTypeName));
        cmd.Parameters.AddWithValue("@created", Timestamp(bookmark.CreatedAt));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task AddColumnIfMissingAsync(string table, string column, string type, CancellationToken ct)
    {
        await using (var probe = _keepAlive!.CreateCommand())
        {
            probe.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = @name";
            probe.Parameters.AddWithValue("@name", column);
            if (await probe.ExecuteScalarAsync(ct).ConfigureAwait(false) is long count && count > 0)
            {
                return;
            }
        }

        await using var alter = _keepAlive!.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async ValueTask EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
        {
            return;
        }

        if (!_options.AutoCreateSchema)
        {
            // Still needed even when the schema is managed elsewhere: an in-memory database only
            // exists while something holds it open.
            _keepAlive ??= await OpenCoreAsync(ct).ConfigureAwait(false);
            return;
        }

        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_initialized)
            {
                await InitializeAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _initGate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = await OpenCoreAsync(ct).ConfigureAwait(false);
        await using var pragma = conn.CreateCommand();

        // Per connection, unlike journal_mode: how long to wait for the single writer before
        // surfacing SQLITE_BUSY. Foreign keys are off by default in SQLite and job_attempts relies
        // on its cascade.
        pragma.CommandText =
            $"PRAGMA busy_timeout = {(int)_options.BusyTimeout.TotalMilliseconds}; PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return conn;
    }

    private async Task<SqliteConnection> OpenCoreAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    /// <summary>
    /// Opens a write transaction that takes SQLite's writer lock immediately.
    /// </summary>
    /// <remarks>
    /// Deferred is the default and is wrong for every path here. A deferred transaction takes a read
    /// lock first and upgrades on its first write, and two of those upgrading at once is the one
    /// shape SQLite cannot resolve by waiting — one of them must be rolled back. Taking the write
    /// lock up front turns that deadlock into a queue.
    /// <para>
    /// Measured, not assumed: flipping this to <c>deferred: true</c> fails exactly the seven
    /// concurrency facts in the kit — claim exclusivity, same-key enqueue, the cancel/apply and
    /// renew/reclaim races, and the Awaiting-parent races — each burning the full
    /// <see cref="SqliteStorageOptions.BusyTimeout"/> first. Everything else stays green, which is
    /// what makes this line load-bearing rather than defensive.
    /// </para>
    /// </remarks>
    private static SqliteTransaction BeginImmediate(SqliteConnection conn)
        => (SqliteTransaction)conn.BeginTransaction(IsolationLevel.Serializable, deferred: false);

    /// <summary>
    /// Canonical lowercase uuid text — the encoding whose lexicographic order <em>is</em> uuid byte
    /// order.
    /// </summary>
    /// <remarks>
    /// Load-bearing, not cosmetic. <c>ConsumeBookmarkAsync</c> breaks ties "in byte order — the
    /// order a database sorts a uuid column", and the canonical form prints the 16 bytes
    /// big-endian, so <c>ORDER BY id</c> on this text agrees with PostgreSQL's <c>ORDER BY</c> on a
    /// uuid column. <see cref="Guid.CompareTo(Guid)"/> would not, which is the divergence the SQL
    /// Server provider found the hard way.
    /// </remarks>
    private static string Id(Guid value) => value.ToString("d", CultureInfo.InvariantCulture);

    private static string? Id(Guid? value) => value is { } v ? Id(v) : null;

    private static string Timestamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static string? Timestamp(DateTimeOffset? value) => value is { } v ? Timestamp(v) : null;

    private static DateTimeOffset ParseTimestamp(string text)
        => new(
            DateTime.ParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None),
            TimeSpan.Zero);

    private static JobRecord ReadJob(SqliteDataReader reader) => new()
    {
        Id = new JobId(Guid.Parse(reader.GetString(0))),
        Queue = reader.GetString(1),
        State = (JobState)reader.GetInt32(2),
        Priority = reader.GetInt32(3),
        Invocation = JsonSerializer.Deserialize<JobInvocation>(reader.GetString(4), Json)!,
        Retry = JsonSerializer.Deserialize<Retry>(reader.GetString(5), Json)!,
        CreatedAt = ParseTimestamp(reader.GetString(6)),
        DueAt = reader.IsDBNull(7) ? null : ParseTimestamp(reader.GetString(7)),
        WorkerId = reader.IsDBNull(8) ? null : reader.GetString(8),
        LeaseUntil = reader.IsDBNull(9) ? null : ParseTimestamp(reader.GetString(9)),
        Attempt = reader.GetInt32(10),
        Failures = reader.GetInt32(11),
        CancelRequested = reader.GetBoolean(12),
        IdempotencyKey = reader.IsDBNull(13) ? null : reader.GetString(13),
        TenantId = reader.IsDBNull(14) ? null : reader.GetString(14),
        ParentId = reader.IsDBNull(15) ? null : new JobId(Guid.Parse(reader.GetString(15))),
        LastError = reader.IsDBNull(16) ? null : reader.GetString(16),
        FinishedAt = reader.IsDBNull(17) ? null : ParseTimestamp(reader.GetString(17)),
        WorkflowInstanceId = reader.IsDBNull(18)
            ? null
            : new WorkflowInstanceId(Guid.Parse(reader.GetString(18))),
        ActivityNodeId = reader.IsDBNull(19) ? null : reader.GetString(19),
        RequeuedFrom = reader.IsDBNull(20) ? null : new JobId(Guid.Parse(reader.GetString(20))),
        TraceParent = reader.IsDBNull(21) ? null : reader.GetString(21),
        RecurringId = reader.IsDBNull(22) ? null : reader.GetString(22),
    };

    private static RecurringJobRecord ReadRecurring(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Cron = reader.GetString(1),
        Queue = reader.GetString(2),
        Invocation = JsonSerializer.Deserialize<JobInvocation>(reader.GetString(3), Json)!,
        Retry = JsonSerializer.Deserialize<Retry>(reader.GetString(4), Json)!,
        Priority = reader.GetInt32(5),
        TenantId = reader.IsDBNull(6) ? null : reader.GetString(6),
        NextFireTime = ParseTimestamp(reader.GetString(7)),
        LastFireTime = reader.IsDBNull(8) ? null : ParseTimestamp(reader.GetString(8)),
        CreatedAt = ParseTimestamp(reader.GetString(9)),
        UpdatedAt = ParseTimestamp(reader.GetString(10)),
    };

    private static object Db(object? value) => value ?? DBNull.Value;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_keepAlive is { } conn)
        {
            _keepAlive = null;
            await conn.DisposeAsync().ConfigureAwait(false);
        }

        _initGate.Dispose();
    }
}
