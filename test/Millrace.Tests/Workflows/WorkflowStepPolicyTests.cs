using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Millrace.Storage;
using Millrace.Workflows;
using Xunit;

namespace Millrace.Tests.Workflows;

/// <summary>
/// Per-step failure policies (#78, §6.4, §11.28).
/// </summary>
/// <remarks>
/// The claim under test is that a step can opt out of the saga's unwind. Compensating is the right
/// default and the wrong reflex for two cases: a step whose earlier work should <em>stand</em>, and
/// one whose failure needs a human before anything else moves. Both are asserted by what does
/// <b>not</b> happen — nothing is undone — which is why each test checks the compensations too.
/// </remarks>
public sealed class WorkflowStepPolicyTests
{
    public sealed class Order
    {
        public Dictionary<string, bool> Done { get; set; } = [];
        public List<string> Undone { get; set; } = [];
    }

    public sealed class Reserve : IActivity<Order>
    {
        public Task ExecuteAsync(ActivityContext<Order> c, CancellationToken ct)
        {
            c.Data.Done["reserved"] = true;
            return Task.CompletedTask;
        }
    }

    public sealed class Release : IActivity<Order>
    {
        public Task ExecuteAsync(ActivityContext<Order> c, CancellationToken ct)
        {
            c.Data.Undone.Add("released");
            return Task.CompletedTask;
        }
    }

    public sealed class AlwaysFails : IActivity<Order>
    {
        public Task ExecuteAsync(ActivityContext<Order> c, CancellationToken ct)
            => throw new InvalidOperationException("step failed");
    }

    private sealed class Suspending : IWorkflow<Order>
    {
        public string Id => "suspending";
        public int Version => 1;
        public void Build(IWorkflowBuilder<Order> flow) => flow
            .Saga(saga => saga
                .Then<Reserve>().CompensateWith<Release>()
                .Then<AlwaysFails>().OnFailure(StepFailurePolicy.Suspend));
    }

    private sealed class Terminating : IWorkflow<Order>
    {
        public string Id => "terminating";
        public int Version => 1;
        public void Build(IWorkflowBuilder<Order> flow) => flow
            .Saga(saga => saga
                .Then<Reserve>().CompensateWith<Release>()
                .Then<AlwaysFails>().OnFailure(StepFailurePolicy.Terminate));
    }

    /// <summary>The default, kept alongside the others so the contrast is asserted, not assumed.</summary>
    private sealed class Unwinding : IWorkflow<Order>
    {
        public string Id => "unwinding";
        public int Version => 1;
        public void Build(IWorkflowBuilder<Order> flow) => flow
            .Saga(saga => saga
                .Then<Reserve>().CompensateWith<Release>()
                .Then<AlwaysFails>());
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
                // Every policy is consulted only once retries are spent, so there must be none.
                o.DefaultRetry = Retry.None;
            }));

        return builder.Build();
    }

    private static async Task<(WorkflowInstanceRecord Instance, Order Data)> RunAsync(
        IHost host, FakeTimeProvider time, string definitionId, params WorkflowInstanceState[] until)
    {
        await host.StartAsync();
        var client = host.Services.GetRequiredService<IWorkflowClient>();
        var storage = host.Services.GetRequiredService<IWorkflowStorage>();
        var id = await client.StartAsync(definitionId, new Order());

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var current = await storage.GetInstanceAsync(id, CancellationToken.None);
            if (current is not null && until.Contains(current.State))
            {
                return (current, (await client.GetDataAsync<Order>(id))!);
            }

            time.Advance(TimeSpan.FromMilliseconds(200));
            await Task.Delay(50);
        }

        var last = await storage.GetInstanceAsync(id, CancellationToken.None);
        throw new TimeoutException(
            $"Expected one of [{string.Join(", ", until)}]; was {last?.State}. Cursor={last?.CursorJson}");
    }

    private static FakeTimeProvider NewTime() => new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Without_a_policy_a_failed_step_still_unwinds_the_saga()
    {
        var time = NewTime();
        using var host = BuildHost<Unwinding>(time);

        var (instance, data) = await RunAsync(
            host, time, "unwinding", WorkflowInstanceState.Compensated, WorkflowInstanceState.Failed);

        // The baseline the other two opt out of.
        Assert.Equal(WorkflowInstanceState.Compensated, instance.State);
        Assert.Equal(["released"], data.Undone);
    }

    [Fact]
    public async Task Suspend_parks_the_instance_and_undoes_nothing()
    {
        var time = NewTime();
        using var host = BuildHost<Suspending>(time);

        var (instance, data) = await RunAsync(
            host, time, "suspending",
            WorkflowInstanceState.Suspended, WorkflowInstanceState.Compensated, WorkflowInstanceState.Failed);

        Assert.Equal(WorkflowInstanceState.Suspended, instance.State);

        // The point of Suspend: the earlier step stays done, so an operator can still choose to
        // unwind. Compensating first would have taken that choice away.
        Assert.Empty(data.Undone);
        Assert.True(data.Done.GetValueOrDefault("reserved"));
    }

    [Fact]
    public async Task Terminate_fails_the_instance_and_leaves_earlier_work_standing()
    {
        var time = NewTime();
        using var host = BuildHost<Terminating>(time);

        var (instance, data) = await RunAsync(
            host, time, "terminating",
            WorkflowInstanceState.Failed, WorkflowInstanceState.Compensated, WorkflowInstanceState.Suspended);

        Assert.Equal(WorkflowInstanceState.Failed, instance.State);
        Assert.Empty(data.Undone);
        Assert.True(data.Done.GetValueOrDefault("reserved"));
    }

    private sealed class PolicyBeforeStep : IWorkflow<Order>
    {
        public string Id => "policy-before-step";
        public int Version => 1;
        public void Build(IWorkflowBuilder<Order> flow) =>
            flow.Saga(saga => saga.OnFailure(StepFailurePolicy.Suspend));
    }

    [Fact]
    public void A_policy_must_follow_the_step_it_governs()
    {
        // Same rule as CompensateWith, and the same reason: it annotates the preceding step, so
        // there is nothing sensible it could mean first. Failing at registration rather than at
        // runtime matters — a mis-ordered builder call is a bug in the definition, and the whole
        // point of a code-first builder is that such bugs never reach an instance.
        var time = NewTime();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddLogging();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddMillrace(m => m.UseInMemoryStorage().AddWorkflow<PolicyBeforeStep>()));

        Assert.Contains("must follow the step", ex.Message);
    }
}
