using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Millrace.Storage.Monitoring;

namespace Millrace.Storage.SqlServer;

/// <summary>
/// The SQL Server provider (ARCHITECTURE.md §11.2, §4.3).
/// </summary>
/// <remarks>
/// <para>
/// Claims with <c>UPDLOCK, READPAST, ROWLOCK</c> over an ordered CTE — SQL Server's equivalent of
/// <c>FOR UPDATE SKIP LOCKED</c>. Advertises no notification capability, because SQL Server has no
/// <c>LISTEN/NOTIFY</c>, so workers fall back to adaptive polling exactly as §4 P3 intends.
/// </para>
/// <para>
/// Three dialect differences drive most of what looks unusual here. SQL Server has no row-value
/// comparison, so keyset predicates are expanded by hand. Its unique indexes treat NULLs as equal,
/// which is what the untenanted idempotency scope wants anyway. And <c>uniqueidentifier</c> sorts
/// in an internal mixed-endian layout that matches neither RFC 4122 nor
/// <see cref="Guid.CompareTo(Guid)"/> — so wherever the contract breaks a tie on an id, this
/// provider orders by <c>CAST(id AS char(36))</c>, the canonical hex form whose lexicographic order
/// <em>is</em> RFC 4122. <c>CAST(… AS binary(16))</c> looks right and is not.
/// </para>
/// </remarks>
public sealed class SqlServerStorage : IJobStorage, IWorkflowStorage, IMonitoringStorage
{
    private const string ActiveStates = "0, 1, 2, 4, 7"; // Scheduled, Enqueued, Processing, Failed, Awaiting

