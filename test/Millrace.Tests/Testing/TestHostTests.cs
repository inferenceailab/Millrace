using Microsoft.Extensions.DependencyInjection;
using Millrace.Storage;
using Millrace.Testing;
using Millrace.Workflows;
using Xunit;

namespace Millrace.Tests.Testing;

/// <summary>
/// The consumer test harness (#44).
/// </summary>
/// <remarks>
/// Every test in this file is written the way a consumer would write one: enqueue, run, assert.
/// No polling loop, no <c>Task.Delay</c>, no deadline. That absence is the feature — the suite
/// elsewhere in this repository is full of the pattern this exists to remove.
/// </remarks>
public sealed class TestHostTests
{
    public interface IOrders
    {
        Task ConfirmAsync(string id);

        Task FailAsync(string id);
    }

    private sealed class Orders(Recorder recorder) : IOrders
    {
        public Task ConfirmAsync(string id)
        {
            recorder.Add($"confirmed:{id}");
            return Task.CompletedTask;
        }

        public Task FailAsync(string id)
        {
            recorder.Add($"attempted:{id}");
            throw new InvalidOperationException("declined");
        }
    }

    public sealed class Recorder
    {
        private readonly List<string> _entries = [];

        public IReadOnlyList<string> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public void Add(string entry)
        {
            lock (_entries)
            {
                _entries.Add(entry);
            }
        }
    }

    private static MillraceTestHost Create(Action<MillraceBuilder>? millrace = null)
        => MillraceTestHost.Create(
            services =>
            {
                services.AddSingleton<Recorder>();
                services.AddScoped<IOrders, Orders>();
            },
            millrace);

    [Fact]
    public async Task A_job_runs_when_the_test_says_so()
    {
        await using var host = Create();

        await host.Jobs.EnqueueAsync<IOrders>(o => o.ConfirmAsync("A1"));
        var executed = await host.RunUntilIdleAsync();

        Assert.Equal(1, executed);
        Assert.Equal(["confirmed:A1"], host.Services.GetRequiredService<Recorder>().Entries);
    }

    [Fact]
    public async Task Nothing_runs_until_it_is_asked_to()
    {
        await using var host = Create();

        await host.Jobs.EnqueueAsync<IOrders>(o => o.ConfirmAsync("A1"));

        // The determinism the harness exists for: an enqueue is not a race.
        Assert.Empty(host.Services.GetRequiredService<Recorder>().Entries);
    }

    [Fact]
    public async Task A_delayed_job_waits_for_the_clock_not_for_the_test()
    {
        await using var host = Create();

        await host.Jobs.EnqueueAsync<IOrders>(o => o.ConfirmAsync("A1"));
        await host.Jobs.ScheduleAsync<IOrders>(o => o.ConfirmAsync("later"), TimeSpan.FromDays(7));

        await host.RunUntilIdleAsync();
        Assert.Equal(["confirmed:A1"], host.Services.GetRequiredService<Recorder>().Entries);

        // Seven days in one call, with no waiting and no flakiness.
        await host.AdvanceTime(TimeSpan.FromDays(8));
        await host.RunUntilIdleAsync();

        Assert.Equal(["confirmed:A1", "confirmed:later"], host.Services.GetRequiredService<Recorder>().Entries);
    }

    [Fact]
    public async Task Continuations_run_in_the_same_drain()
    {
        await using var host = Create();

        var parent = await host.Jobs.EnqueueAsync<IOrders>(o => o.ConfirmAsync("parent"));
        await host.Jobs.ContinueWithAsync<IOrders>(parent, o => o.ConfirmAsync("child"));

        await host.RunUntilIdleAsync();

        // Draining follows work that running the queue creates, so a test does not have to know
        // how many rounds a chain takes.
        Assert.Equal(["confirmed:parent", "confirmed:child"], host.Services.GetRequiredService<Recorder>().Entries);
    }

    [Fact]
    public async Task A_failing_job_fails_the_test_by_default()
    {
        await using var host = Create();

        await host.Jobs.EnqueueAsync<IOrders>(
            o => o.FailAsync("A1"), new EnqueueOptions { Retry = Retry.None });

        // Named, not an unexplained assertion failure three lines later.
        var ex = await Assert.ThrowsAsync<MillraceJobFailedException>(async () => await host.RunUntilIdleAsync());

        Assert.Contains("declined", ex.Message);
        Assert.Equal("FailAsync", ex.Job.Invocation.MethodName);
    }

    [Fact]
    public async Task A_failure_can_be_the_thing_under_test()
    {
        await using var host = Create();

        var id = await host.Jobs.EnqueueAsync<IOrders>(
            o => o.FailAsync("A1"), new EnqueueOptions { Retry = Retry.None });

        await host.RunUntilIdleAsync(throwOnFailure: false);

        var job = await host.GetJobAsync(id);
        Assert.Equal(JobState.Dead, job!.State);
        Assert.Contains("declined", job.LastError);
    }

