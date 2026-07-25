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
