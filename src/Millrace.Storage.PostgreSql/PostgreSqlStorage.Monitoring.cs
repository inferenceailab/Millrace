using System.Text.Json;
using Millrace.Storage.Monitoring;
using Npgsql;

namespace Millrace.Storage.PostgreSql;

/// <summary>
/// The <see cref="IMonitoringStorage"/> half of the PostgreSQL provider.
/// </summary>
/// <remarks>
/// Reads are plain <c>SELECT</c>s taking no locks, so a dashboard cannot delay claiming or applying
/// a transition. Keyset paging uses PostgreSQL's row-value comparison, which the
/// <c>(created_at DESC, id DESC)</c> indexes serve directly — so page 900 costs the same as page 1,
/// which is the point of §11.12.
/// </remarks>
public sealed partial class PostgreSqlStorage
{
    private const string SummaryColumns =
        "id, queue, state, priority, invocation, created_at, due_at, finished_at, " +
        "attempt, failures, tenant_id, worker_id";

    /// <inheritdoc />
    public async ValueTask<JobStatistics> GetStatisticsAsync(TenantFilter tenant, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var (clause, parameter) = TenantClause(tenant, "tenant_id");

        var jobsByState = Enum.GetValues<JobState>().ToDictionary(s => s, _ => 0L);
        var instancesByState = Enum.GetValues<WorkflowInstanceState>().ToDictionary(s => s, _ => 0L);
        var byQueue = new Dictionary<string, long>(StringComparer.Ordinal);
        long recurring = 0;
        long overdue = 0;

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT state, count(*) FROM {_schema}.jobs WHERE {clause} GROUP BY state;
            SELECT queue, count(*) FROM {_schema}.jobs WHERE state = 1 AND {clause} GROUP BY queue;
            SELECT state, count(*) FROM {_schema}.workflow_instances WHERE {clause} GROUP BY state;
            SELECT count(*), count(*) FILTER (WHERE next_fire_time <= @now)
                FROM {_schema}.recurring WHERE {clause};
            """;
        cmd.Parameters.AddWithValue("now", now);
        if (parameter is not null)
        {
            cmd.Parameters.AddWithValue("tenant", parameter);
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
            overdue = reader.GetInt64(1);
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

    /// <inheritdoc />
    public async ValueTask<Page<JobSummary>> QueryJobsAsync(JobQuery query, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var limit = ClampLimit(query.Limit, JobQuery.DefaultLimit, JobQuery.MaxLimit);

        var filters = new List<string>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        if (query.States is { Count: > 0 } states)
        {
            filters.Add("state = ANY(@states)");
            cmd.Parameters.AddWithValue("states", states.Select(s => (int)s).ToArray());
        }

        if (query.Queue is not null)
        {
            filters.Add("queue = @queue");
            cmd.Parameters.AddWithValue("queue", query.Queue);
        }

        AppendTenant(filters, cmd, query.Tenant);
        AppendCreatedRange(filters, cmd, query.CreatedAfter, query.CreatedBefore);
        AppendCursor(filters, cmd, query.Cursor);

        cmd.CommandText = $"""
            SELECT {SummaryColumns} FROM {_schema}.jobs
            WHERE {Where(filters)}
            ORDER BY created_at DESC, id DESC
            LIMIT @limit
            """;
        // One row beyond the page decides whether a further page exists, so the last page reports
        // a null cursor instead of one that yields nothing.
        cmd.Parameters.AddWithValue("limit", limit + 1);

        var rows = new List<JobSummary>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add(ReadSummary(reader));
            }
        }

        return BuildPage(rows, limit, s => (s.CreatedAt, s.Id.Value));
    }

    /// <inheritdoc />
    public async ValueTask<Page<WorkflowInstanceSummary>> QueryInstancesAsync(InstanceQuery query, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var limit = ClampLimit(query.Limit, InstanceQuery.DefaultLimit, InstanceQuery.MaxLimit);

        var filters = new List<string>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        if (query.States is { Count: > 0 } states)
        {
            filters.Add("state = ANY(@states)");
            cmd.Parameters.AddWithValue("states", states.Select(s => (int)s).ToArray());
        }

        if (query.DefinitionId is not null)
        {
            filters.Add("definition_id = @definition");
            cmd.Parameters.AddWithValue("definition", query.DefinitionId);

            // A version without a definition id is meaningless, so it only filters alongside one.
            if (query.DefinitionVersion is { } version)
            {
                filters.Add("definition_version = @version");
                cmd.Parameters.AddWithValue("version", version);
            }
        }

        AppendTenant(filters, cmd, query.Tenant);
        AppendCreatedRange(filters, cmd, query.CreatedAfter, query.CreatedBefore);
        AppendCursor(filters, cmd, query.Cursor);

        cmd.CommandText = $"""
            SELECT id, definition_id, definition_version, state, tenant_id, created_at, updated_at, revision
            FROM {_schema}.workflow_instances
            WHERE {Where(filters)}
            ORDER BY created_at DESC, id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("limit", limit + 1);

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

    /// <inheritdoc />
    public async ValueTask<Page<RecurringSummary>> QueryRecurringAsync(RecurringQuery query, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var limit = ClampLimit(query.Limit, RecurringQuery.DefaultLimit, RecurringQuery.MaxLimit);

        var filters = new List<string>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        if (query.Queue is not null)
        {
            filters.Add("queue = @queue");
            cmd.Parameters.AddWithValue("queue", query.Queue);
        }

        AppendTenant(filters, cmd, query.Tenant);

        if (query.Cursor is not null)
        {
            if (!MonitoringCursor.TryDecodeStringId(query.Cursor, out var nextFire, out var id))
            {
                throw new MillraceStorageException(
                    "The supplied paging cursor was not issued by this provider and cannot be decoded.");
            }

            // Ascending order, so the keyset predicate points forwards.
            filters.Add("(next_fire_time, id) > (@cursorNextFire, @cursorId)");
            cmd.Parameters.AddWithValue("cursorNextFire", nextFire);
            cmd.Parameters.AddWithValue("cursorId", id);
        }

        // The last outcome comes from a LATERAL rather than a join or a subquery per column: it
        // reads one row per definition through ix_jobs_recurring, and definitions are few, so the
        // schedule view stays a single round trip. Ordered by creation, not completion — an
        // occurrence still running must read Processing rather than showing last night's success
        // (§11.26).
        cmd.CommandText = $"""
            SELECT r.id, r.cron, r.queue, r.invocation, r.priority, r.tenant_id,
                   r.next_fire_time, r.last_fire_time, r.created_at, r.updated_at,
                   last_job.last_state, last_job.last_id
            FROM {_schema}.recurring r
            LEFT JOIN LATERAL (
                -- Aliased rather than bare: the filter and cursor predicates are built by helpers
                -- shared with the other list queries and use unqualified column names, so exposing
                -- a second `id` here would make those silently ambiguous.
                SELECT j.id AS last_id, j.state AS last_state
                FROM {_schema}.jobs j
                WHERE j.recurring_id = r.id
                ORDER BY j.created_at DESC, j.id DESC
                LIMIT 1
            ) AS last_job ON TRUE
            WHERE {Where(filters)}
            ORDER BY r.next_fire_time ASC, r.id ASC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("limit", limit + 1);

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

    /// <inheritdoc />
    public async ValueTask<JobDetails?> GetJobDetailsAsync(JobId id, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {JobColumns} FROM {_schema}.jobs WHERE id = @id";
        cmd.Parameters.AddWithValue("id", id.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        // Reuses the full-record mapper so detail can never drift from what the engine sees.
        var r = ReadJob(reader);
        await reader.CloseAsync().ConfigureAwait(false);

        // A second read rather than a join: the timeline is bounded and only wanted on the detail
        // view, so joining it into the row would multiply the job's columns by its attempts for
        // every caller that does not want them.
        var attempts = new List<JobAttempt>();
        await using (var history = conn.CreateCommand())
        {
            history.CommandText = $"""
                SELECT attempt, outcome, recorded_at, worker_id, error
                FROM {_schema}.job_attempts
                WHERE job_id = @id
                ORDER BY attempt DESC
                """;
            history.Parameters.AddWithValue("id", id.Value);

            await using var rows = await history.ExecuteReaderAsync(ct).ConfigureAwait(false);
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
                Id = r.Id,
                Queue = r.Queue,
                State = r.State,
                TypeName = r.Invocation.TypeName,
                MethodName = r.Invocation.MethodName,
                Priority = r.Priority,
                CreatedAt = r.CreatedAt,
                DueAt = r.DueAt,
                FinishedAt = r.FinishedAt,
                Attempt = r.Attempt,
                Failures = r.Failures,
                TenantId = r.TenantId,
                WorkerId = r.WorkerId,
            },
            Invocation = r.Invocation,
            Retry = r.Retry,
            IdempotencyKey = r.IdempotencyKey,
            ParentId = r.ParentId,
            LastError = r.LastError,
            LeaseUntil = r.LeaseUntil,
            CancelRequested = r.CancelRequested,
            WorkflowInstanceId = r.WorkflowInstanceId,
            ActivityNodeId = r.ActivityNodeId,
            Attempts = attempts,
        };
    }

    // ---------------------------------------------------------------- helpers

    private static int ClampLimit(int requested, int fallback, int max)
        => requested < 1 ? fallback : Math.Min(requested, max);

    private static string Where(List<string> filters)
        => filters.Count == 0 ? "TRUE" : string.Join(" AND ", filters);

    private static (string Clause, string? Parameter) TenantClause(TenantFilter tenant, string column)
    {
        if (!tenant.IsConstrained)
        {
            return ("TRUE", null);
        }

        return tenant.TenantId is { } id ? ($"{column} = @tenant", id) : ($"{column} IS NULL", null);
    }

    private static void AppendTenant(List<string> filters, NpgsqlCommand cmd, TenantFilter tenant)
    {
        if (!tenant.IsConstrained)
        {
            return;
        }

        if (tenant.TenantId is { } id)
        {
            filters.Add("tenant_id = @tenant");
            cmd.Parameters.AddWithValue("tenant", id);
        }
        else
        {
            filters.Add("tenant_id IS NULL");
        }
    }

    private static void AppendCreatedRange(
        List<string> filters, NpgsqlCommand cmd, DateTimeOffset? after, DateTimeOffset? before)
    {
        if (after is { } lower)
        {
            filters.Add("created_at >= @createdAfter");
            cmd.Parameters.AddWithValue("createdAfter", lower);
        }

        if (before is { } upper)
        {
            filters.Add("created_at < @createdBefore");
            cmd.Parameters.AddWithValue("createdBefore", upper);
        }
    }

    private static void AppendCursor(List<string> filters, NpgsqlCommand cmd, string? cursor)
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

        // Row-value comparison: the index on (created_at DESC, id DESC) serves this directly, so
        // seeking to a deep page costs the same as the first.
        filters.Add("(created_at, id) < (@cursorCreatedAt, @cursorId)");
        cmd.Parameters.AddWithValue("cursorCreatedAt", createdAt);
        cmd.Parameters.AddWithValue("cursorId", id);
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

    private static JobSummary ReadSummary(NpgsqlDataReader reader)
    {
        var invocation = JsonSerializer.Deserialize<JobInvocation>(reader.GetString(4), Json)!;
        return new JobSummary
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
        };
    }
}