    private const string JobColumns =
        "id, queue, state, priority, invocation, retry, created_at, due_at, worker_id, " +
        "lease_until, attempt, failures, cancel_requested, idempotency_key, tenant_id, " +
        "parent_id, last_error, finished_at, workflow_instance_id, activity_node_id, requeued_from, " +
        "trace_parent, recurring_id";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);

    private readonly string _connectionString;
    private readonly TimeProvider _time;
    private readonly string _schema;
    private readonly bool _autoCreate;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private volatile bool _initialized;

    public SqlServerStorage(
        string connectionString, TimeProvider? time = null, SqlServerStorageOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _time = time ?? TimeProvider.System;
        var opts = options ?? new SqlServerStorageOptions();
        _schema = opts.Schema;
        _autoCreate = opts.AutoCreateSchema;
    }

    /// <summary>No push mechanism, so the engine polls (§4 P3).</summary>
    public StorageCapabilities Capabilities => StorageCapabilities.None;

    // ---------------------------------------------------------------- schema

    public async ValueTask InitializeAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        // CREATE SCHEMA must be the only statement in its batch, hence EXEC.
        cmd.CommandText = $"""
            IF SCHEMA_ID('{_schema}') IS NULL EXEC('CREATE SCHEMA [{_schema}]');

            IF OBJECT_ID('{_schema}.jobs') IS NULL
            CREATE TABLE {_schema}.jobs (
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

            IF IndexProperty(OBJECT_ID('{_schema}.jobs'), 'ux_jobs_active_key', 'IndexID') IS NULL
            EXEC('CREATE UNIQUE INDEX ux_jobs_active_key ON {_schema}.jobs (tenant_id, idempotency_key)
                  WHERE idempotency_key IS NOT NULL AND state IN ({ActiveStates})');

            IF IndexProperty(OBJECT_ID('{_schema}.jobs'), 'ix_jobs_claim', 'IndexID') IS NULL
            EXEC('CREATE INDEX ix_jobs_claim ON {_schema}.jobs (queue, priority DESC, seq) WHERE state IN (1, 2)');

            IF IndexProperty(OBJECT_ID('{_schema}.jobs'), 'ix_jobs_due', 'IndexID') IS NULL
            EXEC('CREATE INDEX ix_jobs_due ON {_schema}.jobs (due_at, seq) WHERE state IN (0, 4)');

            IF IndexProperty(OBJECT_ID('{_schema}.jobs'), 'ix_jobs_parent', 'IndexID') IS NULL
            EXEC('CREATE INDEX ix_jobs_parent ON {_schema}.jobs (parent_id) WHERE state = 7');

            IF IndexProperty(OBJECT_ID('{_schema}.jobs'), 'ix_jobs_monitor', 'IndexID') IS NULL
            EXEC('CREATE INDEX ix_jobs_monitor ON {_schema}.jobs (created_at DESC, id DESC)');

            IF OBJECT_ID('{_schema}.recurring') IS NULL
            CREATE TABLE {_schema}.recurring (
                id nvarchar(400) NOT NULL PRIMARY KEY,
                cron nvarchar(200) NOT NULL,
                queue nvarchar(200) NOT NULL,
                invocation nvarchar(max) NOT NULL,
                retry nvarchar(max) NOT NULL,
                priority int NOT NULL DEFAULT 0,
                tenant_id nvarchar(200) NULL,
                next_fire_time datetimeoffset NOT NULL,
                last_fire_time datetimeoffset NULL,
                created_at datetimeoffset NOT NULL,
                updated_at datetimeoffset NOT NULL);

            IF OBJECT_ID('{_schema}.workflow_instances') IS NULL
            CREATE TABLE {_schema}.workflow_instances (
                id uniqueidentifier NOT NULL PRIMARY KEY,
                definition_id nvarchar(400) NOT NULL,
                definition_version int NOT NULL,
                state int NOT NULL,
                data_json nvarchar(max) NOT NULL,
                cursor_json nvarchar(max) NULL,
                revision bigint NOT NULL,
                tenant_id nvarchar(200) NULL,
                created_at datetimeoffset NOT NULL,
                updated_at datetimeoffset NOT NULL);

            IF OBJECT_ID('{_schema}.bookmarks') IS NULL
            CREATE TABLE {_schema}.bookmarks (
                id uniqueidentifier NOT NULL PRIMARY KEY,
                instance_id uniqueidentifier NOT NULL,
                signal_name nvarchar(400) NOT NULL,
                correlation_id nvarchar(400) NOT NULL,
                payload_type_name nvarchar(800) NULL,
                created_at datetimeoffset NOT NULL);

            IF IndexProperty(OBJECT_ID('{_schema}.bookmarks'), 'ix_bookmarks_lookup', 'IndexID') IS NULL
            EXEC('CREATE INDEX ix_bookmarks_lookup ON {_schema}.bookmarks (signal_name, correlation_id, created_at, id)');

            -- Columns added after 0.1 (§11.25). They live here rather than in the CREATE TABLE
            -- above because that statement is skipped entirely once the table exists: a column
            -- added there reaches new databases and silently never reaches upgraded ones, which is
            -- how requeued_from and trace_parent shipped in 0.4 unable to load on any 0.3 database.
            -- One place per column, and it is this one.
            IF COL_LENGTH('{_schema}.jobs', 'requeued_from') IS NULL
                ALTER TABLE {_schema}.jobs ADD requeued_from uniqueidentifier NULL;

            IF COL_LENGTH('{_schema}.jobs', 'trace_parent') IS NULL
                ALTER TABLE {_schema}.jobs ADD trace_parent nvarchar(200) NULL;

            IF COL_LENGTH('{_schema}.jobs', 'recurring_id') IS NULL
                ALTER TABLE {_schema}.jobs ADD recurring_id nvarchar(400) NULL;

            -- Attempt history (§11.27). Only failed and interrupted executions land here, so a
            -- healthy queue never writes to this table at all. The cascade is defensive: the
            -- contract has no job-delete path today, and if one arrives the history must not
            -- outlive its job.
            IF OBJECT_ID('{_schema}.job_attempts') IS NULL
            CREATE TABLE {_schema}.job_attempts (
                job_id uniqueidentifier NOT NULL
                    REFERENCES {_schema}.jobs (id) ON DELETE CASCADE,
                attempt int NOT NULL,
                outcome int NOT NULL,
                recorded_at datetimeoffset NOT NULL,
                worker_id nvarchar(200) NULL,
                error nvarchar(max) NULL,
                CONSTRAINT pk_job_attempts PRIMARY KEY (job_id, attempt));
            """;

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // A separate batch: the filtered index cannot be created in the same batch that adds the
        // column it indexes, because the whole batch is parsed before any of it runs.
        await using var indexes = conn.CreateCommand();
        indexes.CommandText = $"""
            IF IndexProperty(OBJECT_ID('{_schema}.jobs'), 'ix_jobs_recurring', 'IndexID') IS NULL
            EXEC('CREATE INDEX ix_jobs_recurring ON {_schema}.jobs (recurring_id, created_at DESC, id DESC)
                  WHERE recurring_id IS NOT NULL');
            """;

        await indexes.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _initialized = true;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
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

    // ---------------------------------------------------------------- IJobStorage

    public async ValueTask<IReadOnlyList<JobId>> EnqueueAsync(IReadOnlyList<JobRecord> jobs, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var ids = new JobId[jobs.Count];

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        for (var i = 0; i < jobs.Count; i++)
        {
            ids[i] = await InsertCoreAsync(conn, tx, jobs[i], ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return ids;
    }

    /// <summary>
    /// Inserts one job, honouring the idempotency scope and the continuation fixup.
    /// </summary>
    private async Task<JobId> InsertCoreAsync(
        SqlConnection conn, SqlTransaction tx, JobRecord record, CancellationToken ct)
    {
        if (record.State is not (JobState.Scheduled or JobState.Enqueued or JobState.Awaiting))
        {
            throw new ArgumentException(
                $"Job '{record.Id}' has non-insertable state {record.State}.", nameof(record));
        }

        var effective = record;

        if (record.State == JobState.Awaiting)
        {
            // Locked so the fixup serializes with the parent's terminal transition: once both
            // commit, in either order, the child is Enqueued or Cancelled, never left Awaiting.
            var parentId = record.ParentId
                ?? throw new ArgumentException($"Awaiting job '{record.Id}' has no parent.", nameof(record));

            await using var lookup = Command(conn, tx,
                $"SELECT state FROM {_schema}.jobs WITH (UPDLOCK, HOLDLOCK) WHERE id = @id",
                ("@id", parentId.Value));

            var parentState = await lookup.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (parentState is null)
            {
                throw new MillraceParentJobNotFoundException(parentId);
            }

            effective = (JobState)(int)parentState switch
            {
                JobState.Succeeded => record with { State = JobState.Enqueued, ParentId = record.ParentId },
                JobState.Dead or JobState.Cancelled => record with
                {
                    State = JobState.Cancelled,
                    FinishedAt = _time.GetUtcNow(),
                },
                _ => record,
            };
        }

        if (effective.IdempotencyKey is { } key)
        {
            var existing = await ExistingActiveKeyAsync(conn, tx, effective.TenantId, key, ct).ConfigureAwait(false);
            if (existing is { } held)
            {
                return held; // §4.2.6: a duplicate active key inserts nothing
            }
        }

        try
        {
            await using var insert = Command(conn, tx,
                $"""
                 INSERT INTO {_schema}.jobs ({JobColumns})
                 VALUES (@id, @queue, @state, @priority, @invocation, @retry, @created, @due, @worker,
                         @lease, @attempt, @failures, @cancel, @key, @tenant, @parent, @error,
                         @finished, @wf, @activity, @requeued, @trace, @recurring)
                 """,
                ("@id", effective.Id.Value),
                ("@queue", effective.Queue),
                ("@state", (int)effective.State),
                ("@priority", effective.Priority),
                ("@invocation", JsonSerializer.Serialize(effective.Invocation, Json)),
                ("@retry", JsonSerializer.Serialize(effective.Retry, Json)),
                ("@created", effective.CreatedAt),
                ("@due", Db(effective.DueAt)),
                ("@worker", Db(effective.WorkerId)),
                ("@lease", Db(effective.LeaseUntil)),
                ("@attempt", effective.Attempt),
                ("@failures", effective.Failures),
                ("@cancel", effective.CancelRequested),
                ("@key", Db(effective.IdempotencyKey)),
                ("@tenant", Db(effective.TenantId)),
                ("@parent", Db(effective.ParentId?.Value)),
                ("@error", Db(effective.LastError)),
                ("@finished", Db(effective.FinishedAt)),
                ("@wf", Db(effective.WorkflowInstanceId?.Value)),
                ("@activity", Db(effective.ActivityNodeId)),
                ("@requeued", Db(effective.RequeuedFrom?.Value)),
                ("@trace", Db(effective.TraceParent)),
                ("@recurring", Db(effective.RecurringId)));

            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return effective.Id;
        }
        catch (SqlException e) when (e.Number is 2601 or 2627 && effective.IdempotencyKey is not null)
        {
            // Lost the race to another enqueue holding the same active key. Its id is the answer,
            // which is what linearizing against a concurrent terminal release means.
            var winner = await ExistingActiveKeyAsync(conn, tx, effective.TenantId, effective.IdempotencyKey, ct)
                .ConfigureAwait(false);
            if (winner is { } held)
            {
                return held;
            }

            throw; // a genuine duplicate id, not a key race
        }
    }

    private async Task<JobId?> ExistingActiveKeyAsync(
        SqlConnection conn, SqlTransaction tx, string? tenantId, string key, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx,
            $"""
             SELECT TOP 1 id FROM {_schema}.jobs WITH (UPDLOCK, HOLDLOCK)
             WHERE idempotency_key = @key
               AND ((@tenant IS NULL AND tenant_id IS NULL) OR tenant_id = @tenant)
               AND state IN ({ActiveStates})
             """,
            ("@key", key), ("@tenant", Db(tenantId)));

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is Guid id ? new JobId(id) : null;
    }

    public async ValueTask<IReadOnlyList<JobRecord>> ClaimAsync(ClaimRequest request, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var queues = request.Queues.Select((q, i) => (Name: $"@q{i}", Value: q)).ToList();
        if (queues.Count == 0 || request.MaxCount <= 0)
        {
            return [];
        }

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);

        // The ordered CTE is what makes this SKIP LOCKED-equivalent honour claim order: UPDATE TOP
        // on its own gives no ordering guarantee.
        await using var cmd = Command(conn, transaction: null,
            $"""
             DECLARE @prior TABLE (id uniqueidentifier, state int, attempt int, worker_id nvarchar(200));

             WITH claimable AS (
                 SELECT TOP (@max) *
                 FROM {_schema}.jobs WITH (UPDLOCK, READPAST, ROWLOCK)
                 WHERE queue IN ({string.Join(", ", queues.Select(q => q.Name))})
                   AND (state = 1 OR (state = 2 AND lease_until <= @now))
                 ORDER BY priority DESC, seq
             )
             UPDATE claimable
             SET state = 2, worker_id = @worker, lease_until = @lease, attempt = attempt + 1
             -- Two OUTPUT clauses: one captures the pre-update row, the other returns the claim to
             -- the caller. Only the rows actually claimed reach @prior, which is why the
             -- interruptions below cannot be recorded by a separate pass — another worker may take
             -- a row this one was eligible for.
             OUTPUT deleted.id, deleted.state, deleted.attempt, deleted.worker_id INTO @prior
             OUTPUT {InsertedColumns()};

             -- A row still Processing when it is claimed again ended without ever reporting a
             -- verdict: the lease simply expired. That is the only moment an interruption becomes
             -- observable, because nothing arrives to record it (§11.27).
             INSERT INTO {_schema}.job_attempts (job_id, attempt, outcome, recorded_at, worker_id, error)
             SELECT p.id, p.attempt, 1, @now, p.worker_id, NULL
             FROM @prior p
             WHERE p.state = 2
               AND NOT EXISTS (SELECT 1 FROM {_schema}.job_attempts a
                               WHERE a.job_id = p.id AND a.attempt = p.attempt);

             -- Attempt numbers only ever increase, so "keep the most recent" is a comparison rather
             -- than an ORDER BY / LIMIT per job.
             DELETE a FROM {_schema}.job_attempts a
             INNER JOIN @prior p ON a.job_id = p.id
             WHERE a.attempt <= p.attempt - {JobAttemptRules.HistoryLimit};
             """,
            ("@max", request.MaxCount),
            ("@now", now),
            ("@worker", request.WorkerId),
            ("@lease", now + request.LeaseDuration));

        foreach (var (name, value) in queues)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        var claimed = new List<JobRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            claimed.Add(ReadJob(reader));
        }

        return claimed;
    }

    public async ValueTask<IReadOnlyList<JobId>> RenewLeasesAsync(
        string workerId, IReadOnlyList<JobId> jobs, TimeSpan lease, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        if (jobs.Count == 0)
        {
            return [];
        }

        var now = _time.GetUtcNow();
        var ids = jobs.Select((j, i) => (Name: $"@id{i}", Value: j.Value)).ToList();

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);

        // LeaseUntil is not consulted: an expired-but-unreclaimed lease is renewable. Cancel-
        // requested jobs still renew but are omitted from the result, which is how the worker
        // learns to check.
        await using var cmd = Command(conn, transaction: null,
            $"""
             UPDATE {_schema}.jobs SET lease_until = @lease
             OUTPUT inserted.id, inserted.cancel_requested
             WHERE id IN ({string.Join(", ", ids.Select(i => i.Name))})
               AND state = 2 AND worker_id = @worker
             """,
            ("@lease", now + lease), ("@worker", workerId));

        foreach (var (name, value) in ids)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        var renewed = new List<JobId>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
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
        var now = _time.GetUtcNow();

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        var set = transition.TargetState switch
        {
            JobState.Succeeded or JobState.Dead or JobState.Cancelled =>
                "state = @target, failures = @failures, last_error = COALESCE(@error, last_error), "
                + "finished_at = @finished, worker_id = NULL, lease_until = NULL",
            JobState.Failed =>
                "state = @target, failures = @failures, last_error = @error, due_at = @due, "
                + "worker_id = NULL, lease_until = NULL",
            JobState.Enqueued =>
                "state = @target, failures = @failures, worker_id = NULL, lease_until = NULL, due_at = NULL",
            _ => throw new ArgumentException(
                $"Invalid transition target state {transition.TargetState}.", nameof(transition)),
        };

        // The attempt row is written by the same statement as the fenced UPDATE, inside the same
        // transaction, so the timeline can never disagree with the counters it explains. The prior
        // failure count comes from `deleted`, which is what separates a job that just failed from
        // one dead-lettered without executing — a poison pill leaves the count untouched and has no
        // attempt to describe (§11.27).
        var history = $"""

             INSERT INTO {_schema}.job_attempts (job_id, attempt, outcome, recorded_at, worker_id, error)
             SELECT p.id, p.attempt, p.outcome, @now, p.worker_id, p.error
             FROM @outcome p
             WHERE p.outcome IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM {_schema}.job_attempts a
                               WHERE a.job_id = p.id AND a.attempt = p.attempt);

             DELETE a FROM {_schema}.job_attempts a
             INNER JOIN @outcome p ON a.job_id = p.id
             WHERE p.outcome IS NOT NULL AND a.attempt <= p.attempt - {JobAttemptRules.HistoryLimit};
             """;

        await using (var cmd = Command(conn, tx,
            $"""
             DECLARE @outcome TABLE (
                 id uniqueidentifier, attempt int, worker_id nvarchar(200),
                 outcome int NULL, error nvarchar(max) NULL);

             UPDATE {_schema}.jobs SET {set}
             OUTPUT deleted.id, deleted.attempt, deleted.worker_id,
                    CASE
                        WHEN @target = 4 THEN 0
                        WHEN @target = 5 AND @failures > deleted.failures THEN 0
                        WHEN @target = 1 THEN 1
                        ELSE NULL
                    END,
                    CASE WHEN @target = 4 OR (@target = 5 AND @failures > deleted.failures)
                         THEN @error ELSE NULL END
                 INTO @outcome
             WHERE id = @id AND state = 2 AND worker_id = @worker AND attempt = @attempt;

             IF @@ROWCOUNT = 0 SELECT 0 ELSE SELECT 1;
             {history}
             """,
            ("@now", now),
            ("@target", (int)transition.TargetState),
            ("@failures", transition.Failures),
            ("@error", Db(transition.Error)),
            ("@finished", Db(transition.FinishedAt ?? (transition.TargetState.IsTerminal() ? now : null))),
            ("@due", Db(transition.DueAt)),
            ("@id", transition.JobId.Value),
            ("@worker", transition.ExpectedWorkerId),
            ("@attempt", transition.ExpectedAttempt)))
        {
            // ExecuteScalar rather than ExecuteNonQuery: the row count now covers several
            // statements, so the fence result has to be reported explicitly.
            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not 1)
            {
                return false; // fence rejected — nothing changed, transaction discarded
            }
        }

        // Fence held, so this worker still owns the job and may advance the instance.
        if (transition.Checkpoint is { } checkpoint)
        {
            await UpdateInstanceCoreAsync(conn, tx, checkpoint.Instance, checkpoint.ExpectedRevision, ct)
                .ConfigureAwait(false);
        }

        foreach (var bookmark in transition.Bookmarks)
        {
            await InsertBookmarkAsync(conn, tx, bookmark, ct).ConfigureAwait(false);
        }

        foreach (var record in transition.Enqueue)
        {
            await InsertCoreAsync(conn, tx, record, ct).ConfigureAwait(false);
        }

        if (transition.ActivateContinuations)
        {
            await using var cmd = Command(conn, tx,
                $"UPDATE {_schema}.jobs SET state = 1 WHERE parent_id = @id AND state = 7",
                ("@id", transition.JobId.Value));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (transition.CancelContinuations)
        {
            await CancelClosureAsync(conn, tx, transition.JobId, now, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Cancels the transitive Awaiting-descendant closure of a job.</summary>
    private async Task CancelClosureAsync(
        SqlConnection conn, SqlTransaction tx, JobId root, DateTimeOffset now, CancellationToken ct)
    {
        // An activated child shields its own subtree: the recursion only follows nodes still
        // Awaiting, so a descendant that already started keeps its own children.
        await using var cmd = Command(conn, tx,
            $"""
             WITH closure AS (
                 SELECT id FROM {_schema}.jobs WHERE parent_id = @root AND state = 7
                 UNION ALL
                 SELECT j.id FROM {_schema}.jobs j
                 INNER JOIN closure c ON j.parent_id = c.id
                 WHERE j.state = 7
             )
             UPDATE {_schema}.jobs SET state = 6, finished_at = @now, worker_id = NULL, lease_until = NULL
             WHERE id IN (SELECT id FROM closure)
             """,
            ("@root", root.Value), ("@now", now));

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<bool> TryRunNowAsync(JobId id, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);

        // The state predicate is the fence: a job claimed between the operator's click and this
        // statement is no longer Failed, so it is left alone rather than yanked out from under the
        // worker running it. Attempt and failures are untouched (§11.32).
        await using var cmd = Command(conn, transaction: null,
            $"""
             UPDATE {_schema}.jobs SET state = 1, due_at = NULL
             WHERE id = @id AND state = 4
             """,
            ("@id", id.Value));

        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async ValueTask<bool> TryCancelAsync(JobId id, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        await using (var probe = Command(conn, tx,
            $"SELECT state FROM {_schema}.jobs WITH (UPDLOCK, HOLDLOCK) WHERE id = @id", ("@id", id.Value)))
        {
            if (await probe.ExecuteScalarAsync(ct).ConfigureAwait(false) is not int stateValue)
            {
                return false;
            }

            var state = (JobState)stateValue;
            if (state.IsTerminal())
            {
                return false;
            }

            if (state == JobState.Processing)
            {
                // Cooperative only: the flag never blocks a fenced apply, so a worker about to
                // finish may still win with Succeeded.
                await using var flag = Command(conn, tx,
                    $"UPDATE {_schema}.jobs SET cancel_requested = 1 WHERE id = @id", ("@id", id.Value));
                await flag.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return true;
            }
        }

        await using (var cancel = Command(conn, tx,
            $"""
             UPDATE {_schema}.jobs
             SET state = 6, finished_at = @now, worker_id = NULL, lease_until = NULL, due_at = NULL
             WHERE id = @id AND state IN (0, 1, 4, 7)
             """,
            ("@id", id.Value), ("@now", now)))
        {
            if (await cancel.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            {
                return false;
            }
        }

        await CancelClosureAsync(conn, tx, id, now, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<JobRecord?> GetJobAsync(JobId id, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = Command(conn, transaction: null,
            $"SELECT {JobColumns} FROM {_schema}.jobs WHERE id = @id", ("@id", id.Value));

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadJob(reader) : null;
    }

    public async ValueTask<int> ActivateDueJobsAsync(DateTimeOffset now, int batchSize, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = Command(conn, transaction: null,
            $"""
             WITH due AS (
                 SELECT TOP (@batch) *
                 FROM {_schema}.jobs WITH (UPDLOCK, READPAST, ROWLOCK)
                 WHERE state IN (0, 4) AND due_at <= @now
                 ORDER BY due_at, seq
             )
             UPDATE due SET state = 1, due_at = NULL
             """,
            ("@batch", batchSize), ("@now", now));

        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- recurring

    public async ValueTask UpsertRecurringAsync(RecurringJobRecord record, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);

        // NextFireTime is taken from the record only when the cron changed; otherwise the stored
        // schedule stands, so an upsert cannot rewind a definition that is about to fire.
        await using var cmd = Command(conn, transaction: null,
            $"""
             MERGE {_schema}.recurring WITH (HOLDLOCK) AS t
             USING (SELECT @id AS id) AS s ON t.id = s.id
             WHEN MATCHED THEN UPDATE SET
                 cron = @cron, queue = @queue, invocation = @invocation, retry = @retry,
                 priority = @priority, tenant_id = @tenant, updated_at = @updated,
                 next_fire_time = CASE WHEN t.cron <> @cron THEN @next ELSE t.next_fire_time END
             WHEN NOT MATCHED THEN INSERT
                 (id, cron, queue, invocation, retry, priority, tenant_id, next_fire_time,
                  last_fire_time, created_at, updated_at)
                 VALUES (@id, @cron, @queue, @invocation, @retry, @priority, @tenant, @next,
                         @last, @created, @updated);
             """,
            ("@id", record.Id),
            ("@cron", record.Cron),
            ("@queue", record.Queue),
            ("@invocation", JsonSerializer.Serialize(record.Invocation, Json)),
            ("@retry", JsonSerializer.Serialize(record.Retry, Json)),
            ("@priority", record.Priority),
            ("@tenant", Db(record.TenantId)),
            ("@next", record.NextFireTime),
            ("@last", Db(record.LastFireTime)),
            ("@created", record.CreatedAt),
            ("@updated", record.UpdatedAt));

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<RecurringJobRecord?> GetRecurringAsync(string id, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = Command(conn, transaction: null,
            $"SELECT {RecurringColumns} FROM {_schema}.recurring WHERE id = @id", ("@id", id));

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRecurring(reader) : null;
    }

    public async ValueTask RemoveRecurringAsync(string id, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = Command(conn, transaction: null,
            $"DELETE FROM {_schema}.recurring WHERE id = @id", ("@id", id));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<RecurringJobRecord>> GetDueRecurringAsync(
        DateTimeOffset now, int batchSize, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = Command(conn, transaction: null,
            $"""
             SELECT TOP (@batch) {RecurringColumns} FROM {_schema}.recurring
             WHERE next_fire_time <= @now ORDER BY next_fire_time, id
             """,
            ("@batch", batchSize), ("@now", now));

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
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        // Compare-and-set: exactly one node wins an occurrence, and the fired job is inserted in
        // the same transaction so there is no window between fencing and enqueueing.
        await using (var cas = Command(conn, tx,
            $"""
             UPDATE {_schema}.recurring
             SET next_fire_time = @next, last_fire_time = @expected
             WHERE id = @id AND next_fire_time = @expected
             """,
            ("@id", id), ("@expected", expectedFireTime), ("@next", nextFireTime)))
        {
            if (await cas.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            {
                return false;
            }
        }

        await InsertCoreAsync(conn, tx, job, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    // ---------------------------------------------------------------- IWorkflowStorage

    public async ValueTask CreateInstanceAsync(WorkflowInstanceRecord instance, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);

        try
        {
            await using var cmd = Command(conn, transaction: null,
                $"""
                 INSERT INTO {_schema}.workflow_instances
                     (id, definition_id, definition_version, state, data_json, cursor_json, revision,
                      tenant_id, created_at, updated_at)
                 VALUES (@id, @def, @ver, @state, @data, @cursor, 1, @tenant, @created, @updated)
                 """,
                ("@id", instance.Id.Value),
                ("@def", instance.DefinitionId),
                ("@ver", instance.DefinitionVersion),
                ("@state", (int)instance.State),
                ("@data", instance.DataJson),
                ("@cursor", Db(instance.CursorJson)),
                ("@tenant", Db(instance.TenantId)),
                ("@created", instance.CreatedAt),
                ("@updated", instance.UpdatedAt));

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqlException e) when (e.Number is 2601 or 2627)
        {
            throw new MillraceConcurrencyException(
                $"Workflow instance '{instance.Id}' already exists.");
        }
    }

    public async ValueTask<WorkflowInstanceRecord?> GetInstanceAsync(
        WorkflowInstanceId id, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = Command(conn, transaction: null,
            $"""
             SELECT id, definition_id, definition_version, state, data_json, cursor_json, revision,
                    tenant_id, created_at, updated_at
             FROM {_schema}.workflow_instances WHERE id = @id
             """,
            ("@id", id.Value));

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadInstance(reader) : null;
    }

    public async ValueTask UpdateInstanceAsync(
        WorkflowInstanceRecord instance, long expectedRevision, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await UpdateInstanceCoreAsync(conn, transaction: null, instance, expectedRevision, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Shared by the standalone call and the transition-carried checkpoint.</summary>
    private async Task UpdateInstanceCoreAsync(
        SqlConnection conn, SqlTransaction? transaction, WorkflowInstanceRecord instance,
        long expectedRevision, CancellationToken ct)
    {
        await using var cmd = Command(conn, transaction,
            $"""
             UPDATE {_schema}.workflow_instances
             SET definition_id = @def, definition_version = @ver, state = @state,
                 data_json = @data, cursor_json = @cursor, tenant_id = @tenant,
                 updated_at = @updated, revision = @expected + 1
             WHERE id = @id AND revision = @expected
             """,
            ("@def", instance.DefinitionId),
            ("@ver", instance.DefinitionVersion),
            ("@state", (int)instance.State),
            ("@data", instance.DataJson),
            ("@cursor", Db(instance.CursorJson)),
            ("@tenant", Db(instance.TenantId)),
            ("@updated", instance.UpdatedAt),
            ("@expected", expectedRevision),
            ("@id", instance.Id.Value));

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
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await InsertBookmarkAsync(conn, transaction: null, bookmark, ct).ConfigureAwait(false);
    }

    private async Task InsertBookmarkAsync(
        SqlConnection conn, SqlTransaction? transaction, BookmarkRecord bookmark, CancellationToken ct)
    {
        await using var cmd = Command(conn, transaction,
            $"""
             INSERT INTO {_schema}.bookmarks
                 (id, instance_id, signal_name, correlation_id, payload_type_name, created_at)
             VALUES (@id, @instance, @signal, @correlation, @payload, @created)
             """,
            ("@id", bookmark.Id),
            ("@instance", bookmark.InstanceId.Value),
            ("@signal", bookmark.SignalName),
            ("@correlation", bookmark.CorrelationId),
            ("@payload", Db(bookmark.PayloadTypeName)),
            ("@created", bookmark.CreatedAt));

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<BookmarkRecord?> ConsumeBookmarkAsync(
        string signalName, string correlationId, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);

        // Delete-with-OUTPUT is the at-most-once primitive: whoever's DELETE affects the row wins,
        // and the oldest match goes first so waits resume in order.
        await using var cmd = Command(conn, transaction: null,
            $"""
             WITH target AS (
                 SELECT TOP 1 * FROM {_schema}.bookmarks WITH (UPDLOCK, READPAST, ROWLOCK)
                 WHERE signal_name = @signal AND correlation_id = @correlation
                 -- The canonical hex form, whose lexicographic order is RFC 4122 byte order. Neither the
                 -- raw uniqueidentifier nor CAST(... AS binary(16)) gives that: both use SQL
                 -- Server's internal mixed-endian layout. Found by the conformance kit, twice.
                 ORDER BY created_at, CAST(id AS char(36))
             )
             DELETE FROM target
             OUTPUT deleted.id, deleted.instance_id, deleted.signal_name, deleted.correlation_id,
                    deleted.payload_type_name, deleted.created_at
             """,
            ("@signal", signalName), ("@correlation", correlationId));

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

    // ---------------------------------------------------------------- helpers

    private const string RecurringColumns =
        "id, cron, queue, invocation, retry, priority, tenant_id, next_fire_time, last_fire_time, " +
        "created_at, updated_at";

    private static string InsertedColumns()
        => string.Join(", ", JobColumns.Split(", ").Select(c => $"inserted.{c}"));

    private static SqlCommand Command(
        SqlConnection conn, SqlTransaction? transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = transaction;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return cmd;
    }

    private static object Db(object? value) => value ?? DBNull.Value;

    private static JobRecord ReadJob(SqlDataReader reader) => new()
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

    private static RecurringJobRecord ReadRecurring(SqlDataReader reader) => new()
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

    private static WorkflowInstanceRecord ReadInstance(SqlDataReader reader) => new()
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

    // ---------------------------------------------------------------- IMonitoringStorage

    public async ValueTask<JobStatistics> GetStatisticsAsync(TenantFilter tenant, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var (clause, parameter) = TenantClause(tenant);

        var jobsByState = Enum.GetValues<JobState>().ToDictionary(s => s, _ => 0L);
        var instancesByState = Enum.GetValues<WorkflowInstanceState>().ToDictionary(s => s, _ => 0L);
        var byQueue = new Dictionary<string, long>(StringComparer.Ordinal);
        long recurring = 0;
        long overdue = 0;

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = Command(conn, transaction: null,
            $"""
             SELECT state, COUNT_BIG(*) FROM {_schema}.jobs WHERE {clause} GROUP BY state;
             SELECT queue, COUNT_BIG(*) FROM {_schema}.jobs WHERE state = 1 AND {clause} GROUP BY queue;
             SELECT state, COUNT_BIG(*) FROM {_schema}.workflow_instances WHERE {clause} GROUP BY state;
             SELECT COUNT_BIG(*), SUM(CASE WHEN next_fire_time <= @now THEN 1 ELSE 0 END)
                 FROM {_schema}.recurring WHERE {clause};
             """,
            ("@now", now));

        if (parameter is not null)
        {
            cmd.Parameters.AddWithValue("@tenant", parameter);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            jobsByState[(JobState)reader.GetInt32(0)] = reader.GetInt64(1);
        }

        await reader.NextResultAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            byQueue[reader.GetString(0)] = reader.GetInt64(1);
        }

        await reader.NextResultAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            instancesByState[(WorkflowInstanceState)reader.GetInt32(0)] = reader.GetInt64(1);
        }

        await reader.NextResultAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            recurring = reader.GetInt64(0);
            overdue = reader.IsDBNull(1) ? 0 : Convert.ToInt64(reader.GetValue(1));
        }

        return new JobStatistics
        {
            JobsByState = jobsByState,
            EnqueuedByQueue = byQueue,
            InstancesByState = instancesByState,
            RecurringDefinitions = recurring,
            OverdueRecurringDefinitions = overdue,
        };
    }

    public async ValueTask<Page<JobSummary>> QueryJobsAsync(JobQuery query, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var limit = ClampLimit(query.Limit, JobQuery.DefaultLimit, JobQuery.MaxLimit);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        var filters = new List<string>();

        if (query.States is { Count: > 0 } states)
        {
            var names = states.Select((s, i) => (Name: $"@s{i}", Value: (int)s)).ToList();
            filters.Add($"state IN ({string.Join(", ", names.Select(n => n.Name))})");
            foreach (var (name, value) in names)
            {
                cmd.Parameters.AddWithValue(name, value);
            }
        }

        if (query.Queue is not null)
        {
            filters.Add("queue = @queue");
            cmd.Parameters.AddWithValue("@queue", query.Queue);
        }

        AppendTenant(filters, cmd, query.Tenant);
        AppendCreatedRange(filters, cmd, query.CreatedAfter, query.CreatedBefore);
        AppendCursor(filters, cmd, query.Cursor);

        cmd.CommandText = $"""
            SELECT TOP (@limit) id, queue, state, priority, invocation, created_at, due_at,
                   finished_at, attempt, failures, tenant_id, worker_id
            FROM {_schema}.jobs
            WHERE {Where(filters)}
            ORDER BY created_at DESC, CAST(id AS char(36)) DESC
            """;
        cmd.Parameters.AddWithValue("@limit", limit + 1);

        var rows = new List<JobSummary>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var invocation = JsonSerializer.Deserialize<JobInvocation>(reader.GetString(4), Json)!;
                rows.Add(new JobSummary
                {
                    Id = new JobId(reader.GetGuid(0)),
                    Queue = reader.GetString(1),
                    State = (JobState)reader.GetInt32(2),
                    Priority = reader.GetInt32(3),
                    TypeName = invocation.TypeName,
                    MethodName = invocation.MethodName,
                    CreatedAt = reader.GetFieldValue<DateTimeOffset>(5),
                    DueAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                    FinishedAt = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                    Attempt = reader.GetInt32(8),
                    Failures = reader.GetInt32(9),
                    TenantId = reader.IsDBNull(10) ? null : reader.GetString(10),
                    WorkerId = reader.IsDBNull(11) ? null : reader.GetString(11),
                });
            }
        }

        return BuildPage(rows, limit, s => (s.CreatedAt, s.Id.Value));
    }

    public async ValueTask<Page<WorkflowInstanceSummary>> QueryInstancesAsync(
        InstanceQuery query, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var limit = ClampLimit(query.Limit, InstanceQuery.DefaultLimit, InstanceQuery.MaxLimit);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        var filters = new List<string>();

        if (query.States is { Count: > 0 } states)
        {
            var names = states.Select((s, i) => (Name: $"@s{i}", Value: (int)s)).ToList();
            filters.Add($"state IN ({string.Join(", ", names.Select(n => n.Name))})");
            foreach (var (name, value) in names)
            {
                cmd.Parameters.AddWithValue(name, value);
            }
        }

        if (query.DefinitionId is not null)
        {
            filters.Add("definition_id = @definition");
            cmd.Parameters.AddWithValue("@definition", query.DefinitionId);

            if (query.DefinitionVersion is { } version)
            {
                filters.Add("definition_version = @version");
                cmd.Parameters.AddWithValue("@version", version);
            }
        }

        AppendTenant(filters, cmd, query.Tenant);
        AppendCreatedRange(filters, cmd, query.CreatedAfter, query.CreatedBefore);
        AppendCursor(filters, cmd, query.Cursor);

        cmd.CommandText = $"""
            SELECT TOP (@limit) id, definition_id, definition_version, state, tenant_id,
                   created_at, updated_at, revision
            FROM {_schema}.workflow_instances
            WHERE {Where(filters)}
            ORDER BY created_at DESC, CAST(id AS char(36)) DESC
            """;
        cmd.Parameters.AddWithValue("@limit", limit + 1);

        var rows = new List<WorkflowInstanceSummary>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add(new WorkflowInstanceSummary
                {
                    Id = new WorkflowInstanceId(reader.GetGuid(0)),
                    DefinitionId = reader.GetString(1),
                    DefinitionVersion = reader.GetInt32(2),
                    State = (WorkflowInstanceState)reader.GetInt32(3),
                    TenantId = reader.IsDBNull(4) ? null : reader.GetString(4),
                    CreatedAt = reader.GetFieldValue<DateTimeOffset>(5),
                    UpdatedAt = reader.GetFieldValue<DateTimeOffset>(6),
                    Revision = reader.GetInt64(7),
                });
            }
        }

        return BuildPage(rows, limit, s => (s.CreatedAt, s.Id.Value));
    }

    public async ValueTask<Page<RecurringSummary>> QueryRecurringAsync(
        RecurringQuery query, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var limit = ClampLimit(query.Limit, RecurringQuery.DefaultLimit, RecurringQuery.MaxLimit);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        var filters = new List<string>();

        if (query.Queue is not null)
        {
            filters.Add("queue = @queue");
            cmd.Parameters.AddWithValue("@queue", query.Queue);
        }

        AppendTenant(filters, cmd, query.Tenant);

        if (query.Cursor is not null)
        {
            if (!MonitoringCursor.TryDecodeStringId(query.Cursor, out var nextFire, out var afterId))
            {
                throw new MillraceStorageException(
                    "The supplied paging cursor was not issued by this provider and cannot be decoded.");
            }

            // Expanded by hand: SQL Server has no row-value comparison.
            filters.Add("(next_fire_time > @cnf OR (next_fire_time = @cnf AND id > @cid))");
            cmd.Parameters.AddWithValue("@cnf", nextFire);
            cmd.Parameters.AddWithValue("@cid", afterId);
        }

        cmd.CommandText = $"""
            SELECT TOP (@limit) r.id, r.cron, r.queue, r.invocation, r.priority, r.tenant_id,
                   r.next_fire_time, r.last_fire_time, r.created_at, r.updated_at,
                   last_job.last_state, last_job.last_id
            FROM {_schema}.recurring r
            OUTER APPLY (
                -- SQL Server's LATERAL. One row per definition through ix_jobs_recurring, and
                -- definitions are few, so the schedule view stays a single round trip. Ordered by
                -- creation, not completion: an occurrence still running must read Processing rather
                -- than showing last night's success (§11.26).
                --
                -- Aliased rather than bare, because the filter and cursor predicates come from
                -- helpers shared with the other list queries and use unqualified column names — a
                -- second `id` in scope would make those silently ambiguous.
                SELECT TOP (1) j.id AS last_id, j.state AS last_state
                FROM {_schema}.jobs j
                WHERE j.recurring_id = r.id
                ORDER BY j.created_at DESC, j.id DESC
            ) AS last_job
            WHERE {Where(filters)}
            ORDER BY r.next_fire_time ASC, r.id ASC
            """;
        cmd.Parameters.AddWithValue("@limit", limit + 1);

        var rows = new List<RecurringSummary>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var invocation = JsonSerializer.Deserialize<JobInvocation>(reader.GetString(3), Json)!;
                rows.Add(new RecurringSummary
                {
                    Id = reader.GetString(0),
                    Cron = reader.GetString(1),
                    Queue = reader.GetString(2),
                    TypeName = invocation.TypeName,
                    MethodName = invocation.MethodName,
                    Priority = reader.GetInt32(4),
                    TenantId = reader.IsDBNull(5) ? null : reader.GetString(5),
                    NextFireTime = reader.GetFieldValue<DateTimeOffset>(6),
                    LastFireTime = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                    CreatedAt = reader.GetFieldValue<DateTimeOffset>(8),
                    UpdatedAt = reader.GetFieldValue<DateTimeOffset>(9),
                    LastOutcome = reader.IsDBNull(10) ? null : (JobState)reader.GetInt32(10),
                    LastJobId = reader.IsDBNull(11) ? null : new JobId(reader.GetGuid(11)),
                });
            }
        }

        var hasMore = rows.Count > limit;
        var items = hasMore ? rows.GetRange(0, limit) : rows;
        var next = hasMore && items.Count > 0
            ? MonitoringCursor.Encode(items[^1].NextFireTime, items[^1].Id)
            : null;

        return new Page<RecurringSummary> { Items = items, NextCursor = next };
    }

    public async ValueTask<JobDetails?> GetJobDetailsAsync(JobId id, CancellationToken ct)
    {
        var job = await GetJobAsync(id, ct).ConfigureAwait(false);
        if (job is null)
        {
            return null;
        }

        // A second read rather than a join: the timeline is bounded and only wanted on the detail
        // view, so joining it in would multiply the job's columns by its attempts for every caller
        // that does not want them.
        var attempts = new List<JobAttempt>();
        await using (var conn = await OpenAsync(ct).ConfigureAwait(false))
        await using (var cmd = Command(conn, transaction: null,
            $"""
             SELECT attempt, outcome, recorded_at, worker_id, error
             FROM {_schema}.job_attempts WHERE job_id = @id ORDER BY attempt DESC
             """,
            ("@id", id.Value)))
        {
            await using var rows = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await rows.ReadAsync(ct).ConfigureAwait(false))
            {
                attempts.Add(new JobAttempt
                {
                    Attempt = rows.GetInt32(0),
                    Outcome = (JobAttemptOutcome)rows.GetInt32(1),
                    RecordedAt = rows.GetFieldValue<DateTimeOffset>(2),
                    WorkerId = rows.IsDBNull(3) ? null : rows.GetString(3),
                    Error = rows.IsDBNull(4) ? null : rows.GetString(4),
                });
            }
        }

        return new JobDetails
        {
            Summary = new JobSummary
            {
                Id = job.Id,
                Queue = job.Queue,
                State = job.State,
                TypeName = job.Invocation.TypeName,
                MethodName = job.Invocation.MethodName,
                Priority = job.Priority,
                CreatedAt = job.CreatedAt,
                DueAt = job.DueAt,
                FinishedAt = job.FinishedAt,
                Attempt = job.Attempt,
                Failures = job.Failures,
                TenantId = job.TenantId,
                WorkerId = job.WorkerId,
            },
            Invocation = job.Invocation,
            Retry = job.Retry,
            IdempotencyKey = job.IdempotencyKey,
            ParentId = job.ParentId,
            LastError = job.LastError,
            LeaseUntil = job.LeaseUntil,
            CancelRequested = job.CancelRequested,
            WorkflowInstanceId = job.WorkflowInstanceId,
            ActivityNodeId = job.ActivityNodeId,
            Attempts = attempts,
        };
    }

    private static int ClampLimit(int requested, int fallback, int max)
        => requested < 1 ? fallback : Math.Min(requested, max);

    private static string Where(List<string> filters)
        => filters.Count == 0 ? "1 = 1" : string.Join(" AND ", filters);

    private static (string Clause, string? Parameter) TenantClause(TenantFilter tenant)
    {
        if (!tenant.IsConstrained)
        {
            return ("1 = 1", null);
        }

        return tenant.TenantId is { } id ? ("tenant_id = @tenant", id) : ("tenant_id IS NULL", null);
    }

    private static void AppendTenant(List<string> filters, SqlCommand cmd, TenantFilter tenant)
    {
        if (!tenant.IsConstrained)
        {
            return;
        }

        if (tenant.TenantId is { } id)
        {
            filters.Add("tenant_id = @tenant");
            cmd.Parameters.AddWithValue("@tenant", id);
        }
        else
        {
            filters.Add("tenant_id IS NULL");
        }
    }

    private static void AppendCreatedRange(
        List<string> filters, SqlCommand cmd, DateTimeOffset? after, DateTimeOffset? before)
    {
        if (after is { } lower)
        {
            filters.Add("created_at >= @createdAfter");
            cmd.Parameters.AddWithValue("@createdAfter", lower);
        }

        if (before is { } upper)
        {
            filters.Add("created_at < @createdBefore");
            cmd.Parameters.AddWithValue("@createdBefore", upper);
        }
    }

    private static void AppendCursor(List<string> filters, SqlCommand cmd, string? cursor)
    {
        if (cursor is null)
        {
            return;
        }

        if (!MonitoringCursor.TryDecode(cursor, out var createdAt, out var id))
        {
            throw new MillraceStorageException(
                "The supplied paging cursor was not issued by this provider and cannot be decoded.");
        }

        // Expanded by hand — no row-value comparison — and cast to binary(16), because
        // uniqueidentifier ordering is not byte order and the cursor's tiebreak is.
        filters.Add(
            "(created_at < @cursorCreatedAt OR (created_at = @cursorCreatedAt "
            + "AND CAST(id AS char(36)) < CAST(@cursorId AS char(36))))");
        cmd.Parameters.AddWithValue("@cursorCreatedAt", createdAt);
        cmd.Parameters.AddWithValue("@cursorId", id);
    }

    private static Page<T> BuildPage<T>(List<T> rows, int limit, Func<T, (DateTimeOffset CreatedAt, Guid Id)> key)
    {
        var hasMore = rows.Count > limit;
        var items = hasMore ? rows.GetRange(0, limit) : rows;
        var next = hasMore && items.Count > 0
            ? MonitoringCursor.Encode(key(items[^1]).CreatedAt, key(items[^1]).Id)
            : null;

        return new Page<T> { Items = items, NextCursor = next };
    }
}
