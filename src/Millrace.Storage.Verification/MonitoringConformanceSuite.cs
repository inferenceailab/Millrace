using Microsoft.Extensions.Time.Testing;
using Millrace.Storage.Monitoring;
using Xunit;

namespace Millrace.Storage.Verification;

/// <summary>
/// The monitoring read-model conformance suite (ARCHITECTURE.md §4.1, §11.12, §11.14).
/// </summary>
/// <remarks>
/// These facts exist because the dashboard is rendered three times over one contract: any drift
/// between providers becomes a UI bug that only reproduces on one database. The awkward cases —
/// cursor stability while rows change underneath, the tenant filter's two distinct "null" meanings,
/// and limit clamping — are exactly where independent implementations diverge, so they are asserted
/// rather than assumed.
/// </remarks>
public abstract class MonitoringConformanceSuite
{
    protected static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    protected abstract ValueTask<IStorageHarness> CreateHarnessAsync(TimeProvider time);

    protected static FakeTimeProvider NewTime() => new(Epoch);

    private static JobRecord Job(
        TimeProvider time, string queue = "default", JobState state = JobState.Enqueued,
        string? tenantId = null, int priority = 0) => new()
    {
        Id = JobId.New(time),
        Queue = queue,
        State = state,
        Priority = priority,
        Invocation = new JobInvocation
        {
            TypeName = "Sample.IService, Sample",
            MethodName = "RunAsync",
            ParameterTypes = ["System.Int32, System.Private.CoreLib"],
            ArgumentsJson = ["1"],
        },
        Retry = Retry.None,
        CreatedAt = time.GetUtcNow(),
        TenantId = tenantId,
    };

    /// <summary>
    /// Inserts <paramref name="count"/> jobs one clock tick apart, so <c>CreatedAt</c> is distinct
    /// and the expected order is unambiguous. Returns them oldest-first.
    /// </summary>
    private static async Task<List<JobRecord>> SeedAsync(
        IStorageHarness harness, FakeTimeProvider time, int count, string queue = "default", string? tenantId = null)
    {
        var jobs = new List<JobRecord>();
        for (var i = 0; i < count; i++)
        {
            var job = Job(time, queue, tenantId: tenantId);
            await harness.Jobs.EnqueueAsync([job], CancellationToken.None);
            jobs.Add(job);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        return jobs;
    }

    [Fact]
    public async Task Query_orders_newest_first()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var seeded = await SeedAsync(harness, time, 5);

        var page = await harness.Monitoring.QueryJobsAsync(new JobQuery(), CancellationToken.None);

        Assert.Equal(
            seeded.AsEnumerable().Reverse().Select(j => j.Id).ToList(),
            page.Items.Select(i => i.Id).ToList());
    }

    [Fact]
    public async Task Paging_walks_every_row_exactly_once()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var seeded = await SeedAsync(harness, time, 10);

        var seen = new List<JobId>();
        string? cursor = null;
        do
        {
            var page = await harness.Monitoring.QueryJobsAsync(
                new JobQuery { Limit = 3, Cursor = cursor }, CancellationToken.None);
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(10, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(seeded.AsEnumerable().Reverse().Select(j => j.Id).ToList(), seen);
    }

    [Fact]
    public async Task Last_page_reports_a_null_cursor()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        await SeedAsync(harness, time, 3);

        var page = await harness.Monitoring.QueryJobsAsync(new JobQuery { Limit = 3 }, CancellationToken.None);

        Assert.Equal(3, page.Items.Count);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Empty_result_has_no_items_and_no_cursor()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);

        var page = await harness.Monitoring.QueryJobsAsync(new JobQuery(), CancellationToken.None);

        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Paging_is_stable_when_rows_change_state_underneath()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var seeded = await SeedAsync(harness, time, 6);

        var first = await harness.Monitoring.QueryJobsAsync(new JobQuery { Limit = 2 }, CancellationToken.None);

