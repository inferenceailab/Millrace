using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Millrace.Storage;
using Millrace.Workflows;
using Xunit;

namespace Millrace.Tests.Workflows;

/// <summary>
/// Sagas and compensation (#39, §6.4).
/// </summary>
/// <remarks>
/// The load-bearing claim is that compensation is triggered by <em>exhausted retries</em> and runs
/// in reverse over the steps that actually completed. Both halves need the substrate: the trigger
/// only exists because the worker tells the engine a job died, and "steps that actually completed"
/// is instance state written by earlier jobs.
/// </remarks>
public sealed class WorkflowSagaTests
{
    public sealed class Order
    {
        public Dictionary<string, bool> Done { get; set; } = [];
        public List<string> Undone { get; set; } = [];
        public bool FailCharge { get; set; }
        public bool FailCompensation { get; set; }
    }

    public sealed class ReserveStock : IActivity<Order>
    {
        public Task ExecuteAsync(ActivityContext<Order> c, CancellationToken ct)
        {
            c.Data.Done["reserved"] = true;
            return Task.CompletedTask;
        }
    }

    public sealed class ReleaseStock : IActivity<Order>
    {
        public Task ExecuteAsync(ActivityContext<Order> c, CancellationToken ct)
        {
            if (c.Data.FailCompensation)
            {
                throw new InvalidOperationException("release failed");
            }

            c.Data.Undone.Add("released");
            return Task.CompletedTask;
        }
    }

    public sealed class ChargeCustomer : IActivity<Order>
    {
        public Task ExecuteAsync(ActivityContext<Order> c, CancellationToken ct)
        {
            if (c.Data.FailCharge)
            {
                throw new InvalidOperationException("charge declined");
            }

            c.Data.Done["charged"] = true;
            return Task.CompletedTask;
        }
    }

    public sealed class RefundCustomer : IActivity<Order>
    {
        public Task ExecuteAsync(ActivityContext<Order> c, CancellationToken ct)
        {
            c.Data.Undone.Add("refunded");
            return Task.CompletedTask;
        }
    }

    public sealed class Ship : IActivity<Order>
    {
        public Task ExecuteAsync(ActivityContext<Order> c, CancellationToken ct)
        {
            c.Data.Done["shipped"] = true;
            return Task.CompletedTask;
        }
    }

    private sealed class Checkout : IWorkflow<Order>
    {
        public string Id => "checkout";
        public int Version => 1;
        public void Build(IWorkflowBuilder<Order> flow) => flow
            .Saga(saga => saga
                .Then<ReserveStock>().CompensateWith<ReleaseStock>()
                .Then<ChargeCustomer>().CompensateWith<RefundCustomer>())
            .Then<Ship>();
    }

    private static IHost BuildHost(FakeTimeProvider time)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddMillrace(m => m
            .UseInMemoryStorage()
            .AddWorkflow<Checkout>()
            .Configure(o =>
            {
                o.MinPollDelay = TimeSpan.FromMilliseconds(5);
                o.MaxPollDelay = TimeSpan.FromMilliseconds(20);
                o.SchedulerInterval = TimeSpan.FromMilliseconds(5);
                // Compensation triggers on exhausted retries, so the retry budget has to run out
                // for the test to reach the interesting part.
                o.DefaultRetry = Retry.None;
            }));

