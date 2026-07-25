using Millrace.Storage.Monitoring;

namespace Millrace.Storage.InMemory;

/// <summary>
/// The <see cref="IMonitoringStorage"/> half of the in-memory provider.
/// </summary>
/// <remarks>
/// Held to exactly the same observable contract as a relational provider — ordering, cursor
/// handling and limit clamping all match, because the conformance kit runs the same facts against
/// both. Where the two could drift, this defers to the shared <see cref="MonitoringCursor"/>.
/// </remarks>
public sealed partial class InMemoryStorage
{
    public ValueTask<JobStatistics> GetStatisticsAsync(TenantFilter tenant, CancellationToken ct)
    {
        var now = _time.GetUtcNow();

        lock (_gate)
        {
            var jobs = _jobs.Values.Select(e => e.Record).Where(r => MatchesTenant(r.TenantId, tenant)).ToList();
            var instances = _instances.Values.Where(i => MatchesTenant(i.TenantId, tenant)).ToList();
            var recurring = _recurring.Values.Where(r => MatchesTenant(r.TenantId, tenant)).ToList();

            // Every enum member gets an entry: callers must never have to tell "none" from
            // "not reported".
            var jobsByState = Enum.GetValues<JobState>().ToDictionary(s => s, _ => 0L);
            foreach (var job in jobs)
            {
                jobsByState[job.State]++;
            }

            var instancesByState = Enum.GetValues<WorkflowInstanceState>().ToDictionary(s => s, _ => 0L);
            foreach (var instance in instances)
            {
                instancesByState[instance.State]++;
            }

            var byQueue = jobs
                .Where(j => j.State == JobState.Enqueued)
                .GroupBy(j => j.Queue, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.Ordinal);

            return ValueTask.FromResult(new JobStatistics
            {
                JobsByState = jobsByState,
                EnqueuedByQueue = byQueue,
                InstancesByState = instancesByState,
                RecurringDefinitions = recurring.Count,
                OverdueRecurringDefinitions = recurring.Count(r => r.NextFireTime <= now),
            });
        }
    }

    public ValueTask<Page<JobSummary>> QueryJobsAsync(JobQuery query, CancellationToken ct)
    {
        var limit = ClampLimit(query.Limit, JobQuery.DefaultLimit, JobQuery.MaxLimit);
        var after = DecodeCursor(query.Cursor);
        var states = query.States is { Count: > 0 } ? query.States.ToHashSet() : null;

        lock (_gate)
        {
            var matched = _jobs.Values
                .Select(e => e.Record)
                .Where(r =>
                    (states is null || states.Contains(r.State))
                    && (query.Queue is null || string.Equals(r.Queue, query.Queue, StringComparison.Ordinal))
                    && MatchesTenant(r.TenantId, query.Tenant)
                    && MatchesCreatedRange(r.CreatedAt, query.CreatedAfter, query.CreatedBefore));

            var ordered = OrderAndSeek(matched, r => (r.CreatedAt, r.Id.Value), after);
            return ValueTask.FromResult(BuildPage(ordered, limit, ToSummary, s => (s.CreatedAt, s.Id.Value)));
        }
    }

    public ValueTask<Page<WorkflowInstanceSummary>> QueryInstancesAsync(InstanceQuery query, CancellationToken ct)
    {
        var limit = ClampLimit(query.Limit, InstanceQuery.DefaultLimit, InstanceQuery.MaxLimit);
        var after = DecodeCursor(query.Cursor);
        var states = query.States is { Count: > 0 } ? query.States.ToHashSet() : null;

        lock (_gate)
        {
            var matched = _instances.Values
                .Where(i =>
                    (states is null || states.Contains(i.State))
                    && (query.DefinitionId is null
                        || (string.Equals(i.DefinitionId, query.DefinitionId, StringComparison.Ordinal)
                            // A version means nothing without a definition id, so it only filters here.
                            && (query.DefinitionVersion is null || i.DefinitionVersion == query.DefinitionVersion)))
                    && MatchesTenant(i.TenantId, query.Tenant)
                    && MatchesCreatedRange(i.CreatedAt, query.CreatedAfter, query.CreatedBefore));

            var ordered = OrderAndSeek(matched, i => (i.CreatedAt, i.Id.Value), after);
            return ValueTask.FromResult(BuildPage(ordered, limit, ToSummary, s => (s.CreatedAt, s.Id.Value)));
        }
    }

    public ValueTask<Page<RecurringSummary>> QueryRecurringAsync(RecurringQuery query, CancellationToken ct)
    {
        var limit = ClampLimit(query.Limit, RecurringQuery.DefaultLimit, RecurringQuery.MaxLimit);
        var after = DecodeStringCursor(query.Cursor);

        lock (_gate)
        {
            var ordered = _recurring.Values
                .Where(r =>
                    (query.Queue is null || string.Equals(r.Queue, query.Queue, StringComparison.Ordinal))
                    && MatchesTenant(r.TenantId, query.Tenant))
                // Ascending: a schedule view reads forwards in time.
                .OrderBy(r => r.NextFireTime)
                .ThenBy(r => r.Id, StringComparer.Ordinal)
                .ToList();

            if (after is { } cursor)
            {
                ordered = ordered.Where(r =>
                    r.NextFireTime > cursor.Timestamp
                    || (r.NextFireTime == cursor.Timestamp
                        && string.CompareOrdinal(r.Id, cursor.Id) > 0)).ToList();
            }

            var items = ordered.Take(limit).Select(ToSummary).ToList();
            var next = ordered.Count > limit && items.Count > 0
                ? MonitoringCursor.Encode(items[^1].NextFireTime, items[^1].Id)
                : null;

            return ValueTask.FromResult(new Page<RecurringSummary> { Items = items, NextCursor = next });
        }
    }

