using Microsoft.Extensions.Time.Testing;
using Millrace.Storage;
using Xunit;

namespace Millrace.Storage.Verification;

/// <summary>
/// The job-storage conformance suite (TCK) enforcing the atomicity contract
/// (ARCHITECTURE.md §4.2). Inherit this in your provider's test project and implement
/// <see cref="CreateHarnessAsync"/>; a provider that passes is a supported provider.
/// Facts are spread over partial files: claims/leases/fence here, continuations/idempotency/
/// cancellation, and scheduling/recurring alongside.
/// </summary>
public abstract partial class JobStorageConformanceSuite
{
    /// <summary>The instant every suite's fake clock starts at.</summary>
    /// <remarks>
    /// A fixed date rather than "now", so a failure reproduces with the same timestamps on any
    /// machine on any day. Providers that store timestamps at reduced precision are the reason it
    /// is a whole second with no fractional part.
    /// </remarks>
    protected static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The lease length the suite claims with unless a fact needs another.</summary>
    protected static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    /// <summary>Creates a fresh, empty store bound to <paramref name="time"/>.</summary>
    protected abstract ValueTask<IStorageHarness> CreateHarnessAsync(TimeProvider time);

    /// <summary>A fake clock starting at <see cref="Epoch"/>.</summary>
    /// <remarks>
    /// The suite advances this instead of sleeping, which is why a fact about lease expiry runs in
    /// microseconds. A provider that reads database time rather than the injected
    /// <see cref="TimeProvider"/> fails these facts, and that is deliberate — §4.1 requires it.
    /// </remarks>
    protected static FakeTimeProvider NewTime() => new(Epoch);

    /// <summary>A placeholder invocation, since no conformance fact executes a job.</summary>
    /// <remarks>
    /// The suite tests storage, not execution: it needs a well-formed invocation to persist and read
    /// back, and nothing ever resolves the type it names.
    /// </remarks>
    protected static JobInvocation DummyInvocation { get; } = new()
    {
        TypeName = "Conformance.Dummy, Conformance",
        MethodName = "RunAsync",
        ParameterTypes = [],
        ArgumentsJson = [],
    };

    /// <summary>Builds a job record with everything defaulted except what a fact is about.</summary>
    /// <remarks>
    /// Defaults to <see cref="Retry.None"/>, so a fact that does not mention retries cannot have its
    /// outcome quietly changed by a retry it did not ask for.
    /// </remarks>
    protected static JobRecord Job(
        TimeProvider time,
        JobState state = JobState.Enqueued,
        string queue = "default",
        int priority = 0,
        string? idempotencyKey = null,
        string? tenantId = null,
        JobId? parentId = null,
        DateTimeOffset? dueAt = null,
        Retry? retry = null) => new()
    {
        Id = JobId.New(time),
        Queue = queue,
        Invocation = DummyInvocation,
        State = state,
        Priority = priority,
        CreatedAt = time.GetUtcNow(),
        DueAt = dueAt,
        Retry = retry ?? Retry.None,
        IdempotencyKey = idempotencyKey,
        TenantId = tenantId,
        ParentId = parentId,
    };

    /// <summary>Enqueues one job and reads it back as the provider stored it.</summary>
    /// <remarks>
    /// Returns the stored record rather than the one passed in, so a fact asserts on what the
    /// provider persisted rather than on what it was handed — which is where the two disagree.
    /// </remarks>
    protected static async Task<JobRecord> EnqueueOneAsync(IJobStorage storage, JobRecord record)
    {
        await storage.EnqueueAsync([record], CancellationToken.None);
        return (await storage.GetJobAsync(record.Id, CancellationToken.None))!;
    }

    /// <summary>Claims exactly one job, asserting that exactly one came back.</summary>
    /// <remarks>
    /// The assertion is part of the helper on purpose: a fact that meant to claim one job and
    /// silently got none would otherwise fail later, somewhere that does not name the cause.
    /// </remarks>
    protected static async Task<JobRecord> ClaimOneAsync(
        IJobStorage storage, string workerId = "w1", string queue = "default", TimeSpan? lease = null)
    {
        var claimed = await storage.ClaimAsync(
            new ClaimRequest(workerId, [queue], 1, lease ?? Lease), CancellationToken.None);
        Assert.Single(claimed);
        return claimed[0];
    }

