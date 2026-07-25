using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Millrace.Storage;
using Millrace.Workflows;
using Xunit;

namespace Millrace.Tests.Workflows;

/// <summary>
/// Typed signals, bookmarks and wait timeouts (#37, §6.3).
/// </summary>
public sealed class WorkflowSignalTests
{
    public sealed class Approval
    {
        public string OrderId { get; set; } = "order-1";
        public bool? Approved { get; set; }
        public Dictionary<string, bool> Done { get; set; } = [];
    }

    public sealed record Decision(bool IsApproved);

    public sealed class Submit : IActivity<Approval>
    {
        public Task ExecuteAsync(ActivityContext<Approval> context, CancellationToken ct)
        {
            context.Data.Done["submitted"] = true;
            return Task.CompletedTask;
        }
    }

    public sealed class Finish : IActivity<Approval>
    {
        public Task ExecuteAsync(ActivityContext<Approval> context, CancellationToken ct)
        {
            context.Data.Done["finished"] = true;
            return Task.CompletedTask;
        }
    }

    private sealed class NeedsApproval : IWorkflow<Approval>
    {
        public string Id => "needs-approval";
        public int Version => 1;
        public void Build(IWorkflowBuilder<Approval> flow) => flow
            .StartWith<Submit>()
            .WaitForSignal<Decision>(
                "approval",
                d => d.OrderId,
                (d, signal) => d.Approved = signal.IsApproved)
            .Then<Finish>();
    }

    private sealed class ApprovalWithTimeout : IWorkflow<Approval>
    {
        public string Id => "approval-timeout";
        public int Version => 1;
        public void Build(IWorkflowBuilder<Approval> flow) => flow
            .StartWith<Submit>()
            .WaitForSignal<Decision>(
                "approval",
                d => d.OrderId,
                (d, signal) => d.Approved = signal.IsApproved,
                timeout: TimeSpan.FromSeconds(2))
            .Then<Finish>();
    }

    private static IHost BuildHost<TWorkflow>(FakeTimeProvider time)
        where TWorkflow : class, new()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddMillrace(m => m
            .UseInMemoryStorage()
            .AddWorkflow<TWorkflow>()
            .Configure(o =>
            {
                o.MinPollDelay = TimeSpan.FromMilliseconds(5);
                o.MaxPollDelay = TimeSpan.FromMilliseconds(20);
                o.SchedulerInterval = TimeSpan.FromMilliseconds(5);
            }));