        // Mutate a row from the page already read and one not yet reached. Neither may cause a row
        // to be skipped or repeated: the keyset is (CreatedAt, Id), which no transition changes.
        await harness.Jobs.TryCancelAsync(first.Items[0].Id, CancellationToken.None);
        await harness.Jobs.TryCancelAsync(seeded[0].Id, CancellationToken.None);

        var seen = new List<JobId>(first.Items.Select(i => i.Id));
        var cursor = first.NextCursor;
        while (cursor is not null)
        {
            var page = await harness.Monitoring.QueryJobsAsync(
                new JobQuery { Limit = 2, Cursor = cursor }, CancellationToken.None);
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
        }

        Assert.Equal(seeded.AsEnumerable().Reverse().Select(j => j.Id).ToList(), seen);
    }

    [Fact]
    public async Task An_undecodable_cursor_is_rejected_rather_than_restarting()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        await SeedAsync(harness, time, 2);

        // Silently restarting would turn a client bug into an infinite paging loop.
        await Assert.ThrowsAsync<MillraceStorageException>(async () =>
            await harness.Monitoring.QueryJobsAsync(
                new JobQuery { Cursor = "not-a-cursor" }, CancellationToken.None));
    }

    [Fact]
    public async Task Limit_is_clamped_never_rejected()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        await SeedAsync(harness, time, 3);

        var zero = await harness.Monitoring.QueryJobsAsync(new JobQuery { Limit = 0 }, CancellationToken.None);
        var negative = await harness.Monitoring.QueryJobsAsync(new JobQuery { Limit = -5 }, CancellationToken.None);
        var huge = await harness.Monitoring.QueryJobsAsync(new JobQuery { Limit = 10_000 }, CancellationToken.None);

        Assert.Equal(3, zero.Items.Count);
        Assert.Equal(3, negative.Items.Count);
        Assert.Equal(3, huge.Items.Count);
    }

    [Fact]
    public async Task State_filter_selects_only_those_states()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var seeded = await SeedAsync(harness, time, 4);
        await harness.Jobs.TryCancelAsync(seeded[1].Id, CancellationToken.None);

        var cancelled = await harness.Monitoring.QueryJobsAsync(
            new JobQuery { States = [JobState.Cancelled] }, CancellationToken.None);

        Assert.Equal(seeded[1].Id, Assert.Single(cancelled.Items).Id);
    }

    [Fact]
    public async Task Empty_state_list_means_any_state()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        await SeedAsync(harness, time, 3);

        var page = await harness.Monitoring.QueryJobsAsync(
            new JobQuery { States = [] }, CancellationToken.None);

        Assert.Equal(3, page.Items.Count);
    }

    [Fact]
    public async Task Queue_and_created_range_filters_combine_with_and()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var alpha = await SeedAsync(harness, time, 3, queue: "alpha");
        await SeedAsync(harness, time, 3, queue: "beta");

        var page = await harness.Monitoring.QueryJobsAsync(
            new JobQuery { Queue = "alpha", CreatedAfter = alpha[1].CreatedAt }, CancellationToken.None);

        Assert.Equal([alpha[2].Id, alpha[1].Id], page.Items.Select(i => i.Id).ToList());
    }

    [Fact]
    public async Task Created_range_is_lower_inclusive_and_upper_exclusive()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var seeded = await SeedAsync(harness, time, 3);

        var page = await harness.Monitoring.QueryJobsAsync(
            new JobQuery { CreatedAfter = seeded[0].CreatedAt, CreatedBefore = seeded[2].CreatedAt },
            CancellationToken.None);

        Assert.Equal([seeded[1].Id, seeded[0].Id], page.Items.Select(i => i.Id).ToList());
    }

    [Fact]
    public async Task Tenant_filter_distinguishes_any_from_untenanted()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var untenanted = await SeedAsync(harness, time, 2);
        var acme = await SeedAsync(harness, time, 3, tenantId: "acme");

        var any = await harness.Monitoring.QueryJobsAsync(
            new JobQuery { Tenant = TenantFilter.Any }, CancellationToken.None);
        var none = await harness.Monitoring.QueryJobsAsync(
            new JobQuery { Tenant = TenantFilter.Untenanted }, CancellationToken.None);
        var one = await harness.Monitoring.QueryJobsAsync(
            new JobQuery { Tenant = TenantFilter.For("acme") }, CancellationToken.None);

        Assert.Equal(5, any.Items.Count);
        Assert.Equal(untenanted.Select(j => j.Id).OrderBy(i => i.Value), none.Items.Select(i => i.Id).OrderBy(i => i.Value));
        Assert.Equal(acme.Select(j => j.Id).OrderBy(i => i.Value), one.Items.Select(i => i.Id).OrderBy(i => i.Value));
    }

    [Fact]
    public async Task Job_details_returns_null_for_an_unknown_id()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);

        Assert.Null(await harness.Monitoring.GetJobDetailsAsync(JobId.New(time), CancellationToken.None));
    }

    [Fact]
    public async Task Job_details_carries_the_payload_the_summary_omits()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var job = Job(time, tenantId: "acme");
        await harness.Jobs.EnqueueAsync([job], CancellationToken.None);

        var details = await harness.Monitoring.GetJobDetailsAsync(job.Id, CancellationToken.None);

        Assert.NotNull(details);
        Assert.Equal(job.Id, details.Summary.Id);
        Assert.Equal("acme", details.Summary.TenantId);
        Assert.Equal(job.Invocation.TypeName, details.Summary.TypeName);
        Assert.Equal(job.Invocation.MethodName, details.Summary.MethodName);
        // The arguments are the reason detail is a separate read from the list projection.
        Assert.Equal(job.Invocation.ArgumentsJson, details.Invocation.ArgumentsJson);
    }

    [Fact]
    public async Task Interruptions_separate_infrastructure_churn_from_recorded_failures()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var job = Job(time);
        await harness.Jobs.EnqueueAsync([job], CancellationToken.None);

        // Two claims whose leases expire without any transition: executions started, none failed.
        for (var i = 0; i < 2; i++)
        {
            await harness.Jobs.ClaimAsync(
                new ClaimRequest($"worker-{i}", ["default"], MaxCount: 1, TimeSpan.FromMinutes(1)),
                CancellationToken.None);
            time.Advance(TimeSpan.FromMinutes(2));
        }

        var details = await harness.Monitoring.GetJobDetailsAsync(job.Id, CancellationToken.None);

        Assert.NotNull(details);
        Assert.Equal(2, details.Summary.Attempt);
        Assert.Equal(0, details.Summary.Failures);
        Assert.Equal(2, details.Summary.Interruptions);
    }

    [Fact]
    public async Task Statistics_report_every_state_including_zeroes()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var seeded = await SeedAsync(harness, time, 3);
        await harness.Jobs.TryCancelAsync(seeded[0].Id, CancellationToken.None);

        var stats = await harness.Monitoring.GetStatisticsAsync(TenantFilter.Any, CancellationToken.None);

        // Completeness matters: a caller must never have to tell "none" from "not reported".
        foreach (var state in Enum.GetValues<JobState>())
        {
            Assert.True(stats.JobsByState.ContainsKey(state), $"missing entry for {state}");
        }

        foreach (var state in Enum.GetValues<WorkflowInstanceState>())
        {
            Assert.True(stats.InstancesByState.ContainsKey(state), $"missing entry for {state}");
        }

        Assert.Equal(2, stats.JobsByState[JobState.Enqueued]);
        Assert.Equal(1, stats.JobsByState[JobState.Cancelled]);
        Assert.Equal(2, stats.EnqueuedByQueue["default"]);
    }

    [Fact]
    public async Task Statistics_respect_the_tenant_filter()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        await SeedAsync(harness, time, 2);
        await SeedAsync(harness, time, 3, tenantId: "acme");

        var any = await harness.Monitoring.GetStatisticsAsync(TenantFilter.Any, CancellationToken.None);
        var acme = await harness.Monitoring.GetStatisticsAsync(TenantFilter.For("acme"), CancellationToken.None);
        var untenanted = await harness.Monitoring.GetStatisticsAsync(TenantFilter.Untenanted, CancellationToken.None);

        Assert.Equal(5, any.JobsByState[JobState.Enqueued]);
        Assert.Equal(3, acme.JobsByState[JobState.Enqueued]);
        Assert.Equal(2, untenanted.JobsByState[JobState.Enqueued]);
    }

    [Fact]
    public async Task Statistics_count_recurring_definitions_and_overdue_ones()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);

        await harness.Jobs.UpsertRecurringAsync(Recurring("due", time.GetUtcNow().AddMinutes(-5), time), CancellationToken.None);
        await harness.Jobs.UpsertRecurringAsync(Recurring("later", time.GetUtcNow().AddHours(1), time), CancellationToken.None);

        var stats = await harness.Monitoring.GetStatisticsAsync(TenantFilter.Any, CancellationToken.None);

        Assert.Equal(2, stats.RecurringDefinitions);
        Assert.Equal(1, stats.OverdueRecurringDefinitions);

        RecurringJobRecord Recurring(string id, DateTimeOffset next, TimeProvider clock) => new()
        {
            Id = id,
            Cron = "* * * * *",
            Queue = "default",
            Invocation = Job(clock).Invocation,
            Retry = Retry.None,
            NextFireTime = next,
            CreatedAt = clock.GetUtcNow(),
            UpdatedAt = clock.GetUtcNow(),
        };
    }

    [Fact]
    public async Task Instance_queries_page_and_filter_like_job_queries()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);

        var ids = new List<WorkflowInstanceId>();
        for (var i = 0; i < 4; i++)
        {
            var instance = new WorkflowInstanceRecord
            {
                Id = WorkflowInstanceId.New(time),
                DefinitionId = i < 2 ? "alpha" : "beta",
                DefinitionVersion = 1,
                State = WorkflowInstanceState.Running,
                DataJson = """{"v":1}""",
                Revision = 1,
                CreatedAt = time.GetUtcNow(),
                UpdatedAt = time.GetUtcNow(),
            };
            await harness.Workflows.CreateInstanceAsync(instance, CancellationToken.None);
            ids.Add(instance.Id);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        var all = await harness.Monitoring.QueryInstancesAsync(new InstanceQuery(), CancellationToken.None);
        var alpha = await harness.Monitoring.QueryInstancesAsync(
            new InstanceQuery { DefinitionId = "alpha" }, CancellationToken.None);
        var firstPage = await harness.Monitoring.QueryInstancesAsync(
            new InstanceQuery { Limit = 2 }, CancellationToken.None);

        Assert.Equal(4, all.Items.Count);
        Assert.Equal([ids[3], ids[2], ids[1], ids[0]], all.Items.Select(i => i.Id).ToList());
        Assert.Equal([ids[1], ids[0]], alpha.Items.Select(i => i.Id).ToList());
        Assert.NotNull(firstPage.NextCursor);
        Assert.All(all.Items, i => Assert.Equal(1, i.Revision));
    }

    [Fact]
    public async Task Instance_version_filter_requires_a_definition_id()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var instance = new WorkflowInstanceRecord
        {
            Id = WorkflowInstanceId.New(time),
            DefinitionId = "alpha",
            DefinitionVersion = 2,
            State = WorkflowInstanceState.Running,
            DataJson = """{"v":1}""",
            Revision = 1,
            CreatedAt = time.GetUtcNow(),
            UpdatedAt = time.GetUtcNow(),
        };
        await harness.Workflows.CreateInstanceAsync(instance, CancellationToken.None);

        // A version alone means nothing, so it must not filter on its own.
        var versionOnly = await harness.Monitoring.QueryInstancesAsync(
            new InstanceQuery { DefinitionVersion = 99 }, CancellationToken.None);
        var mismatched = await harness.Monitoring.QueryInstancesAsync(
            new InstanceQuery { DefinitionId = "alpha", DefinitionVersion = 99 }, CancellationToken.None);

        Assert.Single(versionOnly.Items);
        Assert.Empty(mismatched.Items);
    }
}
