using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weft.Invocations;
using Weft.Storage;
using Weft.Storage.InMemory;
using Xunit;

namespace Weft.Tests.Hosting;

public sealed class Recorder
{
    private readonly ConcurrentQueue<string> _events = new();

    public void Add(string entry) => _events.Enqueue(entry);

    public IReadOnlyList<string> Events => [.. _events];

    public int Count(string entry) => _events.Count(e => e == entry);
}

public interface IE2EJobs
{
    Task SucceedAsync(string tag);

    Task FailAsync(string tag);

    Task SlowAsync(string tag, int milliseconds, CancellationToken ct);
}

public sealed class E2EJobs(Recorder recorder) : IE2EJobs
{
    public Task SucceedAsync(string tag)
    {
        recorder.Add($"ok:{tag}");
        return Task.CompletedTask;
    }

    public Task FailAsync(string tag)
    {
        recorder.Add($"fail:{tag}");
        throw new InvalidOperationException($"deliberate failure ({tag})");
    }

    public async Task SlowAsync(string tag, int milliseconds, CancellationToken ct)
    {
        recorder.Add($"slow-start:{tag}");
        await Task.Delay(milliseconds, ct);
        recorder.Add($"slow-end:{tag}");
    }
}

/// <summary>
/// Whole-host tests with real time and aggressively short intervals: enqueue-to-execution,
/// retry-to-dead, continuations, delayed activation, recurring fires, cooperative cancellation,
/// and the two-phase shutdown. Storage-level semantics are covered deterministically by the
/// conformance kit; these prove the worker/scheduler loops drive them end to end.
/// </summary>
public class WorkerEndToEndTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private static IHost BuildHost(Action<WeftOptions>? tune = null)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddLogging();
        builder.Services.AddSingleton<Recorder>();
        builder.Services.AddTransient<IE2EJobs, E2EJobs>();
        builder.Services.AddWeft(w => w
            .UseInMemoryStorage()
            .Configure(o =>
            {
                o.SchedulerInterval = TimeSpan.FromMilliseconds(20);
                o.MinPollDelay = TimeSpan.FromMilliseconds(10);
                o.MaxPollDelay = TimeSpan.FromMilliseconds(50);
                o.HeartbeatInterval = TimeSpan.FromMilliseconds(100);
                o.LeaseDuration = TimeSpan.FromSeconds(30);
                tune?.Invoke(o);
            }));
        return builder.Build();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string description)
    {
        var deadline = DateTimeOffset.UtcNow + WaitTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting for: {description}");
    }

    private static async Task<JobState> StateOf(IHost host, JobId id)
    {
        var storage = host.Services.GetRequiredService<InMemoryStorage>();
        return (await storage.GetJobAsync(id, CancellationToken.None))!.State;
    }

    [Fact]
    public async Task Enqueued_job_executes_and_succeeds()
    {
        using var host = BuildHost();
        await host.StartAsync();
        var client = host.Services.GetRequiredService<IJobClient>();

        var id = await client.EnqueueAsync<IE2EJobs>(j => j.SucceedAsync("basic"));

        await WaitUntilAsync(async () => await StateOf(host, id) == JobState.Succeeded, "job success");
        Assert.Equal(1, host.Services.GetRequiredService<Recorder>().Count("ok:basic"));
        await host.StopAsync();
    }

    [Fact]
    public async Task Failing_job_retries_then_dead_letters_with_failure_count()
    {
        using var host = BuildHost();
        await host.StartAsync();
        var client = host.Services.GetRequiredService<IJobClient>();

        var id = await client.EnqueueAsync<IE2EJobs>(j => j.FailAsync("boom"),
            new EnqueueOptions { Retry = Retry.Fixed(TimeSpan.FromMilliseconds(30), maxAttempts: 3) });

        await WaitUntilAsync(async () => await StateOf(host, id) == JobState.Dead, "dead-letter");
        var storage = host.Services.GetRequiredService<InMemoryStorage>();
        var job = (await storage.GetJobAsync(id, CancellationToken.None))!;
        Assert.Equal(3, job.Failures);
        Assert.Contains("deliberate failure", job.LastError);
        Assert.Equal(3, host.Services.GetRequiredService<Recorder>().Count("fail:boom"));
        await host.StopAsync();
    }

    [Fact]
    public async Task Continuation_runs_after_parent_succeeds()
    {
        using var host = BuildHost();
        await host.StartAsync();
        var client = host.Services.GetRequiredService<IJobClient>();

        var parentId = await client.EnqueueAsync<IE2EJobs>(j => j.SucceedAsync("parent"));
        var childId = await client.ContinueWithAsync<IE2EJobs>(parentId, j => j.SucceedAsync("child"));

        await WaitUntilAsync(async () => await StateOf(host, childId) == JobState.Succeeded, "continuation");
        var events = host.Services.GetRequiredService<Recorder>().Events.ToList();
        Assert.True(events.IndexOf("ok:parent") < events.IndexOf("ok:child"),
            $"parent must run before child; got [{string.Join(", ", events)}]");
        await host.StopAsync();
    }

    [Fact]
    public async Task Continuation_is_cancelled_when_parent_dies()
    {
        using var host = BuildHost();
        await host.StartAsync();
        var client = host.Services.GetRequiredService<IJobClient>();

        var parentId = await client.EnqueueAsync<IE2EJobs>(j => j.FailAsync("fatal"),
            new EnqueueOptions { Retry = Retry.None });
        var childId = await client.ContinueWithAsync<IE2EJobs>(parentId, j => j.SucceedAsync("never"));

        await WaitUntilAsync(async () => await StateOf(host, childId) == JobState.Cancelled, "cascade cancel");
        Assert.Equal(0, host.Services.GetRequiredService<Recorder>().Count("ok:never"));
        await host.StopAsync();
    }

    [Fact]
    public async Task Scheduled_job_executes_after_its_due_time()
    {
        using var host = BuildHost();
        await host.StartAsync();
        var client = host.Services.GetRequiredService<IJobClient>();

        var id = await client.ScheduleAsync<IE2EJobs>(
            j => j.SucceedAsync("later"), TimeSpan.FromMilliseconds(500));
        var storage = host.Services.GetRequiredService<InMemoryStorage>();
        var dueAt = (await storage.GetJobAsync(id, CancellationToken.None))!.DueAt!.Value;

        await WaitUntilAsync(async () => await StateOf(host, id) == JobState.Succeeded, "delayed execution");

        // Deterministic under any CPU contention: activation can only happen at/after DueAt,
        // so completion must never precede it.
        var finishedAt = (await storage.GetJobAsync(id, CancellationToken.None))!.FinishedAt!.Value;
        Assert.True(finishedAt >= dueAt, $"finished {finishedAt:O} before due {dueAt:O}");
        await host.StopAsync();
    }

    [Fact]
    public async Task Heartbeat_keeps_a_job_alive_past_multiple_lease_expiries()
    {
        using var host = BuildHost(o =>
        {
            o.LeaseDuration = TimeSpan.FromSeconds(1);
            o.HeartbeatInterval = TimeSpan.FromMilliseconds(200);
        });
        await host.StartAsync();
        var client = host.Services.GetRequiredService<IJobClient>();
        var recorder = host.Services.GetRequiredService<Recorder>();

        // 2.5s of work against a 1s lease: without heartbeat renewal the worker would reclaim
        // its own expired lease and start a second attempt.
        var id = await client.EnqueueAsync<IE2EJobs>(j => j.SlowAsync("marathon", 2_500, CancellationToken.None));

        await WaitUntilAsync(async () => await StateOf(host, id) == JobState.Succeeded, "long job success");
        Assert.Equal(1, recorder.Count("slow-start:marathon"));
        Assert.Equal(1, recorder.Count("slow-end:marathon"));
        var job = (await host.Services.GetRequiredService<InMemoryStorage>()
            .GetJobAsync(id, CancellationToken.None))!;
        Assert.Equal(1, job.Attempt);
        await host.StopAsync();
    }

    [Fact]
    public async Task Interrupted_job_exceeding_the_interruption_limit_is_poison_pilled_without_executing()
    {
        using var host = BuildHost(o => o.InterruptionLimit = 1);
        var client = host.Services.GetRequiredService<IJobClient>();
        var storage = host.Services.GetRequiredService<InMemoryStorage>();
        var recorder = host.Services.GetRequiredService<Recorder>();

        // Before the workers start: a ghost worker claims the job with a 1ms lease and
        // vanishes, so the real worker's claim is attempt 2 with zero recorded failures —
        // past InterruptionLimit = 1.
        var id = await client.EnqueueAsync<IE2EJobs>(j => j.SucceedAsync("poison"));
        var ghostClaim = await storage.ClaimAsync(
            new ClaimRequest("ghost", ["default"], 1, TimeSpan.FromMilliseconds(1)), CancellationToken.None);
        Assert.Single(ghostClaim);
        await Task.Delay(50); // let the ghost's lease expire

        await host.StartAsync();

        await WaitUntilAsync(async () => await StateOf(host, id) == JobState.Dead, "poison-pill dead-letter");
        var job = (await storage.GetJobAsync(id, CancellationToken.None))!;
        Assert.Contains("Poison-pill", job.LastError);
        Assert.Equal(0, recorder.Count("ok:poison"));
        await host.StopAsync();
    }

    [Fact]
    public async Task Due_recurring_definition_fires_and_advances()
    {
        using var host = BuildHost();
        await host.StartAsync();
        var storage = host.Services.GetRequiredService<InMemoryStorage>();
        var options = new WeftOptions();

        // The client always schedules the next *future* occurrence (cron floor is one minute),
        // so seed a due definition directly at the storage layer.
        var invocation = InvocationCapture.Capture<IE2EJobs>(
            j => j.SucceedAsync("cron"), options.SerializerOptions);
        var now = DateTimeOffset.UtcNow;
        await storage.UpsertRecurringAsync(new RecurringJobRecord
        {
            Id = "tick",
            Cron = "* * * * *",
            Queue = "default",
            Invocation = invocation,
            Retry = Retry.None,
            NextFireTime = now - TimeSpan.FromSeconds(1),
            CreatedAt = now,
            UpdatedAt = now,
        }, CancellationToken.None);

        await WaitUntilAsync(
            () => Task.FromResult(host.Services.GetRequiredService<Recorder>().Count("ok:cron") >= 1),
            "recurring fire");

        var record = (await storage.GetRecurringAsync("tick", CancellationToken.None))!;
        Assert.NotNull(record.LastFireTime);
        Assert.True(record.NextFireTime > now, "NextFireTime must advance beyond the fired occurrence");
        await host.StopAsync();
    }

    [Fact]
    public async Task Cancel_request_interrupts_a_running_job()
    {
        using var host = BuildHost();
        await host.StartAsync();
        var client = host.Services.GetRequiredService<IJobClient>();
        var storage = host.Services.GetRequiredService<InMemoryStorage>();
        var recorder = host.Services.GetRequiredService<Recorder>();

        var id = await client.EnqueueAsync<IE2EJobs>(j => j.SlowAsync("victim", 30_000, CancellationToken.None));
        await WaitUntilAsync(
            () => Task.FromResult(recorder.Count("slow-start:victim") == 1), "job started");

        Assert.True(await storage.TryCancelAsync(id, CancellationToken.None));

        await WaitUntilAsync(async () => await StateOf(host, id) == JobState.Cancelled, "cooperative cancel");
        Assert.Equal(0, recorder.Count("slow-end:victim"));
        await host.StopAsync();
    }

    [Fact]
    public async Task Shutdown_drain_lets_short_jobs_finish()
    {
        using var host = BuildHost(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
        await host.StartAsync();
        var client = host.Services.GetRequiredService<IJobClient>();
        var recorder = host.Services.GetRequiredService<Recorder>();

        var id = await client.EnqueueAsync<IE2EJobs>(j => j.SlowAsync("drainee", 300, CancellationToken.None));
        await WaitUntilAsync(
            () => Task.FromResult(recorder.Count("slow-start:drainee") == 1), "job started");

        await host.StopAsync();

        Assert.Equal(1, recorder.Count("slow-end:drainee"));
        Assert.Equal(JobState.Succeeded, await StateOf(host, id));
    }

    [Fact]
    public async Task Shutdown_releases_jobs_that_exceed_the_drain_window()
    {
        using var host = BuildHost(o =>
        {
            o.ShutdownTimeout = TimeSpan.FromMilliseconds(100);
            o.ShutdownGrace = TimeSpan.FromMilliseconds(200);
        });
        await host.StartAsync();
        var client = host.Services.GetRequiredService<IJobClient>();
        var recorder = host.Services.GetRequiredService<Recorder>();

        var id = await client.EnqueueAsync<IE2EJobs>(j => j.SlowAsync("hostage", 60_000, CancellationToken.None));
        await WaitUntilAsync(
            () => Task.FromResult(recorder.Count("slow-start:hostage") == 1), "job started");

        await host.StopAsync();

        // Released, not failed: back to Enqueued with no retry budget consumed.
        var storage = host.Services.GetRequiredService<InMemoryStorage>();
        var job = (await storage.GetJobAsync(id, CancellationToken.None))!;
        Assert.Equal(JobState.Enqueued, job.State);
        Assert.Equal(0, job.Failures);
        Assert.Equal(0, recorder.Count("slow-end:hostage"));
    }
}
