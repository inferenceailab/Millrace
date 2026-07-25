using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Millrace.Storage;
using Millrace.Workflows;
using Xunit;

namespace Millrace.Tests.Workflows;

/// <summary>
/// End-to-end execution over the real worker pool and the InMemory provider (#36, #38).
/// </summary>
/// <remarks>
/// Deliberately not unit tests of the dispatcher. The claim being tested is that an activity
/// execution <em>is</em> an ordinary Layer 1 job whose checkpoint commits with its own transition,
/// and that only holds if the real worker claims it, executes it, and applies the transition.
/// </remarks>
public sealed class WorkflowEngineTests
{
    public sealed class Trace
    {
        public List<string> Steps { get; set; } = [];
        public List<string> Items { get; set; } = ["x", "y", "z"];
        public bool TakeBranch { get; set; }
        public int Counter { get; set; }
    }

    public abstract class Step(string name) : IActivity<Trace>
    {
        public Task ExecuteAsync(ActivityContext<Trace> context, CancellationToken ct)
        {
            context.Data.Steps.Add(name);
            return Task.CompletedTask;
        }
    }

    public sealed class First : Step { public First() : base("first") { } }
    public sealed class Second : Step { public Second() : base("second") { } }
    public sealed class OnTrue : Step { public OnTrue() : base("true") { } }
    public sealed class OnFalse : Step { public OnFalse() : base("false") { } }
    public sealed class Last : Step { public Last() : base("last") { } }

    public sealed class BranchA : Step { public BranchA() : base("A") { } }
    public sealed class BranchB : Step { public BranchB() : base("B") { } }

    /// <summary>Records the index it was given, which is how a ForEach body reaches its item.</summary>
    public sealed class PerItem : IActivity<Trace>
    {
        public Task ExecuteAsync(ActivityContext<Trace> context, CancellationToken ct)
        {
            lock (context.Data)
            {
                context.Data.Steps.Add($"item:{context.Data.Items[context.LoopIndex]}");
                context.Data.Counter++;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class Sequential : IWorkflow<Trace>
    {
        public string Id => "sequential";
        public int Version => 1;
        public void Build(IWorkflowBuilder<Trace> flow) => flow.StartWith<First>().Then<Second>();
    }

    private sealed class Branching : IWorkflow<Trace>
    {
        public string Id => "branching";
        public int Version => 1;
        public void Build(IWorkflowBuilder<Trace> flow) => flow
            .StartWith<First>()
            .If(d => d.TakeBranch, t => t.Then<OnTrue>(), f => f.Then<OnFalse>())
            .Then<Last>();
    }

    private sealed class Fanning : IWorkflow<Trace>
    {
        public string Id => "fanning";
        public int Version => 1;
        public void Build(IWorkflowBuilder<Trace> flow) => flow
            .StartWith<First>()
            .Parallel(a => a.Then<BranchA>(), b => b.Then<BranchB>())
            .Then<Last>();
    }

    private sealed class Looping : IWorkflow<Trace>
    {
        public string Id => "looping";
        public int Version => 1;
        public void Build(IWorkflowBuilder<Trace> flow) => flow
            .StartWith<First>()
            .ForEach(d => d.Items, body => body.Then<PerItem>())
            .Then<Last>();
    }

    private sealed class Delaying : IWorkflow<Trace>
    {
        public string Id => "delaying";
        public int Version => 1;
        public void Build(IWorkflowBuilder<Trace> flow) => flow
            .StartWith<First>()
            .Delay(TimeSpan.FromHours(2))
            .Then<Last>();
    }

    private sealed class Failing : IActivity<Trace>
    {
        public Task ExecuteAsync(ActivityContext<Trace> context, CancellationToken ct)
        {
            context.Data.Steps.Add("attempted");
            throw new InvalidOperationException("activity failed");
        }
    }

    private sealed class Fails : IWorkflow<Trace>
    {
        public string Id => "fails";
        public int Version => 1;
        public void Build(IWorkflowBuilder<Trace> flow) => flow.StartWith<First>().Then<Failing>().Then<Last>();
    }

    private static async Task<(IHost Host, FakeTimeProvider Time)> StartHostAsync<TWorkflow>()
        where TWorkflow : class, new()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
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
                // Retries are load-bearing here, not incidental: concurrent branch checkpoints
                // conflict by design, and the loser reaches its merge again through a retry.
                o.DefaultRetry = Retry.Exponential(8);
            }));

