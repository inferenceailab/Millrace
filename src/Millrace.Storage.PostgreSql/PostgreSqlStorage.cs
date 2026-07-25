using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Millrace.Storage.Monitoring;
using Npgsql;
using NpgsqlTypes;

namespace Millrace.Storage.PostgreSql;

/// <summary>
/// The PostgreSQL reference provider (ARCHITECTURE.md §11.2): claims via
/// <c>FOR UPDATE SKIP LOCKED</c>, idempotency scopes via a partial unique index with
/// <c>NULLS NOT DISTINCT</c> (PostgreSQL 15+), cancel cascades via a recursive CTE, and
/// push wakeups via <c>LISTEN/NOTIFY</c>. Every <c>now</c> comparison uses the injected
/// <see cref="TimeProvider"/>, never database time, so the conformance kit drives this
/// provider with a fake clock exactly like InMemory.
/// </summary>
public sealed partial class PostgreSqlStorage : IJobStorage, IWorkflowStorage, IStorageNotifier, IMonitoringStorage
{
    private const string ActiveStates = "0, 1, 2, 4, 7"; // Scheduled, Enqueued, Processing, Failed, Awaiting

    private const string JobColumns =
        "id, queue, state, priority, invocation, retry, created_at, due_at, worker_id, " +
        "lease_until, attempt, failures, cancel_requested, idempotency_key, tenant_id, " +
        "parent_id, last_error, finished_at, workflow_instance_id, activity_node_id, requeued_from, " +
        "trace_parent, recurring_id";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);

    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _time;
    private readonly string _schema;
    private readonly string _channel;
    private readonly bool _autoCreate;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private volatile bool _initialized;

    public PostgreSqlStorage(NpgsqlDataSource dataSource, TimeProvider? time = null, PostgreSqlStorageOptions? options = null)
    {
        _dataSource = dataSource;
        _time = time ?? TimeProvider.System;
        var opts = options ?? new PostgreSqlStorageOptions();
        _schema = opts.Schema;
        _channel = $"millrace_{_schema}";
        _autoCreate = opts.AutoCreateSchema;
    }

    public StorageCapabilities Capabilities => StorageCapabilities.Notifications;

    /// <summary>Creates the schema and tables (idempotent). Called lazily unless disabled.</summary>
    public async ValueTask InitializeAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE SCHEMA IF NOT EXISTS {_schema};
            CREATE TABLE IF NOT EXISTS {_schema}.jobs (
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
            CREATE UNIQUE INDEX IF NOT EXISTS ux_jobs_active_key
                ON {_schema}.jobs (tenant_id, idempotency_key) NULLS NOT DISTINCT
                WHERE idempotency_key IS NOT NULL AND state IN ({ActiveStates});
            CREATE INDEX IF NOT EXISTS ix_jobs_claim
                ON {_schema}.jobs (queue, priority DESC, seq) WHERE state IN (1, 2);
            CREATE INDEX IF NOT EXISTS ix_jobs_due
                ON {_schema}.jobs (due_at, seq) WHERE state IN (0, 4);
            CREATE INDEX IF NOT EXISTS ix_jobs_parent
                ON {_schema}.jobs (parent_id) WHERE state = 7;
            CREATE TABLE IF NOT EXISTS {_schema}.recurring (
                id text PRIMARY KEY,
                cron text NOT NULL,
                queue text NOT NULL,
                invocation jsonb NOT NULL,
                retry jsonb NOT NULL,
                priority integer NOT NULL DEFAULT 0,
                tenant_id text,
                next_fire_time timestamptz NOT NULL,
                last_fire_time timestamptz,
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_recurring_due ON {_schema}.recurring (next_fire_time, id);
            CREATE TABLE IF NOT EXISTS {_schema}.workflow_instances (
                id uuid PRIMARY KEY,
                definition_id text NOT NULL,
                definition_version integer NOT NULL,
                state integer NOT NULL,
                data_json jsonb NOT NULL,
                cursor_json jsonb,
                revision bigint NOT NULL,
                tenant_id text,
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL);
            CREATE TABLE IF NOT EXISTS {_schema}.bookmarks (
                id uuid PRIMARY KEY,
                instance_id uuid NOT NULL,
                signal_name text NOT NULL,
                correlation_id text NOT NULL,
                payload_type_name text,
                created_at timestamptz NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_bookmarks_lookup
                ON {_schema}.bookmarks (signal_name, correlation_id, created_at, id);
            -- Monitoring read model (§11.12): keyset order is (created_at DESC, id DESC). The
            -- state-leading index serves the common dashboard view, a list filtered to one state;
            -- the bare one serves an unfiltered list. There is no count index because §11.12
            -- removed totals from list responses.
            CREATE INDEX IF NOT EXISTS ix_jobs_monitor
                ON {_schema}.jobs (created_at DESC, id DESC);
            CREATE INDEX IF NOT EXISTS ix_jobs_monitor_state
                ON {_schema}.jobs (state, created_at DESC, id DESC);
            CREATE INDEX IF NOT EXISTS ix_instances_monitor
                ON {_schema}.workflow_instances (created_at DESC, id DESC);
            CREATE INDEX IF NOT EXISTS ix_instances_monitor_state
                ON {_schema}.workflow_instances (state, created_at DESC, id DESC);

            -- Columns added after 0.1 (§11.25). They live here rather than in CREATE TABLE because
            -- CREATE TABLE IF NOT EXISTS does nothing at all to a table that already exists: a
            -- column added to that statement reaches new databases and silently never reaches
            -- upgraded ones, which is how requeued_from and trace_parent shipped in 0.4 unable to
            -- load on any 0.3 database. One place per column, and it is this one.
            ALTER TABLE {_schema}.jobs ADD COLUMN IF NOT EXISTS requeued_from uuid;
            ALTER TABLE {_schema}.jobs ADD COLUMN IF NOT EXISTS trace_parent text;
            ALTER TABLE {_schema}.jobs ADD COLUMN IF NOT EXISTS recurring_id text;

            -- Partial: only fired jobs carry a recurring id, and there are few definitions, so this
            -- indexes a small slice and leaves the claim path's main table untouched (§11.26).
            CREATE INDEX IF NOT EXISTS ix_jobs_recurring
                ON {_schema}.jobs (recurring_id, created_at DESC, id DESC)
                WHERE recurring_id IS NOT NULL;
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _initialized = true;
    }

    // ---------------------------------------------------------------- IJobStorage

    public async ValueTask<IReadOnlyList<JobId>> EnqueueAsync(IReadOnlyList<JobRecord> jobs, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var ids = new JobId[jobs.Count];
        var wakeups = new HashSet<string>(StringComparer.Ordinal);

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        for (var i = 0; i < jobs.Count; i++)
        {
            ids[i] = await InsertCoreAsync(conn, jobs[i], wakeups, ct).ConfigureAwait(false);
        }

        await NotifyAsync(conn, wakeups, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return ids;
    }

    public async ValueTask<IReadOnlyList<JobRecord>> ClaimAsync(ClaimRequest request, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var now = _time.GetUtcNow().ToUniversalTime();

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH c AS (
                SELECT id FROM {_schema}.jobs
                WHERE queue = ANY(@queues)
                  AND (state = 1 OR (state = 2 AND lease_until <= @now))
                ORDER BY priority DESC, seq
                LIMIT @max
                FOR UPDATE SKIP LOCKED)
            UPDATE {_schema}.jobs j
            SET state = 2, worker_id = @worker, lease_until = @until, attempt = j.attempt + 1
            FROM c WHERE j.id = c.id
            RETURNING {Prefixed("j")}, j.seq
            """;
        cmd.Parameters.AddWithValue("queues", request.Queues.ToArray());
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("worker", request.WorkerId);
        cmd.Parameters.AddWithValue("until", now + request.LeaseDuration);
        cmd.Parameters.AddWithValue("max", request.MaxCount);

        var claimed = new List<(JobRecord Job, long Seq)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                // By name, not position: this trails the JobColumns list, so a positional read here
                // silently reinterprets the wrong column the moment a column is added.
                claimed.Add((ReadJob(reader), reader.GetInt64(reader.GetOrdinal("seq"))));
            }
        }

        // RETURNING row order is not guaranteed — restore the contract order.
        return claimed
            .OrderByDescending(c => c.Job.Priority)
            .ThenBy(c => c.Seq)
            .Select(c => c.Job)
            .ToList();
    }

    public async ValueTask<IReadOnlyList<JobId>> RenewLeasesAsync(
        string workerId, IReadOnlyList<JobId> jobs, TimeSpan lease, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var until = _time.GetUtcNow().ToUniversalTime() + lease;

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE {_schema}.jobs SET lease_until = @until
            WHERE id = ANY(@ids) AND state = 2 AND worker_id = @worker
            RETURNING id, cancel_requested
            """;
        cmd.Parameters.AddWithValue("until", until);
        cmd.Parameters.AddWithValue("ids", jobs.Select(j => j.Value).ToArray());
        cmd.Parameters.AddWithValue("worker", workerId);

        var renewed = new List<JobId>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            // Cancel-requested jobs keep their (renewed) lease but are omitted from the
            // result — the worker disambiguates via GetJobAsync.
            if (!reader.GetBoolean(1))
            {
                renewed.Add(new JobId(reader.GetGuid(0)));
            }
        }

        return renewed;
    }

    public async ValueTask<bool> ApplyAsync(JobTransition transition, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var now = _time.GetUtcNow().ToUniversalTime();
        var wakeups = new HashSet<string>(StringComparer.Ordinal);

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        string set;
        switch (transition.TargetState)
        {
            case JobState.Succeeded or JobState.Dead or JobState.Cancelled:
                set = "state = @target, failures = @failures, last_error = COALESCE(@error, last_error), " +
                      "finished_at = @finished, worker_id = NULL, lease_until = NULL";
                break;
            case JobState.Failed:
                set = "state = @target, failures = @failures, last_error = @error, due_at = @due, " +
                      "worker_id = NULL, lease_until = NULL";
                break;
            case JobState.Enqueued: // release
                set = "state = @target, failures = @failures, worker_id = NULL, lease_until = NULL, due_at = NULL";
                break;
            default:
                throw new ArgumentException(
                    $"Invalid transition target state {transition.TargetState}.", nameof(transition));
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                UPDATE {_schema}.jobs SET {set}
                WHERE id = @id AND state = 2 AND worker_id = @worker AND attempt = @attempt
                RETURNING queue
                """;
            cmd.Parameters.AddWithValue("target", (int)transition.TargetState);
            cmd.Parameters.AddWithValue("failures", transition.Failures);
            cmd.Parameters.AddWithValue("error", Db(transition.Error));
            if (transition.TargetState is JobState.Succeeded or JobState.Dead or JobState.Cancelled)
            {
                cmd.Parameters.AddWithValue("finished", (transition.FinishedAt ?? now).ToUniversalTime());
            }
            else if (transition.TargetState == JobState.Failed)
            {
                cmd.Parameters.AddWithValue("due", Db(transition.DueAt?.ToUniversalTime()));
            }

            cmd.Parameters.AddWithValue("id", transition.JobId.Value);
            cmd.Parameters.AddWithValue("worker", transition.ExpectedWorkerId);
            cmd.Parameters.AddWithValue("attempt", transition.ExpectedAttempt);

            var queue = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (queue is null)
            {
                return false; // fence rejected — nothing changed, transaction discarded
            }

            if (transition.TargetState == JobState.Enqueued)
            {
                wakeups.Add((string)queue);
            }
        }

        // The fence has held, so this worker still owns the job and may advance the instance. A
        // stale revision throws, the transaction is never committed, and the fenced UPDATE above
        // rolls back with it — the whole transition is all-or-nothing.
        if (transition.Checkpoint is { } checkpoint)
        {
            await ApplyCheckpointAsync(conn, checkpoint, ct).ConfigureAwait(false);
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
            cmd.CommandText = $"""
                UPDATE {_schema}.jobs SET state = 1
                WHERE parent_id = @id AND state = 7
                RETURNING queue
                """;
            cmd.Parameters.AddWithValue("id", transition.JobId.Value);
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

        await NotifyAsync(conn, wakeups, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<bool> TryCancelAsync(JobId id, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var now = _time.GetUtcNow().ToUniversalTime();

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        int state;
        await using (var cmd = conn.CreateCommand())
        {
            // Locking the row serializes this decision against claims and applies.
            cmd.CommandText = $"SELECT state FROM {_schema}.jobs WHERE id = @id FOR UPDATE";
            cmd.Parameters.AddWithValue("id", id.Value);
            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (result is null)
            {
                return false;
            }

            state = (int)result;
        }

        var jobState = (JobState)state;
        if (jobState.IsTerminal())
        {
            return false;
        }

        await using (var cmd = conn.CreateCommand())
        {
            if (jobState == JobState.Processing)
            {
                cmd.CommandText = $"UPDATE {_schema}.jobs SET cancel_requested = TRUE WHERE id = @id";
            }
            else
            {
                cmd.CommandText = $"""
                    UPDATE {_schema}.jobs
                    SET state = 6, finished_at = @now, worker_id = NULL, lease_until = NULL, due_at = NULL
                    WHERE id = @id
                    """;
                cmd.Parameters.AddWithValue("now", now);
            }

            cmd.Parameters.AddWithValue("id", id.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (jobState != JobState.Processing)
        {
            await CancelAwaitingClosureAsync(conn, id, now, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<JobRecord?> GetJobAsync(JobId id, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {JobColumns} FROM {_schema}.jobs WHERE id = @id";
        cmd.Parameters.AddWithValue("id", id.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadJob(reader) : null;
    }

    public async ValueTask<int> ActivateDueJobsAsync(DateTimeOffset now, int batchSize, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH d AS (
                SELECT id FROM {_schema}.jobs
                WHERE state IN (0, 4) AND due_at <= @now
                ORDER BY due_at, seq
                LIMIT @max
                FOR UPDATE SKIP LOCKED)
            UPDATE {_schema}.jobs j SET state = 1, due_at = NULL
            FROM d WHERE j.id = d.id
            RETURNING j.queue
            """;
        cmd.Parameters.AddWithValue("now", now.ToUniversalTime());
        cmd.Parameters.AddWithValue("max", batchSize);

        var wakeups = new HashSet<string>(StringComparer.Ordinal);
        var activated = 0;
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                activated++;
                wakeups.Add(reader.GetString(0));
            }
        }

        await NotifyAsync(conn, wakeups, ct).ConfigureAwait(false);
        return activated;
    }

    public async ValueTask UpsertRecurringAsync(RecurringJobRecord record, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {_schema}.recurring
                (id, cron, queue, invocation, retry, priority, tenant_id,
                 next_fire_time, last_fire_time, created_at, updated_at)
            VALUES (@id, @cron, @queue, @invocation, @retry, @priority, @tenant,
                    @next, @last, @created, @updated)
            ON CONFLICT (id) DO UPDATE SET
                cron = EXCLUDED.cron,
                queue = EXCLUDED.queue,
                invocation = EXCLUDED.invocation,
                retry = EXCLUDED.retry,
                priority = EXCLUDED.priority,
                tenant_id = EXCLUDED.tenant_id,
                updated_at = EXCLUDED.updated_at,
                next_fire_time = CASE
                    WHEN {_schema}.recurring.cron IS DISTINCT FROM EXCLUDED.cron
                    THEN EXCLUDED.next_fire_time
                    ELSE {_schema}.recurring.next_fire_time END
            """;
        cmd.Parameters.AddWithValue("id", record.Id);
        cmd.Parameters.AddWithValue("cron", record.Cron);
        cmd.Parameters.AddWithValue("queue", record.Queue);
        AddJsonb(cmd, "invocation", record.Invocation);
        AddJsonb(cmd, "retry", record.Retry);
        cmd.Parameters.AddWithValue("priority", record.Priority);
        cmd.Parameters.AddWithValue("tenant", Db(record.TenantId));
        cmd.Parameters.AddWithValue("next", record.NextFireTime.ToUniversalTime());
        cmd.Parameters.AddWithValue("last", Db(record.LastFireTime?.ToUniversalTime()));
        cmd.Parameters.AddWithValue("created", record.CreatedAt.ToUniversalTime());
        cmd.Parameters.AddWithValue("updated", record.UpdatedAt.ToUniversalTime());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<RecurringJobRecord?> GetRecurringAsync(string id, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {RecurringColumns} FROM {_schema}.recurring WHERE id = @id";
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRecurring(reader) : null;
    }

    public async ValueTask RemoveRecurringAsync(string id, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {_schema}.recurring WHERE id = @id";
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<RecurringJobRecord>> GetDueRecurringAsync(
        DateTimeOffset now, int batchSize, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {RecurringColumns} FROM {_schema}.recurring
            WHERE next_fire_time <= @now
            ORDER BY next_fire_time, id
            LIMIT @max
            """;
        cmd.Parameters.AddWithValue("now", now.ToUniversalTime());
        cmd.Parameters.AddWithValue("max", batchSize);

        var due = new List<RecurringJobRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            due.Add(ReadRecurring(reader));
        }

        return due;
    }

    public async ValueTask<bool> TryFireRecurringAsync(
        string id, DateTimeOffset expectedFireTime, DateTimeOffset nextFireTime,
        JobRecord job, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var wakeups = new HashSet<string>(StringComparer.Ordinal);

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                UPDATE {_schema}.recurring
                SET next_fire_time = @next, last_fire_time = @expected, updated_at = @now
                WHERE id = @id AND next_fire_time = @expected
                """;
            cmd.Parameters.AddWithValue("next", nextFireTime.ToUniversalTime());
            cmd.Parameters.AddWithValue("expected", expectedFireTime.ToUniversalTime());
            cmd.Parameters.AddWithValue("now", _time.GetUtcNow().ToUniversalTime());
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            {
                return false; // fence lost — another node fired this occurrence
            }
        }

        await InsertCoreAsync(conn, job, wakeups, ct).ConfigureAwait(false);
        await NotifyAsync(conn, wakeups, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    // ---------------------------------------------------------------- IWorkflowStorage

    public async ValueTask CreateInstanceAsync(WorkflowInstanceRecord instance, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {_schema}.workflow_instances
                (id, definition_id, definition_version, state, data_json, cursor_json,
                 revision, tenant_id, created_at, updated_at)
            VALUES (@id, @def, @ver, @state, @data, @cursor, 1, @tenant, @created, @updated)
            """;
        cmd.Parameters.AddWithValue("id", instance.Id.Value);
        cmd.Parameters.AddWithValue("def", instance.DefinitionId);
        cmd.Parameters.AddWithValue("ver", instance.DefinitionVersion);
        cmd.Parameters.AddWithValue("state", (int)instance.State);
        cmd.Parameters.Add(new NpgsqlParameter("data", NpgsqlDbType.Jsonb) { Value = instance.DataJson });
        cmd.Parameters.Add(new NpgsqlParameter("cursor", NpgsqlDbType.Jsonb) { Value = Db(instance.CursorJson) });
        cmd.Parameters.AddWithValue("tenant", Db(instance.TenantId));
        cmd.Parameters.AddWithValue("created", instance.CreatedAt.ToUniversalTime());
        cmd.Parameters.AddWithValue("updated", instance.UpdatedAt.ToUniversalTime());
        try
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new MillraceConcurrencyException($"Workflow instance '{instance.Id}' already exists.");
        }
    }

    public async ValueTask<WorkflowInstanceRecord?> GetInstanceAsync(WorkflowInstanceId id, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, definition_id, definition_version, state, data_json, cursor_json,
                   revision, tenant_id, created_at, updated_at
            FROM {_schema}.workflow_instances WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("id", id.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new WorkflowInstanceRecord
        {
            Id = new WorkflowInstanceId(reader.GetGuid(0)),
            DefinitionId = reader.GetString(1),
            DefinitionVersion = reader.GetInt32(2),
            State = (WorkflowInstanceState)reader.GetInt32(3),
            DataJson = reader.GetString(4),
            CursorJson = reader.IsDBNull(5) ? null : reader.GetString(5),
            Revision = reader.GetInt64(6),
            TenantId = reader.IsDBNull(7) ? null : reader.GetString(7),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(8),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(9),
        };
    }

    public async ValueTask UpdateInstanceAsync(
        WorkflowInstanceRecord instance, long expectedRevision, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await UpdateInstanceCoreAsync(conn, instance, expectedRevision, ct).ConfigureAwait(false);
    }

    /// <summary>Applies the checkpoint carried by a transition, on that transition's connection.</summary>
    private Task ApplyCheckpointAsync(NpgsqlConnection conn, WorkflowCheckpoint checkpoint, CancellationToken ct)
        => UpdateInstanceCoreAsync(conn, checkpoint.Instance, checkpoint.ExpectedRevision, ct);

    /// <summary>
    /// The single instance-update implementation, shared by the standalone call and the
    /// transition-carried checkpoint.
    /// </summary>
    /// <remarks>
    /// Shared deliberately: the two paths must be indistinguishable in effect, and two copies of
    /// this SQL would drift the moment a column is added.
    /// </remarks>
    private async Task UpdateInstanceCoreAsync(
        NpgsqlConnection conn, WorkflowInstanceRecord instance, long expectedRevision, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE {_schema}.workflow_instances
            SET definition_id = @def, definition_version = @ver, state = @state,
                data_json = @data, cursor_json = @cursor, tenant_id = @tenant,
                updated_at = @updated, revision = @expected + 1
            WHERE id = @id AND revision = @expected
            """;
        cmd.Parameters.AddWithValue("def", instance.DefinitionId);
        cmd.Parameters.AddWithValue("ver", instance.DefinitionVersion);
        cmd.Parameters.AddWithValue("state", (int)instance.State);
        cmd.Parameters.Add(new NpgsqlParameter("data", NpgsqlDbType.Jsonb) { Value = instance.DataJson });
        cmd.Parameters.Add(new NpgsqlParameter("cursor", NpgsqlDbType.Jsonb) { Value = Db(instance.CursorJson) });
        cmd.Parameters.AddWithValue("tenant", Db(instance.TenantId));
        cmd.Parameters.AddWithValue("updated", instance.UpdatedAt.ToUniversalTime());
        cmd.Parameters.AddWithValue("expected", expectedRevision);
        cmd.Parameters.AddWithValue("id", instance.Id.Value);

        if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
        {
            // Stale revision and missing instance are deliberately indistinguishable.
            throw new MillraceConcurrencyException(
                $"Workflow instance '{instance.Id}' revision conflict (expected {expectedRevision}).");
        }
    }

    public async ValueTask AddBookmarkAsync(BookmarkRecord bookmark, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await InsertBookmarkAsync(conn, bookmark, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The single bookmark-insert implementation, shared by the standalone call and the
    /// transition-carried inserts, so the two cannot diverge.
    /// </summary>
    private async Task InsertBookmarkAsync(NpgsqlConnection conn, BookmarkRecord bookmark, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {_schema}.bookmarks
                (id, instance_id, signal_name, correlation_id, payload_type_name, created_at)
            VALUES (@id, @instance, @signal, @correlation, @payload, @created)
            """;
        cmd.Parameters.AddWithValue("id", bookmark.Id);
        cmd.Parameters.AddWithValue("instance", bookmark.InstanceId.Value);
        cmd.Parameters.AddWithValue("signal", bookmark.SignalName);
        cmd.Parameters.AddWithValue("correlation", bookmark.CorrelationId);
        cmd.Parameters.AddWithValue("payload", Db(bookmark.PayloadTypeName));
        cmd.Parameters.AddWithValue("created", bookmark.CreatedAt.ToUniversalTime());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<BookmarkRecord?> ConsumeBookmarkAsync(
        string signalName, string correlationId, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            DELETE FROM {_schema}.bookmarks
            WHERE id = (
                SELECT id FROM {_schema}.bookmarks
                WHERE signal_name = @signal AND correlation_id = @correlation
                ORDER BY created_at, id
                LIMIT 1
                FOR UPDATE SKIP LOCKED)
            RETURNING id, instance_id, signal_name, correlation_id, payload_type_name, created_at
            """;
        cmd.Parameters.AddWithValue("signal", signalName);
        cmd.Parameters.AddWithValue("correlation", correlationId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new BookmarkRecord
        {
            Id = reader.GetGuid(0),
            InstanceId = new WorkflowInstanceId(reader.GetGuid(1)),
            SignalName = reader.GetString(2),
            CorrelationId = reader.GetString(3),
            PayloadTypeName = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(5),
        };
    }

    // ---------------------------------------------------------------- IStorageNotifier

    public async IAsyncEnumerable<QueueSignal> ListenAsync(
        IReadOnlySet<string> queues, [EnumeratorCancellation] CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var signals = Channel.CreateUnbounded<QueueSignal>(new UnboundedChannelOptions { SingleReader = true });

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        conn.Notification += (_, e) =>
        {
            if (queues.Contains(e.Payload))
            {
                signals.Writer.TryWrite(new QueueSignal(e.Payload));
            }
        };

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"LISTEN \"{_channel}\"";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        while (!ct.IsCancellationRequested)
        {
            await conn.WaitAsync(ct).ConfigureAwait(false); // pumps Notification events
            while (signals.Reader.TryRead(out var signal))
            {
                yield return signal;
            }
        }
    }

    // ---------------------------------------------------------------- internals

    /// <summary>
    /// Inserts one record with full EnqueueAsync semantics inside the ambient transaction:
    /// the Awaiting fixup takes a parent row lock (serializing against the parent's terminal
    /// apply), idempotency dedup rides the partial unique index, and the dedup lookup retries
    /// to linearize against concurrent terminal key release.
    /// </summary>
    private async Task<JobId> InsertCoreAsync(
        NpgsqlConnection conn, JobRecord record, HashSet<string> wakeups, CancellationToken ct)
    {
        var effective = record;

        if (record.State == JobState.Awaiting)
        {
            if (record.ParentId is not { } parentId)
            {
                throw new ArgumentException($"Job '{record.Id}' is Awaiting but has no ParentId.", nameof(record));
            }

            int parentState;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT state FROM {_schema}.jobs WHERE id = @id FOR UPDATE";
                cmd.Parameters.AddWithValue("id", parentId.Value);
                var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (result is null)
                {
                    throw new MillraceParentJobNotFoundException(parentId);
                }

                parentState = (int)result;
            }

            effective = (JobState)parentState switch
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

        var dedup = effective.IdempotencyKey is not null && !effective.State.IsTerminal();
        for (var round = 0; ; round++)
        {
            await using (var cmd = conn.CreateCommand())
            {
                var conflict = dedup
                    ? $"ON CONFLICT (tenant_id, idempotency_key) WHERE idempotency_key IS NOT NULL AND state IN ({ActiveStates}) DO NOTHING"
                    : string.Empty;
                cmd.CommandText = $"""
                    INSERT INTO {_schema}.jobs ({JobColumns})
                    VALUES (@id, @queue, @state, @priority, @invocation, @retry, @created, @due,
                            @worker, @lease, @attempt, @failures, @cancel, @key, @tenant,
                            @parent, @error, @finished, @wf, @activity, @requeued, @trace, @recurring)
                    {conflict}
                    RETURNING id
                    """;
                cmd.Parameters.AddWithValue("id", effective.Id.Value);
                cmd.Parameters.AddWithValue("queue", effective.Queue);
                cmd.Parameters.AddWithValue("state", (int)effective.State);
                cmd.Parameters.AddWithValue("priority", effective.Priority);
                AddJsonb(cmd, "invocation", effective.Invocation);
                AddJsonb(cmd, "retry", effective.Retry);
                cmd.Parameters.AddWithValue("created", effective.CreatedAt.ToUniversalTime());
                cmd.Parameters.AddWithValue("due", Db(effective.DueAt?.ToUniversalTime()));
                cmd.Parameters.AddWithValue("worker", Db(effective.WorkerId));
                cmd.Parameters.AddWithValue("lease", Db(effective.LeaseUntil?.ToUniversalTime()));
                cmd.Parameters.AddWithValue("attempt", effective.Attempt);
                cmd.Parameters.AddWithValue("failures", effective.Failures);
                cmd.Parameters.AddWithValue("cancel", effective.CancelRequested);
                cmd.Parameters.AddWithValue("key", Db(effective.IdempotencyKey));
                cmd.Parameters.AddWithValue("tenant", Db(effective.TenantId));
                cmd.Parameters.AddWithValue("parent", Db(effective.ParentId?.Value));
                cmd.Parameters.AddWithValue("error", Db(effective.LastError));
                cmd.Parameters.AddWithValue("finished", Db(effective.FinishedAt?.ToUniversalTime()));
                cmd.Parameters.AddWithValue("wf", Db(effective.WorkflowInstanceId?.Value));
                cmd.Parameters.AddWithValue("activity", Db(effective.ActivityNodeId));
        cmd.Parameters.AddWithValue("requeued", Db(effective.RequeuedFrom?.Value));
        cmd.Parameters.AddWithValue("trace", Db(effective.TraceParent));
        cmd.Parameters.AddWithValue("recurring", Db(effective.RecurringId));

                if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is Guid inserted)
                {
                    if (effective.State == JobState.Enqueued)
                    {
                        wakeups.Add(effective.Queue);
                    }

                    return new JobId(inserted);
                }
            }

            // Conflict: an active job holds the key — return its id (positional contract).
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT id FROM {_schema}.jobs
                    WHERE tenant_id IS NOT DISTINCT FROM @tenant AND idempotency_key = @key
                      AND state IN ({ActiveStates})
                    """;
                cmd.Parameters.AddWithValue("tenant", Db(effective.TenantId));
                cmd.Parameters.AddWithValue("key", effective.IdempotencyKey!);
                if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is Guid holder)
                {
                    return new JobId(holder);
                }
            }

            // The holder went terminal between our insert and lookup (read-committed window):
            // retry — the loop terminates because someone always makes progress.
            if (round >= 4)
            {
                throw new MillraceStorageException(
                    $"Could not resolve idempotency key '{effective.IdempotencyKey}' after {round + 1} attempts.");
            }
        }
    }

    private async Task CancelAwaitingClosureAsync(
        NpgsqlConnection conn, JobId rootId, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH RECURSIVE descendants AS (
                SELECT id FROM {_schema}.jobs WHERE parent_id = @root AND state = 7
                UNION ALL
                SELECT j.id FROM {_schema}.jobs j
                JOIN descendants d ON j.parent_id = d.id
                WHERE j.state = 7)
            UPDATE {_schema}.jobs
            SET state = 6, finished_at = @now, worker_id = NULL, lease_until = NULL, due_at = NULL
            WHERE id IN (SELECT id FROM descendants)
            """;
        cmd.Parameters.AddWithValue("root", rootId.Value);
        cmd.Parameters.AddWithValue("now", now);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task NotifyAsync(NpgsqlConnection conn, HashSet<string> wakeups, CancellationToken ct)
    {
        // pg_notify inside the transaction: PostgreSQL delivers on commit, never on rollback.
        foreach (var queue in wakeups)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT pg_notify(@channel, @queue)";
            cmd.Parameters.AddWithValue("channel", _channel);
            cmd.Parameters.AddWithValue("queue", queue);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private async ValueTask EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized || !_autoCreate)
        {
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

    private const string RecurringColumns =
        "id, cron, queue, invocation, retry, priority, tenant_id, next_fire_time, " +
        "last_fire_time, created_at, updated_at";

    private static JobRecord ReadJob(NpgsqlDataReader reader) => new()
    {
        Id = new JobId(reader.GetGuid(0)),
        Queue = reader.GetString(1),
        State = (JobState)reader.GetInt32(2),
        Priority = reader.GetInt32(3),
        Invocation = JsonSerializer.Deserialize<JobInvocation>(reader.GetString(4), Json)!,
        Retry = JsonSerializer.Deserialize<Retry>(reader.GetString(5), Json)!,
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(6),
        DueAt = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
        WorkerId = reader.IsDBNull(8) ? null : reader.GetString(8),
        LeaseUntil = reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
        Attempt = reader.GetInt32(10),
        Failures = reader.GetInt32(11),
        CancelRequested = reader.GetBoolean(12),
        IdempotencyKey = reader.IsDBNull(13) ? null : reader.GetString(13),
        TenantId = reader.IsDBNull(14) ? null : reader.GetString(14),
        ParentId = reader.IsDBNull(15) ? null : new JobId(reader.GetGuid(15)),
        LastError = reader.IsDBNull(16) ? null : reader.GetString(16),
        FinishedAt = reader.IsDBNull(17) ? null : reader.GetFieldValue<DateTimeOffset>(17),
        WorkflowInstanceId = reader.IsDBNull(18) ? null : new WorkflowInstanceId(reader.GetGuid(18)),
        ActivityNodeId = reader.IsDBNull(19) ? null : reader.GetString(19),
        RequeuedFrom = reader.IsDBNull(20) ? null : new JobId(reader.GetGuid(20)),
        TraceParent = reader.IsDBNull(21) ? null : reader.GetString(21),
        RecurringId = reader.IsDBNull(22) ? null : reader.GetString(22),
    };

    private static RecurringJobRecord ReadRecurring(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Cron = reader.GetString(1),
        Queue = reader.GetString(2),
        Invocation = JsonSerializer.Deserialize<JobInvocation>(reader.GetString(3), Json)!,
        Retry = JsonSerializer.Deserialize<Retry>(reader.GetString(4), Json)!,
        Priority = reader.GetInt32(5),
        TenantId = reader.IsDBNull(6) ? null : reader.GetString(6),
        NextFireTime = reader.GetFieldValue<DateTimeOffset>(7),
        LastFireTime = reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(9),
        UpdatedAt = reader.GetFieldValue<DateTimeOffset>(10),
    };

    private static void AddJsonb(NpgsqlCommand cmd, string name, object value)
        => cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(value, value.GetType(), Json),
        });

    private static string Prefixed(string alias)
        => string.Join(", ", JobColumns.Split(", ").Select(c => $"{alias}.{c}"));

    private static object Db(object? value) => value ?? DBNull.Value;
}
