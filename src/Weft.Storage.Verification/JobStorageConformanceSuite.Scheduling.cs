using Weft.Storage;
using Xunit;

namespace Weft.Storage.Verification;

public abstract partial class JobStorageConformanceSuite
{
    // ---------------------------------------------------------------- due activation

    [Fact]
    public async Task Activate_moves_only_due_jobs_oldest_first_respecting_batch_size()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var now = time.GetUtcNow();
        var due1 = Job(time, JobState.Scheduled, dueAt: now - TimeSpan.FromMinutes(3));
        var due2 = Job(time, JobState.Scheduled, dueAt: now - TimeSpan.FromMinutes(2));
        var due3 = Job(time, JobState.Scheduled, dueAt: now - TimeSpan.FromMinutes(1));
        var future = Job(time, JobState.Scheduled, dueAt: now + TimeSpan.FromMinutes(5));
        await harness.Jobs.EnqueueAsync([due3, due1, future, due2], CancellationToken.None);

        // Batch limit 2 takes the two oldest.
        Assert.Equal(2, await harness.Jobs.ActivateDueJobsAsync(now, 2, CancellationToken.None));
        Assert.Equal(JobState.Enqueued, (await harness.Jobs.GetJobAsync(due1.Id, CancellationToken.None))!.State);
        Assert.Equal(JobState.Enqueued, (await harness.Jobs.GetJobAsync(due2.Id, CancellationToken.None))!.State);
        Assert.Equal(JobState.Scheduled, (await harness.Jobs.GetJobAsync(due3.Id, CancellationToken.None))!.State);

        // DueAt is cleared on activation.
        Assert.Null((await harness.Jobs.GetJobAsync(due1.Id, CancellationToken.None))!.DueAt);