    /// <summary>Builds a transition already fenced to <paramref name="claimed"/>.</summary>
    /// <remarks>
    /// Takes the claimed record rather than loose values so the worker id and attempt come from what
    /// the provider actually returned. A fact testing the fence itself passes a doctored record;
    /// every other fact gets a fence that matches by construction and cannot fail for the wrong
    /// reason.
    /// </remarks>
    protected static JobTransition Transition(
        JobRecord claimed, JobState target, int? failures = null, DateTimeOffset? dueAt = null,
        string? error = null, bool activateContinuations = false, bool cancelContinuations = false,
        IReadOnlyList<JobRecord>? enqueue = null) => new()
    {
        JobId = claimed.Id,
        ExpectedWorkerId = claimed.WorkerId!,
        ExpectedAttempt = claimed.Attempt,
        TargetState = target,
        Failures = failures ?? claimed.Failures,
        DueAt = dueAt,
        Error = error,
        Enqueue = enqueue ?? [],
        ActivateContinuations = activateContinuations,
        CancelContinuations = cancelContinuations,
    };

    // ---------------------------------------------------------------- claims and leases

    [Fact]
    public async Task Claim_is_exclusive_under_contention()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);

        var records = Enumerable.Range(0, 100).Select(_ => Job(time)).ToList();
        await harness.Jobs.EnqueueAsync(records, CancellationToken.None);

        var claimed = new List<JobId>[8];
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            claimed[worker] = [];
            while (true)
            {
                var batch = await harness.Jobs.ClaimAsync(
                    new ClaimRequest($"w{worker}", ["default"], 5, Lease), CancellationToken.None);
                if (batch.Count == 0)
                {
                    return;
                }

                claimed[worker].AddRange(batch.Select(j => j.Id));
            }
        })));

        var all = claimed.SelectMany(ids => ids).ToList();
        Assert.Equal(100, all.Count);
        Assert.Equal(100, all.Distinct().Count());
    }

    [Fact]
    public async Task Claim_sets_processing_worker_lease_and_increments_attempt()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        await EnqueueOneAsync(harness.Jobs, Job(time));

        var claimed = await ClaimOneAsync(harness.Jobs, "w1");

        Assert.Equal(JobState.Processing, claimed.State);
        Assert.Equal("w1", claimed.WorkerId);
        Assert.Equal(time.GetUtcNow() + Lease, claimed.LeaseUntil);
        Assert.Equal(1, claimed.Attempt);
    }

    [Fact]
    public async Task Claim_returns_at_most_max_count()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        await harness.Jobs.EnqueueAsync([Job(time), Job(time), Job(time)], CancellationToken.None);

        var claimed = await harness.Jobs.ClaimAsync(
            new ClaimRequest("w1", ["default"], 2, Lease), CancellationToken.None);

        Assert.True(claimed.Count <= 2);
    }

    [Fact]
    public async Task Claim_only_returns_requested_queues()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var other = Job(time, queue: "other");
        await harness.Jobs.EnqueueAsync([Job(time), other], CancellationToken.None);

        var claimed = await harness.Jobs.ClaimAsync(
            new ClaimRequest("w1", ["other"], 10, Lease), CancellationToken.None);

        Assert.Single(claimed);
        Assert.Equal(other.Id, claimed[0].Id);
    }

    [Fact]
    public async Task Unexpired_lease_blocks_reclaim()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        await EnqueueOneAsync(harness.Jobs, Job(time));
        await ClaimOneAsync(harness.Jobs, "w1");

        time.Advance(Lease - TimeSpan.FromSeconds(1));
        var reclaim = await harness.Jobs.ClaimAsync(
            new ClaimRequest("w2", ["default"], 1, Lease), CancellationToken.None);

        Assert.Empty(reclaim);
    }

    [Fact]
    public async Task Expired_lease_is_reclaimable_and_increments_attempt()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        await EnqueueOneAsync(harness.Jobs, Job(time));
        var first = await ClaimOneAsync(harness.Jobs, "w1");

        time.Advance(Lease + TimeSpan.FromSeconds(1));
        var reclaimed = await ClaimOneAsync(harness.Jobs, "w2");

        Assert.Equal(first.Id, reclaimed.Id);
        Assert.Equal("w2", reclaimed.WorkerId);
        Assert.Equal(2, reclaimed.Attempt);
    }

    [Fact]
    public async Task Claim_order_is_priority_desc_then_fifo_across_queue_union()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var low1 = Job(time, queue: "a");
        var high1 = Job(time, queue: "b", priority: 5);
        var low2 = Job(time, queue: "b");
        var high2 = Job(time, queue: "a", priority: 5);
        await harness.Jobs.EnqueueAsync([low1], CancellationToken.None);
        await harness.Jobs.EnqueueAsync([high1], CancellationToken.None);
        await harness.Jobs.EnqueueAsync([low2], CancellationToken.None);
        await harness.Jobs.EnqueueAsync([high2], CancellationToken.None);

        var claimed = await harness.Jobs.ClaimAsync(
            new ClaimRequest("w1", ["a", "b"], 4, Lease), CancellationToken.None);

        Assert.Equal(new[] { high1.Id, high2.Id, low1.Id, low2.Id }, claimed.Select(j => j.Id).ToArray());
    }

    [Fact]
    public async Task Scheduled_failed_and_awaiting_jobs_are_never_claimable_directly()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);

        // Scheduled with a DueAt already in the past: still not claimable until activated.
        await harness.Jobs.EnqueueAsync(
            [Job(time, JobState.Scheduled, dueAt: time.GetUtcNow() - TimeSpan.FromMinutes(1))],
            CancellationToken.None);

        // Failed with a past DueAt, produced through a real transition.
        var failing = await EnqueueOneAsync(harness.Jobs, Job(time, retry: Retry.Fixed(TimeSpan.Zero, 5)));
        var claimed = await ClaimOneAsync(harness.Jobs);
        Assert.Equal(failing.Id, claimed.Id);
        Assert.True(await harness.Jobs.ApplyAsync(
            Transition(claimed, JobState.Failed, failures: 1, dueAt: time.GetUtcNow(), error: "boom"),
            CancellationToken.None));

        // Awaiting continuation.
        var parent = await EnqueueOneAsync(harness.Jobs, Job(time));
        var parentClaim = await ClaimOneAsync(harness.Jobs);
        Assert.Equal(parent.Id, parentClaim.Id);
        await harness.Jobs.EnqueueAsync(
            [Job(time, JobState.Awaiting, parentId: parent.Id)], CancellationToken.None);

        var eligible = await harness.Jobs.ClaimAsync(
            new ClaimRequest("w9", ["default"], 10, Lease), CancellationToken.None);

        Assert.Empty(eligible);
    }

    // ---------------------------------------------------------------- lease renewal

    [Fact]
    public async Task Renew_extends_lease_beyond_original_expiry()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        await EnqueueOneAsync(harness.Jobs, Job(time));
        var claimed = await ClaimOneAsync(harness.Jobs, "w1");

        time.Advance(TimeSpan.FromMinutes(4));
        var renewed = await harness.Jobs.RenewLeasesAsync("w1", [claimed.Id], Lease, CancellationToken.None);
        Assert.Equal(new[] { claimed.Id }, renewed);

        // Past the original expiry but inside the renewed window: still invisible to others.
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Empty(await harness.Jobs.ClaimAsync(
            new ClaimRequest("w2", ["default"], 1, Lease), CancellationToken.None));
    }

    [Fact]
    public async Task Renew_resurrects_expired_but_unreclaimed_lease()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        await EnqueueOneAsync(harness.Jobs, Job(time));
        var claimed = await ClaimOneAsync(harness.Jobs, "w1");

        time.Advance(Lease + TimeSpan.FromMinutes(1)); // expired, nobody reclaimed
        var renewed = await harness.Jobs.RenewLeasesAsync("w1", [claimed.Id], Lease, CancellationToken.None);

        Assert.Equal(new[] { claimed.Id }, renewed);
        Assert.Empty(await harness.Jobs.ClaimAsync(
            new ClaimRequest("w2", ["default"], 1, Lease), CancellationToken.None));
    }

    [Fact]
    public async Task Renewal_racing_reclaim_has_exactly_one_owner()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);

        for (var round = 0; round < 10; round++)
        {
            var job = await EnqueueOneAsync(harness.Jobs, Job(time));
            var claimed = await ClaimOneAsync(harness.Jobs, "w1");
            Assert.Equal(job.Id, claimed.Id);
            time.Advance(Lease + TimeSpan.FromSeconds(1)); // expired, contested

            var renewTask = Task.Run(() => harness.Jobs.RenewLeasesAsync(
                "w1", [claimed.Id], Lease, CancellationToken.None).AsTask());
            var reclaimTask = Task.Run(() => harness.Jobs.ClaimAsync(
                new ClaimRequest("w2", ["default"], 1, Lease), CancellationToken.None).AsTask());
            await Task.WhenAll(renewTask, reclaimTask);

            // Whichever committed first wins — never both, never neither.
            var renewedCount = (await renewTask).Count;
            var reclaimedCount = (await reclaimTask).Count;
            Assert.Equal(1, renewedCount + reclaimedCount);

            // Settle the job so the next round starts clean.
            var owner = reclaimedCount == 1 ? (await reclaimTask)[0] : claimed;
            Assert.True(await harness.Jobs.ApplyAsync(
                Transition(owner, JobState.Succeeded), CancellationToken.None));
        }
    }

    [Fact]
    public async Task GetJob_roundtrips_every_field()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var parent = await EnqueueOneAsync(harness.Jobs, Job(time));
        var record = Job(time, JobState.Scheduled, queue: "roundtrip", priority: 7,
            idempotencyKey: "rk", tenantId: "tenant-r",
            dueAt: time.GetUtcNow() + TimeSpan.FromMinutes(10),
            retry: Retry.Exponential(4, TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(2)));
        record = record with
        {
            ParentId = parent.Id,
            RequeuedFrom = parent.Id,
            Invocation = new JobInvocation
            {
                TypeName = "My.Jobs.IMailer, My.Jobs",
                MethodName = "SendAsync",
                ParameterTypes = ["System.Int32, System.Private.CoreLib"],
                ArgumentsJson = ["42"],
            },
        };
        await harness.Jobs.EnqueueAsync([record], CancellationToken.None);

        var stored = await harness.Jobs.GetJobAsync(record.Id, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal(record.Id, stored.Id);
        Assert.Equal("roundtrip", stored.Queue);
        Assert.Equal(JobState.Scheduled, stored.State);
        Assert.Equal(7, stored.Priority);
        Assert.Equal(record.CreatedAt, stored.CreatedAt);
        Assert.Equal(record.DueAt, stored.DueAt);
        Assert.Equal("rk", stored.IdempotencyKey);
        Assert.Equal("tenant-r", stored.TenantId);
        Assert.Equal(parent.Id, stored.ParentId);
        Assert.Equal(parent.Id, stored.RequeuedFrom);
        Assert.Equal(record.Retry, stored.Retry);
        Assert.Equal(0, stored.Attempt);
        Assert.Equal(0, stored.Failures);
        Assert.False(stored.CancelRequested);
        // Invocation lists compare by sequence — providers that serialize must round-trip them.
        Assert.Equal(record.Invocation.TypeName, stored.Invocation.TypeName);
        Assert.Equal(record.Invocation.MethodName, stored.Invocation.MethodName);
        Assert.Equal(record.Invocation.ParameterTypes, stored.Invocation.ParameterTypes);
        Assert.Equal(record.Invocation.ArgumentsJson, stored.Invocation.ArgumentsJson);
    }

    [Fact]
    public async Task Renew_excludes_jobs_reclaimed_by_another_worker()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        await EnqueueOneAsync(harness.Jobs, Job(time));
        var claimed = await ClaimOneAsync(harness.Jobs, "w1");

        time.Advance(Lease + TimeSpan.FromSeconds(1));
        await ClaimOneAsync(harness.Jobs, "w2");

        var renewed = await harness.Jobs.RenewLeasesAsync("w1", [claimed.Id], Lease, CancellationToken.None);
        Assert.Empty(renewed);
    }

    // ---------------------------------------------------------------- the apply fence

    [Fact]
    public async Task Apply_with_wrong_worker_is_rejected_without_changes()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        await EnqueueOneAsync(harness.Jobs, Job(time));
        var claimed = await ClaimOneAsync(harness.Jobs, "w1");

        var applied = await harness.Jobs.ApplyAsync(
            Transition(claimed, JobState.Succeeded) with { ExpectedWorkerId = "intruder" },
            CancellationToken.None);

        Assert.False(applied);
        var current = await harness.Jobs.GetJobAsync(claimed.Id, CancellationToken.None);
        Assert.Equal(JobState.Processing, current!.State);
    }

    [Fact]
    public async Task Apply_with_wrong_attempt_is_rejected_without_changes()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        await EnqueueOneAsync(harness.Jobs, Job(time));
        var claimed = await ClaimOneAsync(harness.Jobs, "w1");

        var applied = await harness.Jobs.ApplyAsync(
            Transition(claimed, JobState.Succeeded) with { ExpectedAttempt = claimed.Attempt + 1 },
            CancellationToken.None);

        Assert.False(applied);
        Assert.Equal(JobState.Processing,
            (await harness.Jobs.GetJobAsync(claimed.Id, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task Apply_zombie_versus_new_owner_exactly_one_wins()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        await EnqueueOneAsync(harness.Jobs, Job(time));
        var zombie = await ClaimOneAsync(harness.Jobs, "w1");

        time.Advance(Lease + TimeSpan.FromSeconds(1));
        var owner = await ClaimOneAsync(harness.Jobs, "w2");

        Assert.False(await harness.Jobs.ApplyAsync(
            Transition(zombie, JobState.Succeeded), CancellationToken.None));
        Assert.True(await harness.Jobs.ApplyAsync(
            Transition(owner, JobState.Succeeded), CancellationToken.None));
        Assert.Equal(JobState.Succeeded,
            (await harness.Jobs.GetJobAsync(owner.Id, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task Apply_succeeded_sets_terminal_fields()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        await EnqueueOneAsync(harness.Jobs, Job(time));
        var claimed = await ClaimOneAsync(harness.Jobs);
        var finishedAt = time.GetUtcNow();

        Assert.True(await harness.Jobs.ApplyAsync(
            Transition(claimed, JobState.Succeeded) with { FinishedAt = finishedAt },
            CancellationToken.None));

        var current = (await harness.Jobs.GetJobAsync(claimed.Id, CancellationToken.None))!;
        Assert.Equal(JobState.Succeeded, current.State);
        Assert.Equal(finishedAt, current.FinishedAt);
        Assert.Null(current.WorkerId);
        Assert.Null(current.LeaseUntil);
    }

    [Fact]
    public async Task Run_now_makes_a_retrying_job_claimable_without_spending_retry_budget()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        await EnqueueOneAsync(harness.Jobs, Job(time, retry: Retry.Fixed(TimeSpan.FromHours(1), 5)));
        var claimed = await ClaimOneAsync(harness.Jobs);

        Assert.True(await harness.Jobs.ApplyAsync(
            Transition(claimed, JobState.Failed, failures: 1, dueAt: time.GetUtcNow().AddHours(1), error: "boom"),
            CancellationToken.None));

        Assert.True(await harness.Jobs.TryRunNowAsync(claimed.Id, CancellationToken.None));

        var current = (await harness.Jobs.GetJobAsync(claimed.Id, CancellationToken.None))!;

        // Claimable now, without waiting out the hour and without activation having to run.
        Assert.Equal(JobState.Enqueued, current.State);
        Assert.Null(current.DueAt);

        // The point of the operation: nothing was attempted, so nothing is spent. An operator who
        // deploys a fix and runs the job now must not find it dead-lettered a step early (§11.32).
        Assert.Equal(1, current.Attempt);
        Assert.Equal(1, current.Failures);
        Assert.Equal("boom", current.LastError);

        Assert.Equal(claimed.Id, (await ClaimOneAsync(harness.Jobs, "w2")).Id);
    }

    [Theory]
    [InlineData(JobState.Scheduled)]
    [InlineData(JobState.Enqueued)]
    [InlineData(JobState.Processing)]
    [InlineData(JobState.Succeeded)]
    public async Task Run_now_refuses_anything_not_awaiting_a_retry(JobState state)
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);

        var job = state == JobState.Scheduled
            ? Job(time) with { State = JobState.Scheduled, DueAt = time.GetUtcNow().AddHours(1) }
            : Job(time);
        await EnqueueOneAsync(harness.Jobs, job);

        if (state is JobState.Processing or JobState.Succeeded)
        {
            var claimed = await ClaimOneAsync(harness.Jobs);
            if (state == JobState.Succeeded)
            {
                await harness.Jobs.ApplyAsync(
                    Transition(claimed, JobState.Succeeded), CancellationToken.None);
            }
        }

        // A Scheduled job's due time is the caller's intent, not a backoff; a Processing one is
        // already running; a terminal one needs a requeue, which mints a new job (§11.18). Only
        // Failed means "waiting on a clock to try again".
        Assert.False(await harness.Jobs.TryRunNowAsync(job.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Run_now_on_an_unknown_job_reports_false()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);

        Assert.False(await harness.Jobs.TryRunNowAsync(JobId.New(time), CancellationToken.None));
    }

    [Fact]
    public async Task Apply_failed_schedules_retry_and_activation_makes_it_claimable()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        await EnqueueOneAsync(harness.Jobs, Job(time, retry: Retry.Fixed(TimeSpan.FromSeconds(30), 5)));
        var claimed = await ClaimOneAsync(harness.Jobs);
        var dueAt = time.GetUtcNow() + TimeSpan.FromSeconds(30);

        Assert.True(await harness.Jobs.ApplyAsync(
            Transition(claimed, JobState.Failed, failures: 1, dueAt: dueAt, error: "boom"),
            CancellationToken.None));

        var current = (await harness.Jobs.GetJobAsync(claimed.Id, CancellationToken.None))!;
        Assert.Equal(JobState.Failed, current.State);
        Assert.Equal(1, current.Failures);
        Assert.Equal(dueAt, current.DueAt);
        Assert.Equal("boom", current.LastError);
        Assert.Null(current.WorkerId);
        Assert.Null(current.LeaseUntil);

        // Not due yet.
        Assert.Equal(0, await harness.Jobs.ActivateDueJobsAsync(time.GetUtcNow(), 10, CancellationToken.None));

        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, await harness.Jobs.ActivateDueJobsAsync(time.GetUtcNow(), 10, CancellationToken.None));

        var activated = (await harness.Jobs.GetJobAsync(claimed.Id, CancellationToken.None))!;
        Assert.Equal(JobState.Enqueued, activated.State);
        Assert.Null(activated.DueAt);

        var reclaimed = await ClaimOneAsync(harness.Jobs, "w2");
        Assert.Equal(claimed.Id, reclaimed.Id);
        Assert.Equal(2, reclaimed.Attempt);
        Assert.Equal(1, reclaimed.Failures);
    }

    [Fact]
    public async Task Apply_release_returns_job_to_queue_without_consuming_retry_budget()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        await EnqueueOneAsync(harness.Jobs, Job(time));
        var claimed = await ClaimOneAsync(harness.Jobs, "w1");

        Assert.True(await harness.Jobs.ApplyAsync(
            Transition(claimed, JobState.Enqueued), CancellationToken.None));

        var released = (await harness.Jobs.GetJobAsync(claimed.Id, CancellationToken.None))!;
        Assert.Equal(JobState.Enqueued, released.State);
        Assert.Equal(0, released.Failures);
        Assert.Null(released.WorkerId);
        Assert.Null(released.LeaseUntil);

        // Immediately claimable — and the old attempt's zombie apply is fence-rejected.
        var reclaimed = await ClaimOneAsync(harness.Jobs, "w2");
        Assert.Equal(claimed.Id, reclaimed.Id);
        Assert.Equal(2, reclaimed.Attempt);
        Assert.False(await harness.Jobs.ApplyAsync(
            Transition(claimed, JobState.Succeeded), CancellationToken.None));
    }

    [Fact]
    public async Task Apply_is_all_or_nothing_when_an_enqueue_insert_fails()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        await EnqueueOneAsync(harness.Jobs, Job(time));
        var claimed = await ClaimOneAsync(harness.Jobs);

        var orphan = Job(time, JobState.Awaiting, parentId: JobId.New(time));
        await Assert.ThrowsAsync<MillraceParentJobNotFoundException>(async () =>
            await harness.Jobs.ApplyAsync(
                Transition(claimed, JobState.Succeeded, enqueue: [orphan]), CancellationToken.None));

        // Nothing committed: the job is still Processing and the insert is absent.
        Assert.Equal(JobState.Processing,
            (await harness.Jobs.GetJobAsync(claimed.Id, CancellationToken.None))!.State);
        Assert.Null(await harness.Jobs.GetJobAsync(orphan.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Apply_enqueue_inserts_commit_atomically_with_the_transition()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        await EnqueueOneAsync(harness.Jobs, Job(time));
        var claimed = await ClaimOneAsync(harness.Jobs);

        var followUp = Job(time);
        Assert.True(await harness.Jobs.ApplyAsync(
            Transition(claimed, JobState.Succeeded, enqueue: [followUp]), CancellationToken.None));

        Assert.Equal(JobState.Succeeded,
            (await harness.Jobs.GetJobAsync(claimed.Id, CancellationToken.None))!.State);
        Assert.Equal(JobState.Enqueued,
            (await harness.Jobs.GetJobAsync(followUp.Id, CancellationToken.None))!.State);
    }
}
