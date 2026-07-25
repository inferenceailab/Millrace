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

    [Theory]
    // Well-formed base64url of the wrong length, a string containing characters outside the
    // base64url alphabet, and one long enough to stress the length maths. Cursors arrive straight
    // from a query string, so every one of these must be a rejected cursor rather than an
    // unhandled exception — and silently restarting would turn a client bug into an infinite
    // paging loop.
    [InlineData("not-a-cursor")]
    [InlineData("!!not-a-cursor!!")]
    [InlineData("%%%")]
    [InlineData(" ")]
    [InlineData("a")]
    public async Task An_undecodable_cursor_is_rejected_rather_than_restarting(string cursor)
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        await SeedAsync(harness, time, 2);

        await Assert.ThrowsAsync<MillraceStorageException>(async () =>
            await harness.Monitoring.QueryJobsAsync(
                new JobQuery { Cursor = cursor }, CancellationToken.None));
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
    }

    [Fact]
    public async Task Recurring_definitions_are_ordered_soonest_first()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var now = time.GetUtcNow();

        await harness.Jobs.UpsertRecurringAsync(Recurring("c", now.AddHours(3), time), CancellationToken.None);
        await harness.Jobs.UpsertRecurringAsync(Recurring("a", now.AddHours(1), time), CancellationToken.None);
        await harness.Jobs.UpsertRecurringAsync(Recurring("b", now.AddHours(2), time), CancellationToken.None);

        var page = await harness.Monitoring.QueryRecurringAsync(new RecurringQuery(), CancellationToken.None);

        // Ascending — the opposite of the job and instance lists, because a schedule view is read
        // forwards in time.
        Assert.Equal(["a", "b", "c"], page.Items.Select(r => r.Id).ToList());
    }

    [Fact]
    public async Task A_definition_that_has_never_fired_reports_no_last_outcome()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);

        await harness.Jobs.UpsertRecurringAsync(
            Recurring("never", time.GetUtcNow().AddHours(1), time), CancellationToken.None);

        var page = await harness.Monitoring.QueryRecurringAsync(new RecurringQuery(), CancellationToken.None);

        // Null means "has produced no job", not "unknown" — for a definition whose next fire time
        // is long past, that absence is itself the answer (§11.26).
        Assert.Null(Assert.Single(page.Items).LastOutcome);
        Assert.Null(page.Items[0].LastJobId);
    }

    [Fact]
    public async Task The_last_outcome_is_the_state_of_the_most_recently_created_fired_job()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var now = time.GetUtcNow();

        await harness.Jobs.UpsertRecurringAsync(Recurring("nightly", now.AddHours(1), time), CancellationToken.None);

        var older = Job(time) with { RecurringId = "nightly", CreatedAt = now.AddHours(-2) };
        var newer = Job(time) with { RecurringId = "nightly", CreatedAt = now.AddHours(-1) };
        var unrelated = Job(time) with { CreatedAt = now };
        await harness.Jobs.EnqueueAsync([older, newer, unrelated], CancellationToken.None);

        // Driven through claim-and-apply rather than inserted terminal, because §4.2 refuses a
        // terminal state on insert — a job may only reach one by finishing.
        var claimed = await ClaimAllAsync(harness);
        await FinishAsync(harness, claimed.Single(c => c.Id == older.Id), JobState.Succeeded);
        await FinishAsync(harness, claimed.Single(c => c.Id == newer.Id), JobState.Dead);
        await FinishAsync(harness, claimed.Single(c => c.Id == unrelated.Id), JobState.Succeeded);

        var page = await harness.Monitoring.QueryRecurringAsync(new RecurringQuery(), CancellationToken.None);
        var summary = Assert.Single(page.Items);

        Assert.Equal(JobState.Dead, summary.LastOutcome);
        Assert.Equal(newer.Id, summary.LastJobId);
    }

    [Fact]
    public async Task A_running_occurrence_outranks_an_earlier_success()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var now = time.GetUtcNow();

        await harness.Jobs.UpsertRecurringAsync(Recurring("nightly", now.AddHours(1), time), CancellationToken.None);

        // Ordered by creation rather than completion, deliberately: a finished-last ordering would
        // show last night's success while tonight's run is still going, which is the one answer an
        // operator must not be given.
        var finished = Job(time) with { RecurringId = "nightly", CreatedAt = now.AddHours(-2) };
        var running = Job(time) with { RecurringId = "nightly", CreatedAt = now.AddHours(-1) };
        await harness.Jobs.EnqueueAsync([finished, running], CancellationToken.None);

        // Both are claimed; only one is finished. The other stays Processing — the occurrence still
        // in flight.
        var claimed = await ClaimAllAsync(harness);
        await FinishAsync(harness, claimed.Single(c => c.Id == finished.Id), JobState.Succeeded);

        var summary = Assert.Single(
            (await harness.Monitoring.QueryRecurringAsync(new RecurringQuery(), CancellationToken.None)).Items);

        Assert.Equal(JobState.Processing, summary.LastOutcome);
    }

    [Fact]
    public async Task The_link_to_a_definition_survives_the_definition_being_removed()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);

        await harness.Jobs.UpsertRecurringAsync(
            Recurring("gone", time.GetUtcNow().AddHours(1), time), CancellationToken.None);
        var fired = Job(time) with { RecurringId = "gone" };
        await harness.Jobs.EnqueueAsync([fired], CancellationToken.None);

        await harness.Jobs.RemoveRecurringAsync("gone", CancellationToken.None);

        // Provenance, not a live reference: the job records which definition produced it and must
        // outlive that definition rather than dangle or cascade.
        var stored = await harness.Jobs.GetJobAsync(fired.Id, CancellationToken.None);
        Assert.Equal("gone", stored!.RecurringId);
    }

    [Fact]
    public async Task Recurring_paging_walks_every_definition_exactly_once()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var now = time.GetUtcNow();
        for (var i = 0; i < 7; i++)
        {
            await harness.Jobs.UpsertRecurringAsync(
                Recurring($"def-{i}", now.AddMinutes(i), time), CancellationToken.None);
        }

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await harness.Monitoring.QueryRecurringAsync(
                new RecurringQuery { Limit = 2, Cursor = cursor }, CancellationToken.None);
            seen.AddRange(page.Items.Select(r => r.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(7, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(Enumerable.Range(0, 7).Select(i => $"def-{i}"), seen);
    }

    [Fact]
    public async Task Recurring_ties_on_fire_time_break_by_id()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var same = time.GetUtcNow().AddHours(1);

        // Identical fire times are ordinary here: a cron like "0 * * * *" gives every definition
        // the same next occurrence, so the id tiebreak carries the ordering.
        foreach (var id in new[] { "zebra", "alpha", "mango" })
        {
            await harness.Jobs.UpsertRecurringAsync(Recurring(id, same, time), CancellationToken.None);
        }

        var page = await harness.Monitoring.QueryRecurringAsync(new RecurringQuery(), CancellationToken.None);

        Assert.Equal(["alpha", "mango", "zebra"], page.Items.Select(r => r.Id).ToList());
    }

    [Fact]
    public async Task Recurring_filters_by_queue_and_tenant_and_clamps_the_limit()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var now = time.GetUtcNow();

        await harness.Jobs.UpsertRecurringAsync(Recurring("a", now.AddMinutes(1), time, queue: "reports"), CancellationToken.None);
        await harness.Jobs.UpsertRecurringAsync(Recurring("b", now.AddMinutes(2), time, queue: "default"), CancellationToken.None);
        await harness.Jobs.UpsertRecurringAsync(Recurring("c", now.AddMinutes(3), time, tenantId: "acme"), CancellationToken.None);

        var reports = await harness.Monitoring.QueryRecurringAsync(
            new RecurringQuery { Queue = "reports" }, CancellationToken.None);
        var acme = await harness.Monitoring.QueryRecurringAsync(
            new RecurringQuery { Tenant = TenantFilter.For("acme") }, CancellationToken.None);
        var untenanted = await harness.Monitoring.QueryRecurringAsync(
            new RecurringQuery { Tenant = TenantFilter.Untenanted }, CancellationToken.None);
        var clamped = await harness.Monitoring.QueryRecurringAsync(
            new RecurringQuery { Limit = 0 }, CancellationToken.None);

        Assert.Equal("a", Assert.Single(reports.Items).Id);
        Assert.Equal("c", Assert.Single(acme.Items).Id);
        Assert.Equal(["a", "b"], untenanted.Items.Select(r => r.Id).ToList());
        Assert.Equal(3, clamped.Items.Count);
    }

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("!!not-a-cursor!!")]
    [InlineData("%%%")]
    [InlineData(" ")]
    public async Task Recurring_rejects_an_undecodable_cursor(string cursor)
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);

        await Assert.ThrowsAsync<MillraceStorageException>(async () =>
            await harness.Monitoring.QueryRecurringAsync(
                new RecurringQuery { Cursor = cursor }, CancellationToken.None));
    }

    [Fact]
    public async Task Recurring_summary_carries_schedule_fields_and_no_outcome()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var next = time.GetUtcNow().AddHours(1);
        await harness.Jobs.UpsertRecurringAsync(Recurring("nightly", next, time), CancellationToken.None);

        var summary = Assert.Single(
            (await harness.Monitoring.QueryRecurringAsync(new RecurringQuery(), CancellationToken.None)).Items);

        Assert.Equal("nightly", summary.Id);
        Assert.Equal("* * * * *", summary.Cron);
        Assert.Equal(next, summary.NextFireTime);
        // Never fired yet, and there is no outcome field to populate even after it does.
        Assert.Null(summary.LastFireTime);
    }

    private const string ConformanceWorker = "conformance-worker";

    /// <summary>
    /// Claims everything claimable in one request, under one worker.
    /// </summary>
    /// <remarks>
    /// One claim rather than one per job: a claim takes whatever is available, so claiming twice
    /// leaves the second call nothing to find. Anything left unfinished afterwards stays
    /// <see cref="JobState.Processing"/>, which is how a fact stages a job that is still running.
    /// </remarks>
    private static async Task<IReadOnlyList<JobRecord>> ClaimAllAsync(IStorageHarness harness)
        => await harness.Jobs.ClaimAsync(
            new ClaimRequest(ConformanceWorker, ["default"], MaxCount: 100, TimeSpan.FromMinutes(5)),
            CancellationToken.None);

    /// <summary>Finishes a claimed job — the only way the contract lets one reach a terminal state.</summary>
    private static async Task FinishAsync(IStorageHarness harness, JobRecord claimed, JobState target)
    {
        var applied = await harness.Jobs.ApplyAsync(
            new JobTransition
            {
                JobId = claimed.Id,
                ExpectedWorkerId = ConformanceWorker,
                ExpectedAttempt = claimed.Attempt,
                TargetState = target,
                Failures = target == JobState.Dead ? claimed.Failures + 1 : claimed.Failures,
                FinishedAt = claimed.CreatedAt,
            },
            CancellationToken.None);

        Assert.True(applied, $"the fence rejected a transition to {target} while staging a fact");
    }

    private static RecurringJobRecord Recurring(
        string id, DateTimeOffset next, TimeProvider clock, string queue = "default", string? tenantId = null) => new()
    {
        Id = id,
        Cron = "* * * * *",
        Queue = queue,
        Invocation = new JobInvocation
        {
            TypeName = "Sample.IService, Sample",
            MethodName = "RunAsync",
            ParameterTypes = [],
            ArgumentsJson = [],
        },
        Retry = Retry.None,
        TenantId = tenantId,
        NextFireTime = next,
        CreatedAt = clock.GetUtcNow(),
        UpdatedAt = clock.GetUtcNow(),
    };

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