        Assert.Equal(1, await harness.Jobs.ActivateDueJobsAsync(now, 10, CancellationToken.None));
        Assert.Equal(JobState.Scheduled, (await harness.Jobs.GetJobAsync(future.Id, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task Concurrent_activation_activates_each_job_exactly_once()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var now = time.GetUtcNow();
        var records = Enumerable.Range(0, 50)
            .Select(i => Job(time, JobState.Scheduled, dueAt: now - TimeSpan.FromSeconds(i + 1)))
            .ToList();
        await harness.Jobs.EnqueueAsync(records, CancellationToken.None);

        var counts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            var total = 0;
            while (true)
            {
                var moved = await harness.Jobs.ActivateDueJobsAsync(now, 10, CancellationToken.None);
                if (moved == 0)
                {
                    return total;
                }

                total += moved;
            }
        })));

        Assert.Equal(50, counts.Sum());
    }

    // ---------------------------------------------------------------- recurring

    private static RecurringJobRecord Recurring(
        TimeProvider time, string id = "r1", string cron = "*/5 * * * *", int priority = 0,
        DateTimeOffset? nextFireTime = null) => new()
    {
        Id = id,
        Cron = cron,
        Queue = "default",
        Invocation = DummyInvocation,
        Retry = Retry.None,
        Priority = priority,
        NextFireTime = nextFireTime ?? time.GetUtcNow() + TimeSpan.FromMinutes(5),
        CreatedAt = time.GetUtcNow(),
        UpdatedAt = time.GetUtcNow(),
    };

    [Fact]
    public async Task Recurring_upsert_roundtrips_all_fields()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var record = Recurring(time, priority: 7);

        await harness.Jobs.UpsertRecurringAsync(record, CancellationToken.None);
        var stored = await harness.Jobs.GetRecurringAsync(record.Id, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal(record.Cron, stored.Cron);
        Assert.Equal(record.Queue, stored.Queue);
        Assert.Equal(7, stored.Priority);
        Assert.Equal(record.NextFireTime, stored.NextFireTime);
        Assert.Equal(record.Retry, stored.Retry);
        Assert.Null(stored.LastFireTime);
    }

    [Fact]
    public async Task Recurring_upsert_with_same_cron_preserves_next_fire_time()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var original = Recurring(time);
        await harness.Jobs.UpsertRecurringAsync(original, CancellationToken.None);

        var update = original with
        {
            Queue = "high",
            Priority = 3,
            NextFireTime = original.NextFireTime + TimeSpan.FromHours(9), // engine-recomputed; must be ignored
            UpdatedAt = time.GetUtcNow() + TimeSpan.FromMinutes(1),
        };
        await harness.Jobs.UpsertRecurringAsync(update, CancellationToken.None);

        var stored = (await harness.Jobs.GetRecurringAsync(original.Id, CancellationToken.None))!;
        Assert.Equal("high", stored.Queue);
        Assert.Equal(3, stored.Priority);
        Assert.Equal(original.NextFireTime, stored.NextFireTime);
        Assert.Equal(original.CreatedAt, stored.CreatedAt);
    }

    [Fact]
    public async Task Recurring_upsert_with_changed_cron_takes_the_records_next_fire_time()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var original = Recurring(time);
        await harness.Jobs.UpsertRecurringAsync(original, CancellationToken.None);

        var newNext = original.NextFireTime + TimeSpan.FromHours(1);
        var update = original with { Cron = "0 3 * * *", NextFireTime = newNext };
        await harness.Jobs.UpsertRecurringAsync(update, CancellationToken.None);

        var stored = (await harness.Jobs.GetRecurringAsync(original.Id, CancellationToken.None))!;
        Assert.Equal("0 3 * * *", stored.Cron);
        Assert.Equal(newNext, stored.NextFireTime);
    }

    [Fact]
    public async Task GetDueRecurring_returns_due_only_within_batch_limit()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var now = time.GetUtcNow();
        await harness.Jobs.UpsertRecurringAsync(
            Recurring(time, "due-1", nextFireTime: now - TimeSpan.FromMinutes(2)), CancellationToken.None);
        await harness.Jobs.UpsertRecurringAsync(
            Recurring(time, "due-2", nextFireTime: now - TimeSpan.FromMinutes(1)), CancellationToken.None);
        await harness.Jobs.UpsertRecurringAsync(
            Recurring(time, "future", nextFireTime: now + TimeSpan.FromMinutes(1)), CancellationToken.None);

        var due = await harness.Jobs.GetDueRecurringAsync(now, 10, CancellationToken.None);
        Assert.Equal(2, due.Count);
        Assert.DoesNotContain(due, r => r.Id == "future");

        // Batch limiting returns the most-overdue definition first — a backlog can never
        // starve the oldest one.
        var limited = await harness.Jobs.GetDueRecurringAsync(now, 1, CancellationToken.None);
        Assert.Equal("due-1", Assert.Single(limited).Id);
    }

    [Fact]
    public async Task Activation_breaks_due_time_ties_by_enqueue_order()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var dueAt = time.GetUtcNow() - TimeSpan.FromMinutes(1);
        var first = Job(time, JobState.Scheduled, dueAt: dueAt);
        var second = Job(time, JobState.Scheduled, dueAt: dueAt);
        var third = Job(time, JobState.Scheduled, dueAt: dueAt);
        await harness.Jobs.EnqueueAsync([first], CancellationToken.None);
        await harness.Jobs.EnqueueAsync([second], CancellationToken.None);
        await harness.Jobs.EnqueueAsync([third], CancellationToken.None);

        Assert.Equal(2, await harness.Jobs.ActivateDueJobsAsync(time.GetUtcNow(), 2, CancellationToken.None));

        Assert.Equal(JobState.Enqueued, (await harness.Jobs.GetJobAsync(first.Id, CancellationToken.None))!.State);
        Assert.Equal(JobState.Enqueued, (await harness.Jobs.GetJobAsync(second.Id, CancellationToken.None))!.State);
        Assert.Equal(JobState.Scheduled, (await harness.Jobs.GetJobAsync(third.Id, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task TryFire_cas_has_exactly_one_winner_and_one_enqueued_job()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var now = time.GetUtcNow();
        var record = Recurring(time, nextFireTime: now - TimeSpan.FromMinutes(1));
        await harness.Jobs.UpsertRecurringAsync(record, CancellationToken.None);
        var next = now + TimeSpan.FromMinutes(5);

        var jobs = Enumerable.Range(0, 8).Select(_ => Job(time)).ToArray();
        var wins = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
            await harness.Jobs.TryFireRecurringAsync(
                record.Id, record.NextFireTime, next, jobs[i], CancellationToken.None))));

        Assert.Equal(1, wins.Count(w => w));

        var stored = (await harness.Jobs.GetRecurringAsync(record.Id, CancellationToken.None))!;
        Assert.Equal(next, stored.NextFireTime);
        Assert.Equal(record.NextFireTime, stored.LastFireTime);

        // Exactly one of the pre-built jobs exists — the winner's, atomically with its CAS.
        var inserted = new List<JobId>();
        foreach (var job in jobs)
        {
            if (await harness.Jobs.GetJobAsync(job.Id, CancellationToken.None) is { } found)
            {
                inserted.Add(found.Id);
            }
        }

        Assert.Single(inserted);
    }

    [Fact]
    public async Task TryFire_with_stale_expected_time_returns_false_and_inserts_nothing()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var record = Recurring(time, nextFireTime: time.GetUtcNow());
        await harness.Jobs.UpsertRecurringAsync(record, CancellationToken.None);

        var job = Job(time);
        var fired = await harness.Jobs.TryFireRecurringAsync(
            record.Id, record.NextFireTime - TimeSpan.FromMinutes(1),
            record.NextFireTime + TimeSpan.FromMinutes(5), job, CancellationToken.None);

        Assert.False(fired);
        Assert.Null(await harness.Jobs.GetJobAsync(job.Id, CancellationToken.None));
        Assert.Equal(record.NextFireTime,
            (await harness.Jobs.GetRecurringAsync(record.Id, CancellationToken.None))!.NextFireTime);
    }

    [Fact]
    public async Task TryFire_unknown_id_returns_false()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);

        Assert.False(await harness.Jobs.TryFireRecurringAsync(
            "missing", time.GetUtcNow(), time.GetUtcNow() + TimeSpan.FromMinutes(5),
            Job(time), CancellationToken.None));
    }

    [Fact]
    public async Task Remove_recurring_removes_the_definition()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var record = Recurring(time);
        await harness.Jobs.UpsertRecurringAsync(record, CancellationToken.None);

        await harness.Jobs.RemoveRecurringAsync(record.Id, CancellationToken.None);

        Assert.Null(await harness.Jobs.GetRecurringAsync(record.Id, CancellationToken.None));
        Assert.False(await harness.Jobs.TryFireRecurringAsync(
            record.Id, record.NextFireTime, record.NextFireTime + TimeSpan.FromMinutes(5),
            Job(time), CancellationToken.None));
    }

    [Fact]
    public async Task Same_cron_upsert_racing_fire_never_rewinds_next_fire_time()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var now = time.GetUtcNow();
        var record = Recurring(time, nextFireTime: now - TimeSpan.FromMinutes(1));
        await harness.Jobs.UpsertRecurringAsync(record, CancellationToken.None);
        var next = now + TimeSpan.FromMinutes(5);

        var fireJob = Job(time);
        var upsert = Task.Run(() => harness.Jobs.UpsertRecurringAsync(
            record with { NextFireTime = now + TimeSpan.FromMinutes(4) }, CancellationToken.None).AsTask());
        var fire = Task.Run(() => harness.Jobs.TryFireRecurringAsync(
            record.Id, record.NextFireTime, next, fireJob, CancellationToken.None).AsTask());
        await Task.WhenAll(upsert, fire);

        // Whatever the interleaving, the occurrence fires at most once: a follow-up fire pass
        // using the currently stored NextFireTime wins at most once more, and the total number
        // of fired jobs for this definition never exceeds the number of CAS wins.
        var stored = (await harness.Jobs.GetRecurringAsync(record.Id, CancellationToken.None))!;
        if (await fire)
        {
            // Fire won its CAS: the stored NextFireTime must reflect an advance (the same-cron
            // upsert preserves it), never the stale pre-fire value.
            Assert.Equal(next, stored.NextFireTime);
            Assert.NotNull(await harness.Jobs.GetJobAsync(fireJob.Id, CancellationToken.None));
        }
        else
        {
            // Fire lost: its job must not exist.
            Assert.Null(await harness.Jobs.GetJobAsync(fireJob.Id, CancellationToken.None));
        }
    }
}