    [Fact]
    public async Task Retry_backoff_is_reached_by_advancing_the_clock()
    {
        await using var host = Create();

        await host.Jobs.EnqueueAsync<IOrders>(
            o => o.FailAsync("A1"), new EnqueueOptions { Retry = Retry.Exponential(3) });

        await host.RunUntilIdleAsync(throwOnFailure: false);
        Assert.Single(host.Services.GetRequiredService<Recorder>().Entries);

        // The retry is a scheduled row, so testing backoff is advancing time rather than sleeping.
        await host.AdvanceTime(TimeSpan.FromHours(1));
        await host.RunUntilIdleAsync(throwOnFailure: false);

        Assert.Equal(2, host.Services.GetRequiredService<Recorder>().Entries.Count);
    }

    // ---------------------------------------------------------------- workflows

    public sealed class Onboarding
    {
        public string CustomerId { get; set; } = "c1";

        public bool Approved { get; set; }

        public Dictionary<string, bool> Done { get; set; } = [];
    }

    public sealed record Decision(bool IsApproved);

    public sealed class Register(Recorder recorder) : IActivity<Onboarding>
    {
        public Task ExecuteAsync(ActivityContext<Onboarding> context, CancellationToken ct)
        {
            context.Data.Done["registered"] = true;
            recorder.Add("registered");
            return Task.CompletedTask;
        }
    }

    public sealed class Welcome(Recorder recorder) : IActivity<Onboarding>
    {
        public Task ExecuteAsync(ActivityContext<Onboarding> context, CancellationToken ct)
        {
            context.Data.Done["welcomed"] = true;
            recorder.Add($"welcomed:{context.Data.Approved}");
            return Task.CompletedTask;
        }
    }

    private sealed class OnboardingFlow : IWorkflow<Onboarding>
    {
        public string Id => "onboarding";
        public int Version => 1;
        public void Build(IWorkflowBuilder<Onboarding> flow) => flow
            .StartWith<Register>()
            .WaitForSignal<Decision>(
                "approval", d => d.CustomerId, (d, s) => d.Approved = s.IsApproved,
                timeout: TimeSpan.FromDays(3))
            .Then<Welcome>();
    }

    [Fact]
    public async Task A_workflow_suspends_and_resumes_on_a_signal()
    {
        await using var host = Create(m => m.AddWorkflow<OnboardingFlow>());

        var id = await host.Workflows.StartAsync("onboarding", new Onboarding());
        await host.RunUntilIdleAsync();

        Assert.Equal(WorkflowInstanceState.Suspended, await host.GetInstanceStateAsync(id));

        Assert.True(await host.Workflows.SignalAsync("approval", "c1", new Decision(true)));
        await host.RunUntilIdleAsync();

        Assert.Equal(WorkflowInstanceState.Completed, await host.GetInstanceStateAsync(id));
        var data = await host.GetDataAsync<Onboarding>(id);
        Assert.True(data!.Approved);
        Assert.True(data.Done.GetValueOrDefault("welcomed"));
    }

    [Fact]
    public async Task A_signal_timeout_is_one_call_rather_than_three_days()
    {
        await using var host = Create(m => m.AddWorkflow<OnboardingFlow>());

        var id = await host.Workflows.StartAsync("onboarding", new Onboarding());
        await host.RunUntilIdleAsync();
        Assert.Equal(WorkflowInstanceState.Suspended, await host.GetInstanceStateAsync(id));

        await host.AdvanceTime(TimeSpan.FromDays(4));
        await host.RunUntilIdleAsync();

        Assert.Equal(WorkflowInstanceState.Completed, await host.GetInstanceStateAsync(id));
        // Nothing bound a decision, and the flow continued anyway.
        Assert.False((await host.GetDataAsync<Onboarding>(id))!.Approved);
    }

    [Fact]
    public async Task A_runaway_job_fails_the_test_instead_of_hanging_it()
    {
        await using var host = MillraceTestHost.Create(services => services.AddScoped<IRunaway, Runaway>());

        await host.Jobs.EnqueueAsync<IRunaway>(r => r.GoAsync());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await host.RunUntilIdleAsync());
        Assert.Contains("enqueueing itself in a loop", ex.Message);
    }

    public interface IRunaway
    {
        Task GoAsync();
    }

    private sealed class Runaway(IJobClient jobs) : IRunaway
    {
        public async Task GoAsync() => await jobs.EnqueueAsync<IRunaway>(r => r.GoAsync());
    }
}