        var host = builder.Build();
        await host.StartAsync();
        return (host, time);
    }

    /// <summary>
    /// Waits for the instance to reach a terminal state. The fake clock is advanced as we go, so
    /// scheduled work (delays, retries) comes due without any real waiting.
    /// </summary>
    private static async Task<WorkflowInstanceRecord> WaitForCompletionAsync(
        IHost host, FakeTimeProvider time, WorkflowInstanceId id, TimeSpan? advanceEach = null)
    {
        var storage = host.Services.GetRequiredService<IWorkflowStorage>();
        var deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            var instance = await storage.GetInstanceAsync(id, CancellationToken.None);
            if (instance is not null && instance.State != WorkflowInstanceState.Running)
            {
                return instance;
            }

            // Always advance: retry backoff and delays are scheduled jobs on the fake clock, so
            // without this a conflicted branch would never become due again.
            time.Advance(advanceEach ?? TimeSpan.FromSeconds(5));

            await Task.Delay(15);
        }

        var last = await storage.GetInstanceAsync(id, CancellationToken.None);
        throw new TimeoutException(
            $"Workflow did not finish. State={last?.State}, cursor={last?.CursorJson}, data={last?.DataJson}");
    }

    private static async Task<Trace> DataAsync(IHost host, WorkflowInstanceId id)
        => (await host.Services.GetRequiredService<IWorkflowClient>().GetDataAsync<Trace>(id))!;

    // ---------------------------------------------------------------- #36

    [Fact]
    public async Task A_sequence_runs_its_activities_in_order()
    {
        var (host, time) = await StartHostAsync<Sequential>();
        using var _ = host;

        var id = await host.Services.GetRequiredService<IWorkflowClient>()
            .StartAsync("sequential", new Trace());

        var instance = await WaitForCompletionAsync(host, time, id);
        var data = await DataAsync(host, id);

        Assert.Equal(WorkflowInstanceState.Completed, instance.State);
        Assert.Equal(["first", "second"], data.Steps);
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public async Task A_branch_takes_one_arm_and_rejoins_the_sequence(bool takeBranch, string expected)
    {
        var (host, time) = await StartHostAsync<Branching>();
        using var _ = host;

        var id = await host.Services.GetRequiredService<IWorkflowClient>()
            .StartAsync("branching", new Trace { TakeBranch = takeBranch });

        await WaitForCompletionAsync(host, time, id);
        var data = await DataAsync(host, id);

        // Rejoining matters: the arm ends, and the flow continues after the If rather than stopping.
        Assert.Equal(["first", expected, "last"], data.Steps);
    }

    [Fact]
    public async Task Parallel_branches_both_run_and_the_sequence_resumes_once()
    {
        var (host, time) = await StartHostAsync<Fanning>();
        using var _ = host;

        var id = await host.Services.GetRequiredService<IWorkflowClient>()
            .StartAsync("fanning", new Trace());

        await WaitForCompletionAsync(host, time, id);
        var data = await DataAsync(host, id);

        Assert.Equal("first", data.Steps[0]);
        Assert.Contains("A", data.Steps);
        Assert.Contains("B", data.Steps);
        // The join is the point: "last" runs exactly once, after both branches, never twice.
        Assert.Equal("last", data.Steps[^1]);
        Assert.Single(data.Steps, s => s == "last");
        Assert.Equal(4, data.Steps.Count);
    }

    [Fact]
    public async Task A_foreach_runs_the_body_once_per_item_and_joins()
    {
        var (host, time) = await StartHostAsync<Looping>();
        using var _ = host;

        var id = await host.Services.GetRequiredService<IWorkflowClient>()
            .StartAsync("looping", new Trace());

        await WaitForCompletionAsync(host, time, id);
        var data = await DataAsync(host, id);

        Assert.Equal(3, data.Counter);
        foreach (var item in new[] { "x", "y", "z" })
        {
            Assert.Contains($"item:{item}", data.Steps);
        }

        Assert.Single(data.Steps, s => s == "last");
        Assert.Equal("last", data.Steps[^1]);
    }

    [Fact]
    public async Task An_activity_that_throws_stops_the_flow_without_checkpointing_past_it()
    {
        var (host, time) = await StartHostAsync<Fails>();
        using var _ = host;

        var id = await host.Services.GetRequiredService<IWorkflowClient>()
            .StartAsync("fails", new Trace());

        // Give the substrate time to run the failing activity and dead-letter it.
        await Task.Delay(400);

        var storage = host.Services.GetRequiredService<IWorkflowStorage>();
        var instance = await storage.GetInstanceAsync(id, CancellationToken.None);
        var data = await DataAsync(host, id);

        // A failing activity never checkpoints, so the step after it never runs and the instance
        // stays Running rather than being marked complete.
        Assert.Equal(WorkflowInstanceState.Running, instance!.State);
        Assert.DoesNotContain("last", data.Steps);
        // Its own mutation is discarded too — the retry sees the document it saw the first time.
        Assert.Equal(["first"], data.Steps);
    }

    // ---------------------------------------------------------------- #38

    [Fact]
    public async Task A_delay_defers_the_rest_of_the_flow_until_it_comes_due()
    {
        var (host, time) = await StartHostAsync<Delaying>();
        using var _ = host;

        var id = await host.Services.GetRequiredService<IWorkflowClient>()
            .StartAsync("delaying", new Trace());

        // Before the delay elapses the flow has run only up to it.
        await Task.Delay(200);
        var midway = await DataAsync(host, id);
        Assert.Equal(["first"], midway.Steps);

        // The wait is a scheduled job, not an in-memory timer, so advancing the clock releases it.
        var instance = await WaitForCompletionAsync(host, time, id, advanceEach: TimeSpan.FromMinutes(30));
        var data = await DataAsync(host, id);

        Assert.Equal(WorkflowInstanceState.Completed, instance.State);
        Assert.Equal(["first", "last"], data.Steps);
    }

    // ---------------------------------------------------------------- instance lifecycle

    [Fact]
    public async Task Starting_an_unknown_workflow_fails_loudly()
    {
        var (host, _) = await StartHostAsync<Sequential>();
        using var __ = host;

        var client = host.Services.GetRequiredService<IWorkflowClient>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.StartAsync("nope", new Trace()));

        Assert.Contains("AddWorkflow", ex.Message);
    }

    [Fact]
    public async Task Activity_jobs_carry_their_instance_and_node_for_the_dashboard()
    {
        var (host, time) = await StartHostAsync<Sequential>();
        using var _ = host;

        var id = await host.Services.GetRequiredService<IWorkflowClient>()
            .StartAsync("sequential", new Trace());
        await WaitForCompletionAsync(host, time, id);

        var monitoring = host.Services.GetRequiredService<Millrace.Storage.Monitoring.IMonitoringStorage>();
        var page = await monitoring.QueryJobsAsync(new Millrace.Storage.Monitoring.JobQuery(), CancellationToken.None);

        Assert.NotEmpty(page.Items);
        foreach (var job in page.Items)
        {
            var details = await monitoring.GetJobDetailsAsync(job.Id, CancellationToken.None);
            Assert.Equal(id, details!.WorkflowInstanceId);
            Assert.False(string.IsNullOrEmpty(details.ActivityNodeId));
        }
    }
}
