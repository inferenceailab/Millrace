using Xunit;

namespace Millrace.Storage.Verification;

/// <summary>
/// The workflow-checkpoint clause of the atomicity contract (ARCHITECTURE.md §4.2, §6.2).
/// </summary>
/// <remarks>
/// The checkpoint is the unit of exactly-once progress, so what matters is not that it works but
/// that it is <em>indivisible</em> from the transition carrying it. Every fact here is about a
/// partial application that must be impossible: an instance advanced by a job that did not
/// complete, a job completed without its instance advancing, or either surviving a rejection.
/// </remarks>
public abstract partial class JobStorageConformanceSuite
{
    private static WorkflowInstanceRecord CheckpointInstance(TimeProvider time, string data = """{"step":0}""") => new()
    {
        Id = WorkflowInstanceId.New(time),
        DefinitionId = "checkpoint-flow",
        DefinitionVersion = 1,
        State = WorkflowInstanceState.Running,
        DataJson = data,
        Revision = 1,
        CreatedAt = time.GetUtcNow(),
        UpdatedAt = time.GetUtcNow(),
    };

    /// <summary>Claims a single job and returns it as claimed, so a transition can be fenced on it.</summary>
    private static async Task<JobRecord> ClaimOneAsync(IStorageHarness harness, string workerId = "worker-1")
    {
        var claimed = await harness.Jobs.ClaimAsync(
            new ClaimRequest(workerId, ["default"], MaxCount: 1, TimeSpan.FromMinutes(5)), CancellationToken.None);
        return Assert.Single(claimed);
    }

    [Fact]
    public async Task Checkpoint_transition_and_enqueue_all_commit_together()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var instance = CheckpointInstance(time);
        await harness.Workflows.CreateInstanceAsync(instance, CancellationToken.None);

        var activity = Job(time);
        await harness.Jobs.EnqueueAsync([activity], CancellationToken.None);
        var claimed = await ClaimOneAsync(harness);

        var next = Job(time);
        var applied = await harness.Jobs.ApplyAsync(
            new JobTransition
            {
                JobId = claimed.Id,
                ExpectedWorkerId = claimed.WorkerId!,
                ExpectedAttempt = claimed.Attempt,
                TargetState = JobState.Succeeded,
                Failures = 0,
                Enqueue = [next],
                Checkpoint = new WorkflowCheckpoint
                {
                    Instance = instance with { DataJson = """{"step":1}""" },
                    ExpectedRevision = 1,
                },
            },
            CancellationToken.None);

        Assert.True(applied);

        var storedJob = await harness.Jobs.GetJobAsync(claimed.Id, CancellationToken.None);
        var storedNext = await harness.Jobs.GetJobAsync(next.Id, CancellationToken.None);
        var storedInstance = await harness.Workflows.GetInstanceAsync(instance.Id, CancellationToken.None);