        return builder.Build();
    }

    private static async Task<WorkflowInstanceRecord> WaitForStateAsync(
        IHost host, FakeTimeProvider time, WorkflowInstanceId id, WorkflowInstanceState state,
        TimeSpan? advanceEach = null)
    {
        var storage = host.Services.GetRequiredService<IWorkflowStorage>();
        var deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            var instance = await storage.GetInstanceAsync(id, CancellationToken.None);
            if (instance?.State == state)
            {
                return instance;
            }

            time.Advance(advanceEach ?? TimeSpan.FromMilliseconds(200));
            await Task.Delay(15);
        }

        var last = await storage.GetInstanceAsync(id, CancellationToken.None);

        // Surface why: a workflow that will not advance almost always has a failed job behind it,
        // and the instance alone never says so.
        var monitoring = host.Services.GetRequiredService<Millrace.Storage.Monitoring.IMonitoringStorage>();
        var jobs = await monitoring.QueryJobsAsync(
            new Millrace.Storage.Monitoring.JobQuery(), CancellationToken.None);
        var failures = new List<string>();
        foreach (var job in jobs.Items)
        {
            var details = await monitoring.GetJobDetailsAsync(job.Id, CancellationToken.None);
            failures.Add($"{job.MethodName}@{job.State}: {details?.LastError?.Split('\n')[0]}");
        }

        throw new TimeoutException(
            $"Expected {state}; was {last?.State}. Cursor={last?.CursorJson}{Environment.NewLine}"
            + string.Join(Environment.NewLine, failures));
    }

    private static FakeTimeProvider NewTime() => new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task A_wait_suspends_the_instance_and_holds_no_job()
    {
        var time = NewTime();
        using var host = BuildHost<NeedsApproval>(time);
        await host.StartAsync();

        var id = await host.Services.GetRequiredService<IWorkflowClient>()
            .StartAsync("needs-approval", new Approval());

        await WaitForStateAsync(host, time, id, WorkflowInstanceState.Suspended);

        // §6.3: while a bookmark exists no job does — a workflow parked for a month costs a row.
        var monitoring = host.Services.GetRequiredService<Millrace.Storage.Monitoring.IMonitoringStorage>();
        var stats = await monitoring.GetStatisticsAsync(
            Millrace.Storage.Monitoring.TenantFilter.Any, CancellationToken.None);

        Assert.Equal(0, stats.JobsByState[JobState.Enqueued]);
        Assert.Equal(0, stats.JobsByState[JobState.Scheduled]);
    }

    [Fact]
    public async Task A_typed_signal_resumes_the_wait_and_binds_its_payload()
    {
        var time = NewTime();
        using var host = BuildHost<NeedsApproval>(time);
        await host.StartAsync();

        var client = host.Services.GetRequiredService<IWorkflowClient>();
        var id = await client.StartAsync("needs-approval", new Approval());
        await WaitForStateAsync(host, time, id, WorkflowInstanceState.Suspended);

        var delivered = await client.SignalAsync("approval", "order-1", new Decision(true));
        Assert.True(delivered);

        await WaitForStateAsync(host, time, id, WorkflowInstanceState.Completed);
        var data = await client.GetDataAsync<Approval>(id);

        Assert.True(data!.Approved);
        Assert.True(data.Done.GetValueOrDefault("finished"));
    }

    [Fact]
    public async Task Signalling_something_nobody_waits_for_reports_false()
    {
        var time = NewTime();
        using var host = BuildHost<NeedsApproval>(time);
        await host.StartAsync();

        var client = host.Services.GetRequiredService<IWorkflowClient>();

        Assert.False(await client.SignalAsync("approval", "no-such-order", new Decision(true)));
    }

    [Fact]
    public async Task A_second_signal_for_the_same_wait_is_refused()
    {
        var time = NewTime();
        using var host = BuildHost<NeedsApproval>(time);
        await host.StartAsync();

        var client = host.Services.GetRequiredService<IWorkflowClient>();
        var id = await client.StartAsync("needs-approval", new Approval());
        await WaitForStateAsync(host, time, id, WorkflowInstanceState.Suspended);

        Assert.True(await client.SignalAsync("approval", "order-1", new Decision(true)));
        // At-most-once: the bookmark is consumed, so a duplicate delivery cannot resume it twice.
        Assert.False(await client.SignalAsync("approval", "order-1", new Decision(false)));

        await WaitForStateAsync(host, time, id, WorkflowInstanceState.Completed);
        var data = await client.GetDataAsync<Approval>(id);

        // The first payload is the one that bound.
        Assert.True(data!.Approved);
    }

    [Fact]
    public async Task A_raw_json_payload_binds_the_same_way()
    {
        var time = NewTime();
        using var host = BuildHost<NeedsApproval>(time);
        await host.StartAsync();

        var client = host.Services.GetRequiredService<IWorkflowClient>();
        var id = await client.StartAsync("needs-approval", new Approval());
        await WaitForStateAsync(host, time, id, WorkflowInstanceState.Suspended);

        // The escape hatch for webhooks and senders outside this process.
        Assert.True(await client.SignalAsync("approval", "order-1", """{"IsApproved":true}"""));

        await WaitForStateAsync(host, time, id, WorkflowInstanceState.Completed);
        Assert.True((await client.GetDataAsync<Approval>(id))!.Approved);
    }

    [Fact]
    public async Task A_wait_that_times_out_continues_without_a_payload()
    {
        var time = NewTime();
        using var host = BuildHost<ApprovalWithTimeout>(time);
        await host.StartAsync();

        var client = host.Services.GetRequiredService<IWorkflowClient>();
        var id = await client.StartAsync("approval-timeout", new Approval());
        await WaitForStateAsync(host, time, id, WorkflowInstanceState.Suspended);

        // The timeout is a scheduled job, so it survives restarts and the fake clock releases it.
        // Advanced once rather than per poll: repeatedly jumping a fake clock by days makes every
        // background poller replay the whole interval, which is pathologically slow and tests
        // nothing.
        time.Advance(TimeSpan.FromSeconds(3));
        await WaitForStateAsync(host, time, id, WorkflowInstanceState.Completed);

        var data = await client.GetDataAsync<Approval>(id);

        // The flow continued, but nothing bound a decision.
        Assert.Null(data!.Approved);
        Assert.True(data.Done.GetValueOrDefault("finished"));
    }

    [Fact]
    public async Task A_signal_arriving_before_the_timeout_wins_the_race()
    {
        var time = NewTime();
        using var host = BuildHost<ApprovalWithTimeout>(time);
        await host.StartAsync();

        var client = host.Services.GetRequiredService<IWorkflowClient>();
        var id = await client.StartAsync("approval-timeout", new Approval());
        await WaitForStateAsync(host, time, id, WorkflowInstanceState.Suspended);

        Assert.True(await client.SignalAsync("approval", "order-1", new Decision(true)));
        await WaitForStateAsync(host, time, id, WorkflowInstanceState.Completed);

        // Now let the timeout come due. It must find the bookmark already consumed and do nothing —
        // the flow must not advance a second time.
        time.Advance(TimeSpan.FromSeconds(3));
        await Task.Delay(300);

        var data = await client.GetDataAsync<Approval>(id);
        var instance = await host.Services.GetRequiredService<IWorkflowStorage>()
            .GetInstanceAsync(id, CancellationToken.None);

        Assert.True(data!.Approved);
        Assert.Equal(WorkflowInstanceState.Completed, instance!.State);
    }
}