    public ValueTask<JobDetails?> GetJobDetailsAsync(JobId id, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var entry))
            {
                return ValueTask.FromResult<JobDetails?>(null);
            }

            var r = entry.Record;
            return ValueTask.FromResult<JobDetails?>(new JobDetails
            {
                Summary = ToSummary(r),
                Invocation = r.Invocation,
                Retry = r.Retry,
                IdempotencyKey = r.IdempotencyKey,
                ParentId = r.ParentId,
                LastError = r.LastError,
                LeaseUntil = r.LeaseUntil,
                CancelRequested = r.CancelRequested,
                WorkflowInstanceId = r.WorkflowInstanceId,
                ActivityNodeId = r.ActivityNodeId,
            });
        }
    }

    // ---------------------------------------------------------------- helpers

    private static int ClampLimit(int requested, int fallback, int max)
        => requested < 1 ? fallback : Math.Min(requested, max);

    private static (DateTimeOffset CreatedAt, Guid Id)? DecodeCursor(string? cursor)
    {
        if (cursor is null)
        {
            return null;
        }

        if (!MonitoringCursor.TryDecode(cursor, out var createdAt, out var id))
        {
            throw new MillraceStorageException(
                "The supplied paging cursor was not issued by this provider and cannot be decoded.");
        }

        return (createdAt, id);
    }

    private static (DateTimeOffset Timestamp, string Id)? DecodeStringCursor(string? cursor)
    {
        if (cursor is null)
        {
            return null;
        }

        if (!MonitoringCursor.TryDecodeStringId(cursor, out var timestamp, out var id))
        {
            throw new MillraceStorageException(
                "The supplied paging cursor was not issued by this provider and cannot be decoded.");
        }

        return (timestamp, id);
    }

    private static bool MatchesTenant(string? tenantId, TenantFilter filter)
        => !filter.IsConstrained || string.Equals(tenantId, filter.TenantId, StringComparison.Ordinal);

    private static bool MatchesCreatedRange(DateTimeOffset createdAt, DateTimeOffset? after, DateTimeOffset? before)
        => (after is null || createdAt >= after) && (before is null || createdAt < before);

    /// <summary>
    /// Applies the <c>CreatedAt DESC, Id DESC</c> order and seeks past the cursor position.
    /// </summary>
    private static List<T> OrderAndSeek<T>(
        IEnumerable<T> source, Func<T, (DateTimeOffset CreatedAt, Guid Id)> key, (DateTimeOffset CreatedAt, Guid Id)? after)
    {
        var ordered = source
            .OrderByDescending(x => key(x).CreatedAt)
            .ThenByDescending(x => key(x).Id, Comparer<Guid>.Create(MonitoringCursor.CompareIds));

        if (after is not { } cursor)
        {
            return ordered.ToList();
        }

        return ordered.Where(x =>
        {
            var (createdAt, id) = key(x);
            return createdAt < cursor.CreatedAt
                || (createdAt == cursor.CreatedAt && MonitoringCursor.CompareIds(id, cursor.Id) < 0);
        }).ToList();
    }

    /// <summary>
    /// Takes one row beyond the limit to decide whether a further page exists, so the last page
    /// reports a null cursor rather than one that yields nothing.
    /// </summary>
    private static Page<TOut> BuildPage<TIn, TOut>(
        List<TIn> ordered, int limit, Func<TIn, TOut> project, Func<TOut, (DateTimeOffset CreatedAt, Guid Id)> key)
    {
        var items = ordered.Take(limit).Select(project).ToList();
        var hasMore = ordered.Count > limit;
        var next = hasMore && items.Count > 0
            ? MonitoringCursor.Encode(key(items[^1]).CreatedAt, key(items[^1]).Id)
            : null;

        return new Page<TOut> { Items = items, NextCursor = next };
    }

    private static JobSummary ToSummary(JobRecord r) => new()
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
    };

    private static RecurringSummary ToSummary(RecurringJobRecord r) => new()
    {
        Id = r.Id,
        Cron = r.Cron,
        Queue = r.Queue,
        TypeName = r.Invocation.TypeName,
        MethodName = r.Invocation.MethodName,
        Priority = r.Priority,
        TenantId = r.TenantId,
        NextFireTime = r.NextFireTime,
        LastFireTime = r.LastFireTime,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
    };

    private static WorkflowInstanceSummary ToSummary(WorkflowInstanceRecord r) => new()
    {
        Id = r.Id,
        DefinitionId = r.DefinitionId,
        DefinitionVersion = r.DefinitionVersion,
        State = r.State,
        TenantId = r.TenantId,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
        Revision = r.Revision,
    };
}