        Assert.Equal(JobState.Succeeded, storedJob!.State);
        Assert.NotNull(storedNext);
        Assert.NotNull(storedInstance);
        Assert.Equal(2, storedInstance.Revision);
        JsonAssert.Equal("""{"step":1}""", storedInstance.DataJson);
    }

    [Fact]
    public async Task A_stale_checkpoint_revision_rolls_back_the_whole_transition()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var instance = CheckpointInstance(time);
        await harness.Workflows.CreateInstanceAsync(instance, CancellationToken.None);

        // Something else advanced the instance first — a sibling parallel branch, say.
        await harness.Workflows.UpdateInstanceAsync(
            instance with { DataJson = """{"step":9}""" }, 1, CancellationToken.None);

        var activity = Job(time);
        await harness.Jobs.EnqueueAsync([activity], CancellationToken.None);
        var claimed = await ClaimOneAsync(harness);
        var next = Job(time);

        await Assert.ThrowsAsync<MillraceConcurrencyException>(async () =>
            await harness.Jobs.ApplyAsync(
                new JobTransition
                {
                    JobId = claimed.Id,
                    ExpectedWorkerId = claimed.WorkerId!,
                    ExpectedAttempt = claimed.Attempt,
                    TargetState = JobState.Succeeded,
                    Failures = 0,
                    Enqueue = [next],
                    Checkpoint = new WorkflowCheckpoint
                    {
                        Instance = instance with { DataJson = """{"step":1}""" },
                        ExpectedRevision = 1, // stale
                    },
                },
                CancellationToken.None));

        // Nothing may have happened: not the state change, not the insert, not the instance.
        var storedJob = await harness.Jobs.GetJobAsync(claimed.Id, CancellationToken.None);
        var storedNext = await harness.Jobs.GetJobAsync(next.Id, CancellationToken.None);
        var storedInstance = await harness.Workflows.GetInstanceAsync(instance.Id, CancellationToken.None);

        Assert.Equal(JobState.Processing, storedJob!.State);
        Assert.Null(storedNext);
        Assert.Equal(2, storedInstance!.Revision);
        JsonAssert.Equal("""{"step":9}""", storedInstance.DataJson);
    }

    [Fact]
    public async Task A_checkpoint_for_a_missing_instance_rolls_back_the_whole_transition()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);

        var activity = Job(time);
        await harness.Jobs.EnqueueAsync([activity], CancellationToken.None);
        var claimed = await ClaimOneAsync(harness);

        await Assert.ThrowsAsync<MillraceConcurrencyException>(async () =>
            await harness.Jobs.ApplyAsync(
                new JobTransition
                {
                    JobId = claimed.Id,
                    ExpectedWorkerId = claimed.WorkerId!,
                    ExpectedAttempt = claimed.Attempt,
                    TargetState = JobState.Succeeded,
                    Failures = 0,
                    Checkpoint = new WorkflowCheckpoint
                    {
                        Instance = CheckpointInstance(time),
                        ExpectedRevision = 1,
                    },
                },
                CancellationToken.None));

        var storedJob = await harness.Jobs.GetJobAsync(claimed.Id, CancellationToken.None);
        Assert.Equal(JobState.Processing, storedJob!.State);
    }

    [Fact]
    public async Task A_fence_rejection_leaves_the_instance_untouched()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var instance = CheckpointInstance(time);
        await harness.Workflows.CreateInstanceAsync(instance, CancellationToken.None);

        var activity = Job(time);
        await harness.Jobs.EnqueueAsync([activity], CancellationToken.None);
        var claimed = await ClaimOneAsync(harness);

        // Wrong attempt: a worker that no longer owns this job must not advance the instance, and
        // must learn that by a false return rather than a concurrency exception.
        var applied = await harness.Jobs.ApplyAsync(
            new JobTransition
            {
                JobId = claimed.Id,
                ExpectedWorkerId = claimed.WorkerId!,
                ExpectedAttempt = claimed.Attempt + 1,
                TargetState = JobState.Succeeded,
                Failures = 0,
                Checkpoint = new WorkflowCheckpoint
                {
                    Instance = instance with { DataJson = """{"step":1}""" },
                    ExpectedRevision = 1,
                },
            },
            CancellationToken.None);

        Assert.False(applied);

        var storedInstance = await harness.Workflows.GetInstanceAsync(instance.Id, CancellationToken.None);
        Assert.Equal(1, storedInstance!.Revision);
        JsonAssert.Equal("""{"step":0}""", storedInstance.DataJson);
    }

    [Fact]
    public async Task Concurrent_branch_checkpoints_have_exactly_one_winner()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var instance = CheckpointInstance(time);
        await harness.Workflows.CreateInstanceAsync(instance, CancellationToken.None);

        // Two parallel branches finishing at once, each holding its own job and the same revision.
        var jobs = new[] { Job(time), Job(time) };
        await harness.Jobs.EnqueueAsync(jobs, CancellationToken.None);
        var first = await ClaimOneAsync(harness, "worker-1");
        var second = await ClaimOneAsync(harness, "worker-2");

        async Task<bool> ApplyAsync(JobRecord claimed, string data)
        {
            try
            {
                return await harness.Jobs.ApplyAsync(
                    new JobTransition
                    {
                        JobId = claimed.Id,
                        ExpectedWorkerId = claimed.WorkerId!,
                        ExpectedAttempt = claimed.Attempt,
                        TargetState = JobState.Succeeded,
                        Failures = 0,
                        Checkpoint = new WorkflowCheckpoint
                        {
                            Instance = instance with { DataJson = data },
                            ExpectedRevision = 1,
                        },
                    },
                    CancellationToken.None);
            }
            catch (MillraceConcurrencyException)
            {
                return false;
            }
        }

        var results = await Task.WhenAll(
            Task.Run(() => ApplyAsync(first, """{"branch":"a"}""")),
            Task.Run(() => ApplyAsync(second, """{"branch":"b"}""")));

        // Exactly one wins; §6.2 has the loser retry the merge, not the activity.
        Assert.Equal(1, results.Count(r => r));

        var storedInstance = await harness.Workflows.GetInstanceAsync(instance.Id, CancellationToken.None);
        Assert.Equal(2, storedInstance!.Revision);

        // And the loser's job stayed claimable rather than silently completing.
        var states = new List<JobState>();
        foreach (var job in jobs)
        {
            states.Add((await harness.Jobs.GetJobAsync(job.Id, CancellationToken.None))!.State);
        }

        Assert.Equal(1, states.Count(s => s == JobState.Succeeded));
        Assert.Equal(1, states.Count(s => s == JobState.Processing));
    }

    [Fact]
    public async Task A_transition_without_a_checkpoint_still_behaves_as_before()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var instance = CheckpointInstance(time);
        await harness.Workflows.CreateInstanceAsync(instance, CancellationToken.None);

        var activity = Job(time);
        await harness.Jobs.EnqueueAsync([activity], CancellationToken.None);
        var claimed = await ClaimOneAsync(harness);

        var applied = await harness.Jobs.ApplyAsync(
            new JobTransition
            {
                JobId = claimed.Id,
                ExpectedWorkerId = claimed.WorkerId!,
                ExpectedAttempt = claimed.Attempt,
                TargetState = JobState.Succeeded,
                Failures = 0,
            },
            CancellationToken.None);

        Assert.True(applied);
        var storedInstance = await harness.Workflows.GetInstanceAsync(instance.Id, CancellationToken.None);
        Assert.Equal(1, storedInstance!.Revision);
    }
}