        return builder.Build();
    }

    private static async Task<(WorkflowInstanceRecord Instance, Order Data)> RunAsync(
        IHost host, FakeTimeProvider time, Order seed, params WorkflowInstanceState[] until)
    {
        await host.StartAsync();
        var client = host.Services.GetRequiredService<IWorkflowClient>();
        var storage = host.Services.GetRequiredService<IWorkflowStorage>();
        var id = await client.StartAsync("checkout", seed);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var current = await storage.GetInstanceAsync(id, CancellationToken.None);
            if (current is not null && until.Contains(current.State))
            {
                return (current, (await client.GetDataAsync<Order>(id))!);
            }

            time.Advance(TimeSpan.FromMilliseconds(200));
            await Task.Delay(15);
        }

        var last = await storage.GetInstanceAsync(id, CancellationToken.None);
        throw new TimeoutException(
            $"Expected one of [{string.Join(", ", until)}]; was {last?.State}. Cursor={last?.CursorJson}");
    }

    private static FakeTimeProvider NewTime() => new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task A_saga_that_succeeds_compensates_nothing()
    {
        var time = NewTime();
        using var host = BuildHost(time);

        var (instance, data) = await RunAsync(host, time, new Order(), WorkflowInstanceState.Completed);

        Assert.Equal(WorkflowInstanceState.Completed, instance.State);
        Assert.True(data.Done.GetValueOrDefault("shipped"));
        Assert.Empty(data.Undone);
        // The saga stops being tracked once it has nothing left to undo.
        Assert.DoesNotContain("saga1", instance.CursorJson);
    }

    [Fact]
    public async Task A_failed_step_undoes_the_completed_ones_in_reverse()
    {
        var time = NewTime();
        using var host = BuildHost(time);

        var (instance, data) = await RunAsync(
            host, time, new Order { FailCharge = true }, WorkflowInstanceState.Compensated);

        Assert.Equal(WorkflowInstanceState.Compensated, instance.State);

        // ChargeCustomer never completed, so it is not undone; ReserveStock is.
        Assert.Equal(["released"], data.Undone);
        Assert.True(data.Done.GetValueOrDefault("reserved"));
        Assert.False(data.Done.GetValueOrDefault("charged"));

        // And the flow does not continue past a compensated saga.
        Assert.False(data.Done.GetValueOrDefault("shipped"));
    }

    [Fact]
    public async Task A_failed_compensation_leaves_the_instance_suspended()
    {
        var time = NewTime();
        using var host = BuildHost(time);

        // A half-undone saga is exactly where automatic behaviour is worse than an operator looking.
        var (instance, data) = await RunAsync(
            host, time, new Order { FailCharge = true, FailCompensation = true },
            WorkflowInstanceState.Suspended, WorkflowInstanceState.Compensated);

        Assert.Equal(WorkflowInstanceState.Suspended, instance.State);
        Assert.Empty(data.Undone);
    }

    /// <summary>
    /// Drives a saga to the suspended half-undone state, then hands it to an operator (§11.30).
    /// </summary>
    private static async Task<(IWorkflowClient Client, WorkflowInstanceId Id, IWorkflowStorage Storage)>
        SuspendedUnwindAsync(IHost host, FakeTimeProvider time, Order seed)
    {
        await host.StartAsync();
        var client = host.Services.GetRequiredService<IWorkflowClient>();
        var storage = host.Services.GetRequiredService<IWorkflowStorage>();
        var id = await client.StartAsync("checkout", seed);

        await Eventually.ObservedAsync(
            async () => await storage.GetInstanceAsync(id, CancellationToken.None),
            i => { time.Advance(TimeSpan.FromMilliseconds(200)); return i?.State == WorkflowInstanceState.Suspended; },
            "the failed compensation to suspend the instance");

        return (client, id, storage);
    }

    private static Task<WorkflowInstanceRecord?> SettlesAtAsync(
        IWorkflowStorage storage, FakeTimeProvider time, WorkflowInstanceId id, WorkflowInstanceState state)
        => Eventually.ObservedAsync(
            async () => await storage.GetInstanceAsync(id, CancellationToken.None),
            i => { time.Advance(TimeSpan.FromMilliseconds(200)); return i?.State == state; },
            $"the instance to reach {state}");

    [Fact]
    public async Task Abandoning_a_failed_compensation_fails_the_instance_and_undoes_no_more()
    {
        var time = NewTime();
        using var host = BuildHost(time);
        var (client, id, storage) = await SuspendedUnwindAsync(
            host, time, new Order { FailCharge = true, FailCompensation = true });

        Assert.True(await client.RecoverCompensationAsync(id, CompensationRecovery.Abandon));

        // The decision is that the remaining steps should stand: terminal, and nothing further runs.
        var instance = await SettlesAtAsync(storage, time, id, WorkflowInstanceState.Failed);
        Assert.Equal(WorkflowInstanceState.Failed, instance!.State);
        Assert.Empty((await client.GetDataAsync<Order>(id))!.Undone);
    }

    [Fact]
    public async Task Skipping_a_failed_compensation_carries_on_unwinding()
    {
        var time = NewTime();
        using var host = BuildHost(time);
        var (client, id, storage) = await SuspendedUnwindAsync(
            host, time, new Order { FailCharge = true, FailCompensation = true });

        Assert.True(await client.RecoverCompensationAsync(id, CompensationRecovery.Skip));

        // Skip records the operator's assertion that the step is undone — it deliberately does not
        // run the compensation, so "released" never appears even though the unwind completes.
        var instance = await SettlesAtAsync(storage, time, id, WorkflowInstanceState.Compensated);
        Assert.Equal(WorkflowInstanceState.Compensated, instance!.State);
        Assert.Empty((await client.GetDataAsync<Order>(id))!.Undone);
    }

    [Fact]
    public async Task Recovering_something_that_is_not_suspended_reports_false()
    {
        var time = NewTime();
        using var host = BuildHost(time);
        await host.StartAsync();

        var client = host.Services.GetRequiredService<IWorkflowClient>();
        var id = await client.StartAsync("checkout", new Order());

        // The stale-button case: an ordinary answer, not a fault, so the dashboard can re-read
        // rather than alarm.
        Assert.False(await client.RecoverCompensationAsync(id, CompensationRecovery.Retry));
    }

    [Fact]
    public void The_exported_shape_records_each_step_and_its_compensation()
    {
        var graph = WorkflowDefinition.Compile(new Checkout()).Graph;
        var saga = graph.Nodes.Single(n => n.Kind == WorkflowNodeKind.Saga);
        var steps = graph.Nodes.Where(n => n.SagaId == saga.Id).ToList();

        Assert.Equal(2, steps.Count);
        Assert.All(steps, s => Assert.NotNull(s.Compensation));
        Assert.Contains(steps, s => s.Compensation!.Contains("ReleaseStock"));
        Assert.Contains(steps, s => s.Compensation!.Contains("RefundCustomer"));
    }

    [Fact]
    public void CompensateWith_before_any_step_is_rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WorkflowDefinition.Compile(new Malformed()));

        Assert.Contains("must follow the step it undoes", ex.Message);
    }

    private sealed class Malformed : IWorkflow<Order>
    {
        public string Id => "malformed";
        public int Version => 1;
        public void Build(IWorkflowBuilder<Order> flow)
            => flow.Saga(saga => saga.CompensateWith<ReleaseStock>());
    }

    [Fact]
    public void An_empty_saga_is_rejected()
        => Assert.Contains(
            "at least one step",
            Assert.Throws<ArgumentException>(() => WorkflowDefinition.Compile(new EmptySaga())).Message);

    private sealed class EmptySaga : IWorkflow<Order>
    {
        public string Id => "empty-saga";
        public int Version => 1;
        public void Build(IWorkflowBuilder<Order> flow) => flow.Saga(_ => { });
    }
}
