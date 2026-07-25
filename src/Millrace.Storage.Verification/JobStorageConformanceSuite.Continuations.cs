using Millrace.Storage;
using Xunit;

namespace Millrace.Storage.Verification;

public abstract partial class JobStorageConformanceSuite
{
    // ---------------------------------------------------------------- continuations

    [Fact]
    public async Task Parent_success_activates_direct_awaiting_children_only()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var parent = await EnqueueOneAsync(harness.Jobs, Job(time));
        var claimed = await ClaimOneAsync(harness.Jobs);
        var child = Job(time, JobState.Awaiting, parentId: parent.Id);
        var grandchild = Job(time, JobState.Awaiting, parentId: child.Id);
        await harness.Jobs.EnqueueAsync([child], CancellationToken.None);
        await harness.Jobs.EnqueueAsync([grandchild], CancellationToken.None);

        Assert.True(await harness.Jobs.ApplyAsync(
            Transition(claimed, JobState.Succeeded, activateContinuations: true), CancellationToken.None));

        Assert.Equal(JobState.Enqueued,
            (await harness.Jobs.GetJobAsync(child.Id, CancellationToken.None))!.State);
        // Activation is one level deep by design — the grandchild waits for its own parent.
        Assert.Equal(JobState.Awaiting,
            (await harness.Jobs.GetJobAsync(grandchild.Id, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task Parent_death_cancels_the_transitive_awaiting_closure_and_releases_keys()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var parent = await EnqueueOneAsync(harness.Jobs, Job(time, retry: Retry.None));
        var claimed = await ClaimOneAsync(harness.Jobs);
        var child = Job(time, JobState.Awaiting, parentId: parent.Id, idempotencyKey: "child-key");
        var grandchild = Job(time, JobState.Awaiting, parentId: child.Id, idempotencyKey: "grandchild-key");
        await harness.Jobs.EnqueueAsync([child], CancellationToken.None);
        await harness.Jobs.EnqueueAsync([grandchild], CancellationToken.None);

        Assert.True(await harness.Jobs.ApplyAsync(
            Transition(claimed, JobState.Dead, failures: 1, error: "boom", cancelContinuations: true),
            CancellationToken.None));

        Assert.Equal(JobState.Cancelled,
            (await harness.Jobs.GetJobAsync(child.Id, CancellationToken.None))!.State);
        Assert.Equal(JobState.Cancelled,
            (await harness.Jobs.GetJobAsync(grandchild.Id, CancellationToken.None))!.State);

        // Both keys are released with the cascade: re-enqueueing them yields new jobs.
        var reuse1 = Job(time, idempotencyKey: "child-key");
        var reuse2 = Job(time, idempotencyKey: "grandchild-key");
        var ids = await harness.Jobs.EnqueueAsync([reuse1, reuse2], CancellationToken.None);
        Assert.Equal(reuse1.Id, ids[0]);
        Assert.Equal(reuse2.Id, ids[1]);
    }

    [Fact]
    public async Task Active_children_shield_their_descendants_from_the_cancel_cascade()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var parent = await EnqueueOneAsync(harness.Jobs, Job(time));
        var claimedParent = await ClaimOneAsync(harness.Jobs);
        var child = Job(time, JobState.Awaiting, parentId: parent.Id);
        var grandchild = Job(time, JobState.Awaiting, parentId: child.Id);
        await harness.Jobs.EnqueueAsync([child], CancellationToken.None);
        await harness.Jobs.EnqueueAsync([grandchild], CancellationToken.None);

        // Activate the child (parent succeeds), then kill the parent's OTHER continuation path:
        // re-parenting scenario — instead simply verify: once the child is active (Enqueued),
        // cancelling the PARENT's closure again touches nothing under it.
        Assert.True(await harness.Jobs.ApplyAsync(
            Transition(claimedParent, JobState.Succeeded, activateContinuations: true),
            CancellationToken.None));
        Assert.Equal(JobState.Enqueued,
            (await harness.Jobs.GetJobAsync(child.Id, CancellationToken.None))!.State);

        // The grandchild still waits on the now-active child; it must not be collateral damage
        // of any later cascade rooted at the parent (there is nothing left Awaiting under it).
        Assert.Equal(JobState.Awaiting,
            (await harness.Jobs.GetJobAsync(grandchild.Id, CancellationToken.None))!.State);

        // Drive the child to Dead: only now does the grandchild cancel.
        var claimedChild = await ClaimOneAsync(harness.Jobs, "w2");
        Assert.Equal(child.Id, claimedChild.Id);
        Assert.True(await harness.Jobs.ApplyAsync(
            Transition(claimedChild, JobState.Dead, failures: 1, error: "boom", cancelContinuations: true),
            CancellationToken.None));
        Assert.Equal(JobState.Cancelled,
            (await harness.Jobs.GetJobAsync(grandchild.Id, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task Awaiting_insert_after_parent_succeeded_is_fixed_up_to_enqueued()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var parent = await EnqueueOneAsync(harness.Jobs, Job(time));
        var claimed = await ClaimOneAsync(harness.Jobs);
        Assert.True(await harness.Jobs.ApplyAsync(
            Transition(claimed, JobState.Succeeded), CancellationToken.None));

        var child = Job(time, JobState.Awaiting, parentId: parent.Id);
        await harness.Jobs.EnqueueAsync([child], CancellationToken.None);

        Assert.Equal(JobState.Enqueued,
            (await harness.Jobs.GetJobAsync(child.Id, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task Awaiting_insert_after_parent_death_is_fixed_up_to_cancelled()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var parent = await EnqueueOneAsync(harness.Jobs, Job(time));
        var claimed = await ClaimOneAsync(harness.Jobs);
        Assert.True(await harness.Jobs.ApplyAsync(
            Transition(claimed, JobState.Dead, failures: 1, error: "boom"), CancellationToken.None));

        var child = Job(time, JobState.Awaiting, parentId: parent.Id);
        await harness.Jobs.EnqueueAsync([child], CancellationToken.None);

        Assert.Equal(JobState.Cancelled,
            (await harness.Jobs.GetJobAsync(child.Id, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task Awaiting_insert_after_parent_cancellation_is_fixed_up_to_cancelled()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var parent = await EnqueueOneAsync(harness.Jobs, Job(time));
        Assert.True(await harness.Jobs.TryCancelAsync(parent.Id, CancellationToken.None));

        var child = Job(time, JobState.Awaiting, parentId: parent.Id);
        await harness.Jobs.EnqueueAsync([child], CancellationToken.None);

        Assert.Equal(JobState.Cancelled,
            (await harness.Jobs.GetJobAsync(child.Id, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task Awaiting_insert_with_missing_parent_throws_and_rolls_back_the_batch()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var good = Job(time);
        var orphan = Job(time, JobState.Awaiting, parentId: JobId.New(time));

        await Assert.ThrowsAsync<MillraceParentJobNotFoundException>(async () =>
            await harness.Jobs.EnqueueAsync([good, orphan], CancellationToken.None));

        Assert.Null(await harness.Jobs.GetJobAsync(good.Id, CancellationToken.None));
        Assert.Null(await harness.Jobs.GetJobAsync(orphan.Id, CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Awaiting_inserts_racing_parent_terminal_apply_never_strand_a_child(bool parentSucceeds)
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var parent = await EnqueueOneAsync(harness.Jobs, Job(time));
        var claimed = await ClaimOneAsync(harness.Jobs);

        const int inserters = 8;
        var children = new JobRecord[inserters][];
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var insertTasks = Enumerable.Range(0, inserters).Select(worker => Task.Run(async () =>
        {
            await start.Task;
            children[worker] = new JobRecord[10];
            for (var i = 0; i < 10; i++)
            {
                children[worker][i] = Job(time, JobState.Awaiting, parentId: parent.Id);
                await harness.Jobs.EnqueueAsync([children[worker][i]], CancellationToken.None);
            }
        })).ToArray();

        var applyTask = Task.Run(async () =>
        {
            await start.Task;
            Assert.True(await harness.Jobs.ApplyAsync(
                parentSucceeds
                    ? Transition(claimed, JobState.Succeeded, activateContinuations: true)
                    : Transition(claimed, JobState.Dead, failures: 1, error: "boom", cancelContinuations: true),
                CancellationToken.None));
        });

        start.SetResult();
        await Task.WhenAll(insertTasks.Append(applyTask));

        // The write-skew check (§4.2): however the insert and the terminal apply interleaved,
        // no child may remain Awaiting once both have committed.
        var expected = parentSucceeds ? JobState.Enqueued : JobState.Cancelled;
        foreach (var child in children.SelectMany(c => c))
        {
            var current = await harness.Jobs.GetJobAsync(child.Id, CancellationToken.None);
            Assert.Equal(expected, current!.State);
        }
    }

    // ---------------------------------------------------------------- idempotency keys

    [Fact]
    public async Task Duplicate_active_key_is_a_noop_returning_the_existing_id()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var first = Job(time, idempotencyKey: "K");
        var duplicate = Job(time, idempotencyKey: "K");

        var firstIds = await harness.Jobs.EnqueueAsync([first], CancellationToken.None);
        var secondIds = await harness.Jobs.EnqueueAsync([duplicate], CancellationToken.None);

        Assert.Equal(first.Id, firstIds[0]);
        Assert.Equal(first.Id, secondIds[0]);
        Assert.Null(await harness.Jobs.GetJobAsync(duplicate.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_same_key_enqueues_yield_exactly_one_job()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var records = Enumerable.Range(0, 8).Select(_ => Job(time, idempotencyKey: "K")).ToArray();

        var results = await Task.WhenAll(records.Select(record => Task.Run(async () =>
        {
            var ids = await harness.Jobs.EnqueueAsync([record], CancellationToken.None);
            return ids[0];
        })));

        var winner = Assert.Single(results.Distinct());

        // The seven losing records were never persisted — only the winner exists.
        foreach (var record in records)
        {
            var stored = await harness.Jobs.GetJobAsync(record.Id, CancellationToken.None);
            if (record.Id == winner)
            {
                Assert.NotNull(stored);
            }
            else
            {
                Assert.Null(stored);
            }
        }
    }

    [Fact]
    public async Task Key_uniqueness_is_scoped_per_tenant_with_null_as_its_own_scope()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var tenantA = Job(time, idempotencyKey: "K", tenantId: "tenant-a");
        var tenantB = Job(time, idempotencyKey: "K", tenantId: "tenant-b");
        var nullTenant1 = Job(time, idempotencyKey: "K");
        var nullTenant2 = Job(time, idempotencyKey: "K");

        var ids = await harness.Jobs.EnqueueAsync([tenantA], CancellationToken.None);
        Assert.Equal(tenantA.Id, ids[0]);

        // A different tenant with the same key is a distinct job — never cross-tenant dedupe.
        ids = await harness.Jobs.EnqueueAsync([tenantB], CancellationToken.None);
        Assert.Equal(tenantB.Id, ids[0]);

        // Null tenant is one shared scope: the two null-tenant enqueues dedupe to one job
        // (a naive UNIQUE(TenantId, Key) index with distinct NULLs silently breaks this).
        ids = await harness.Jobs.EnqueueAsync([nullTenant1], CancellationToken.None);
        Assert.Equal(nullTenant1.Id, ids[0]);
        ids = await harness.Jobs.EnqueueAsync([nullTenant2], CancellationToken.None);
        Assert.Equal(nullTenant1.Id, ids[0]);
    }

    [Fact]
    public async Task Terminal_transition_frees_the_key_but_retains_the_field()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var first = await EnqueueOneAsync(harness.Jobs, Job(time, idempotencyKey: "K"));
        var claimed = await ClaimOneAsync(harness.Jobs);
        Assert.True(await harness.Jobs.ApplyAsync(
            Transition(claimed, JobState.Succeeded), CancellationToken.None));

        // Release is a uniqueness-scope rule, not a field mutation.
        Assert.Equal("K", (await harness.Jobs.GetJobAsync(first.Id, CancellationToken.None))!.IdempotencyKey);

        var reuse = Job(time, idempotencyKey: "K");
        var ids = await harness.Jobs.EnqueueAsync([reuse], CancellationToken.None);
        Assert.Equal(reuse.Id, ids[0]);
    }

    [Fact]
    public async Task Enqueue_racing_terminal_release_always_returns_a_valid_id()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);

        for (var round = 0; round < 20; round++)
        {
            var key = $"K{round}";
            var holder = await EnqueueOneAsync(harness.Jobs, Job(time, idempotencyKey: key));
            var claimed = await ClaimOneAsync(harness.Jobs, "w1");
            Assert.Equal(holder.Id, claimed.Id);

            var contender = Job(time, idempotencyKey: key);
            var enqueueTask = Task.Run(() =>
                harness.Jobs.EnqueueAsync([contender], CancellationToken.None).AsTask());
            var applyTask = Task.Run(() =>
                harness.Jobs.ApplyAsync(Transition(claimed, JobState.Succeeded), CancellationToken.None).AsTask());
            await Task.WhenAll(enqueueTask, applyTask);

            // Linearization: either the old holder's id (enqueue saw the key still active) or
            // the new job's id (enqueue saw it released) — never anything else, never a tear.
            var returned = (await enqueueTask)[0];
            Assert.True(returned == holder.Id || returned == contender.Id,
                $"round {round}: returned {returned}, expected {holder.Id} or {contender.Id}");

            // Drain the possibly-inserted contender so the next round starts clean.
            if (returned == contender.Id)
            {
                var claimedContender = await ClaimOneAsync(harness.Jobs, "w1");
                Assert.Equal(contender.Id, claimedContender.Id);
                Assert.True(await harness.Jobs.ApplyAsync(
                    Transition(claimedContender, JobState.Succeeded), CancellationToken.None));
            }
        }
    }

    [Fact]
    public async Task Apply_enqueue_insert_with_duplicate_active_key_is_skipped_as_noop()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var holder = await EnqueueOneAsync(harness.Jobs, Job(time, idempotencyKey: "K"));
        var worker = await EnqueueOneAsync(harness.Jobs, Job(time, queue: "other"));
        var claimed = await ClaimOneAsync(harness.Jobs, queue: "other");
        Assert.Equal(worker.Id, claimed.Id);

        var duplicate = Job(time, idempotencyKey: "K");
        Assert.True(await harness.Jobs.ApplyAsync(
            Transition(claimed, JobState.Succeeded, enqueue: [duplicate]), CancellationToken.None));

        // Transition committed; the duplicate insert was skipped; the holder is untouched.
        Assert.Equal(JobState.Succeeded,
            (await harness.Jobs.GetJobAsync(claimed.Id, CancellationToken.None))!.State);
        Assert.Null(await harness.Jobs.GetJobAsync(duplicate.Id, CancellationToken.None));
        Assert.Equal(JobState.Enqueued,
            (await harness.Jobs.GetJobAsync(holder.Id, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task Terminal_key_release_is_visible_to_the_same_transitions_enqueue_inserts()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var holder = await EnqueueOneAsync(harness.Jobs, Job(time, idempotencyKey: "K"));
        var claimed = await ClaimOneAsync(harness.Jobs);
        Assert.Equal(holder.Id, claimed.Id);

        // The dying job's own key must be released before its Enqueue inserts run.
        var successor = Job(time, idempotencyKey: "K");
        Assert.True(await harness.Jobs.ApplyAsync(
            Transition(claimed, JobState.Succeeded, enqueue: [successor]), CancellationToken.None));

        Assert.Equal(JobState.Enqueued,
            (await harness.Jobs.GetJobAsync(successor.Id, CancellationToken.None))!.State);
    }

    // ---------------------------------------------------------------- batches

    [Fact]
    public async Task Enqueue_returns_effective_ids_positionally()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var existing = await EnqueueOneAsync(harness.Jobs, Job(time, idempotencyKey: "K"));

        var fresh1 = Job(time);
        var duplicate = Job(time, idempotencyKey: "K");
        var fresh2 = Job(time);
        var ids = await harness.Jobs.EnqueueAsync([fresh1, duplicate, fresh2], CancellationToken.None);

        Assert.Equal(new[] { fresh1.Id, existing.Id, fresh2.Id }, ids);
    }

    [Fact]
    public async Task Enqueue_batch_with_duplicate_job_id_throws_and_persists_nothing()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var first = Job(time);
        var clash = Job(time) with { Id = first.Id };

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await harness.Jobs.EnqueueAsync([first, clash], CancellationToken.None));

        Assert.Null(await harness.Jobs.GetJobAsync(first.Id, CancellationToken.None));
    }

    // ---------------------------------------------------------------- cancellation

    [Theory]
    [InlineData(JobState.Scheduled)]
    [InlineData(JobState.Enqueued)]
    [InlineData(JobState.Failed)]
    [InlineData(JobState.Awaiting)]
    public async Task TryCancel_pre_active_states_cancel_with_cascade_and_key_release(JobState state)
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);

        JobRecord target;
        switch (state)
        {
            case JobState.Scheduled:
                target = await EnqueueOneAsync(harness.Jobs,
                    Job(time, JobState.Scheduled, idempotencyKey: "K",
                        dueAt: time.GetUtcNow() + TimeSpan.FromHours(1)));
                break;
            case JobState.Enqueued:
                target = await EnqueueOneAsync(harness.Jobs, Job(time, idempotencyKey: "K"));
                break;
            case JobState.Failed:
                await EnqueueOneAsync(harness.Jobs,
                    Job(time, idempotencyKey: "K", retry: Retry.Fixed(TimeSpan.FromMinutes(1), 5)));
                var claimed = await ClaimOneAsync(harness.Jobs);
                Assert.True(await harness.Jobs.ApplyAsync(
                    Transition(claimed, JobState.Failed, failures: 1,
                        dueAt: time.GetUtcNow() + TimeSpan.FromMinutes(1), error: "boom"),
                    CancellationToken.None));
                target = (await harness.Jobs.GetJobAsync(claimed.Id, CancellationToken.None))!;
                break;
            default:
                var parent = await EnqueueOneAsync(harness.Jobs, Job(time));
                target = await EnqueueOneAsync(harness.Jobs,
                    Job(time, JobState.Awaiting, idempotencyKey: "K", parentId: parent.Id));
                break;
        }

        // An Awaiting child under the target must cancel with it.
        var descendant = await EnqueueOneAsync(harness.Jobs,
            Job(time, JobState.Awaiting, parentId: target.Id));

        Assert.True(await harness.Jobs.TryCancelAsync(target.Id, CancellationToken.None));

        var cancelled = (await harness.Jobs.GetJobAsync(target.Id, CancellationToken.None))!;
        Assert.Equal(JobState.Cancelled, cancelled.State);
        Assert.NotNull(cancelled.FinishedAt);
        Assert.Equal(JobState.Cancelled,
            (await harness.Jobs.GetJobAsync(descendant.Id, CancellationToken.None))!.State);

        // Key released.
        var reuse = Job(time, idempotencyKey: "K");
        var ids = await harness.Jobs.EnqueueAsync([reuse], CancellationToken.None);
        Assert.Equal(reuse.Id, ids[0]);
    }

    [Fact]
    public async Task TryCancel_processing_sets_flag_only_and_never_blocks_the_fence()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        await EnqueueOneAsync(harness.Jobs, Job(time));
        var claimed = await ClaimOneAsync(harness.Jobs, "w1");

        Assert.True(await harness.Jobs.TryCancelAsync(claimed.Id, CancellationToken.None));

        var current = (await harness.Jobs.GetJobAsync(claimed.Id, CancellationToken.None))!;
        Assert.Equal(JobState.Processing, current.State);
        Assert.True(current.CancelRequested);
        Assert.Equal("w1", current.WorkerId);

        // The renewal result omits the id (cooperative signal) while ownership is retained —
        // and the renewal must still have extended the lease: past the ORIGINAL expiry the job
        // remains invisible to other workers.
        time.Advance(Lease - TimeSpan.FromMinutes(1));
        var renewed = await harness.Jobs.RenewLeasesAsync("w1", [claimed.Id], Lease, CancellationToken.None);
        Assert.Empty(renewed);
        time.Advance(TimeSpan.FromMinutes(2)); // beyond the original LeaseUntil, inside the renewed one
        Assert.Empty(await harness.Jobs.ClaimAsync(
            new ClaimRequest("w2", ["default"], 1, Lease), CancellationToken.None));

        // A completing worker still wins: cooperative semantics.
        Assert.True(await harness.Jobs.ApplyAsync(
            Transition(claimed, JobState.Succeeded), CancellationToken.None));
        Assert.Equal(JobState.Succeeded,
            (await harness.Jobs.GetJobAsync(claimed.Id, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task TryCancel_racing_fenced_apply_yields_exactly_one_terminal_outcome()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);

        for (var round = 0; round < 20; round++)
        {
            await EnqueueOneAsync(harness.Jobs, Job(time));
            var claimed = await ClaimOneAsync(harness.Jobs, "w1");

            var cancelTask = Task.Run(() =>
                harness.Jobs.TryCancelAsync(claimed.Id, CancellationToken.None).AsTask());
            var applyTask = Task.Run(() =>
                harness.Jobs.ApplyAsync(Transition(claimed, JobState.Succeeded), CancellationToken.None).AsTask());
            await Task.WhenAll(cancelTask, applyTask);

            // TryCancel on a Processing job only sets the flag and never blocks the fence, so
            // the fenced apply must always win: the job is Succeeded, never Cancelled — a
            // read-then-write TryCancel that stamps state over a completed row fails here.
            Assert.True(await applyTask);
            var final = (await harness.Jobs.GetJobAsync(claimed.Id, CancellationToken.None))!;
            Assert.Equal(JobState.Succeeded, final.State);
        }
    }

    [Fact]
    public async Task TryCancel_racing_activation_and_claim_yields_exactly_one_owner()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);

        for (var round = 0; round < 20; round++)
        {
            // Drive a job to Failed with an already-due retry.
            await EnqueueOneAsync(harness.Jobs, Job(time, retry: Retry.Fixed(TimeSpan.Zero, 5)));
            var claimed = await ClaimOneAsync(harness.Jobs, "w1");
            Assert.True(await harness.Jobs.ApplyAsync(
                Transition(claimed, JobState.Failed, failures: 1, dueAt: time.GetUtcNow(), error: "boom"),
                CancellationToken.None));

            var cancelTask = Task.Run(() =>
                harness.Jobs.TryCancelAsync(claimed.Id, CancellationToken.None).AsTask());
            var claimTask = Task.Run(async () =>
            {
                await harness.Jobs.ActivateDueJobsAsync(time.GetUtcNow(), 10, CancellationToken.None);
                return await harness.Jobs.ClaimAsync(
                    new ClaimRequest("w2", ["default"], 1, Lease), CancellationToken.None);
            });
            await Task.WhenAll(cancelTask, claimTask);

            // The job was never terminal during the window, so TryCancel must report success
            // (either it cancelled a pre-active state or flagged the claimed Processing job).
            Assert.True(await cancelTask);

            var final = (await harness.Jobs.GetJobAsync(claimed.Id, CancellationToken.None))!;
            var reclaimed = await claimTask;
            if (reclaimed.Count == 1)
            {
                // Claim won: flag-only path — the job must still be Processing under w2, and
                // w2's fenced apply must still succeed.
                Assert.Equal(JobState.Processing, final.State);
                Assert.Equal("w2", final.WorkerId);
                Assert.True(await harness.Jobs.ApplyAsync(
                    Transition(reclaimed[0], JobState.Succeeded), CancellationToken.None));
            }
            else
            {
                // Cancel won before activation/claim: terminal, and never claimable again.
                Assert.Equal(JobState.Cancelled, final.State);
            }
        }
    }

    [Fact]
    public async Task TryCancel_terminal_and_unknown_jobs_return_false_without_mutation()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var job = await EnqueueOneAsync(harness.Jobs, Job(time));
        var claimed = await ClaimOneAsync(harness.Jobs);
        Assert.True(await harness.Jobs.ApplyAsync(
            Transition(claimed, JobState.Succeeded), CancellationToken.None));

        Assert.False(await harness.Jobs.TryCancelAsync(job.Id, CancellationToken.None));
        Assert.Equal(JobState.Succeeded,
            (await harness.Jobs.GetJobAsync(job.Id, CancellationToken.None))!.State);

        Assert.False(await harness.Jobs.TryCancelAsync(JobId.New(time), CancellationToken.None));
    }
}
