using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Millrace.Storage;
using Millrace.Workflows;
using Xunit;

namespace Millrace.Tests.Workflows;

/// <summary>
/// Nested sagas (#77, §11.29 and §11.35).
/// </summary>
/// <remarks>
/// <para>
/// Two claims are load-bearing and neither is visible from the graph. §11.29: a failure inside a
/// nested saga unwinds that saga <em>completely</em> before the enclosing one starts, so each
/// saga's compensations run against the state its own steps left. §11.35: whether a nested saga
/// that already committed is undone by an outer unwind is the author's choice.
/// </para>
/// <para>
/// Every assertion here is about <em>order</em> or about <em>what did not happen</em>, because both
/// failure modes are silent — an unwind in the wrong order still ends Compensated, and a nested
/// saga skipped by an outer unwind still ends Compensated too.
/// </para>
/// </remarks>
public sealed class NestedSagaTests
{
    public sealed class Trip
    {
        /// <summary>Appended by every compensation, so the assertions can read the unwind order.</summary>
        public List<string> Undone { get; set; } = [];

        public List<string> Done { get; set; } = [];

        public bool FailInner { get; set; }

        public bool FailOuterAfterInner { get; set; }

        public bool FailInnerCompensation { get; set; }
    }

    public sealed class BookHotel : IActivity<Trip>
    {
        public Task ExecuteAsync(ActivityContext<Trip> c, CancellationToken ct)
        {
            c.Data.Done.Add("hotel");
            return Task.CompletedTask;
        }
    }

    public sealed class CancelHotel : IActivity<Trip>
    {
        public Task ExecuteAsync(ActivityContext<Trip> c, CancellationToken ct)
        {
            c.Data.Undone.Add("hotel");
            return Task.CompletedTask;
        }
    }

    public sealed class ChargeDeposit : IActivity<Trip>
    {
        public Task ExecuteAsync(ActivityContext<Trip> c, CancellationToken ct)
        {
            c.Data.Done.Add("deposit");
            return Task.CompletedTask;
        }
    }

    public sealed class RefundDeposit : IActivity<Trip>
    {
        public Task ExecuteAsync(ActivityContext<Trip> c, CancellationToken ct)
        {
            if (c.Data.FailInnerCompensation)
            {
                throw new InvalidOperationException("refund failed");
            }

            c.Data.Undone.Add("deposit");
            return Task.CompletedTask;
        }
    }

    public sealed class IssueTicket : IActivity<Trip>
    {
        public Task ExecuteAsync(ActivityContext<Trip> c, CancellationToken ct)
        {
            if (c.Data.FailInner)
            {
                throw new InvalidOperationException("no seats");
            }

            c.Data.Done.Add("ticket");
            return Task.CompletedTask;
        }
    }

    public sealed class VoidTicket : IActivity<Trip>
    {
        public Task ExecuteAsync(ActivityContext<Trip> c, CancellationToken ct)
        {
            c.Data.Undone.Add("ticket");
            return Task.CompletedTask;
        }
    }

    public sealed class Confirm : IActivity<Trip>
    {
        public Task ExecuteAsync(ActivityContext<Trip> c, CancellationToken ct)
        {
            if (c.Data.FailOuterAfterInner)
            {
                throw new InvalidOperationException("confirmation rejected");
            }

            c.Data.Done.Add("confirmed");
            return Task.CompletedTask;
        }
    }

    /// <summary>Outer: hotel, then a nested payment saga, then confirm. Nested is <c>Unwind</c>.</summary>
    private sealed class BookTrip : IWorkflow<Trip>
    {
        public string Id => "trip";

        public int Version => 1;

        public void Build(IWorkflowBuilder<Trip> flow) => flow
            .Saga(outer => outer
                .Then<BookHotel>().CompensateWith<CancelHotel>()
                .Saga(
                    inner => inner
                        .Then<ChargeDeposit>().CompensateWith<RefundDeposit>()
                        .Then<IssueTicket>().CompensateWith<VoidTicket>(),
                    NestedSagaPolicy.Unwind)
                .Then<Confirm>());
    }

    /// <summary>Identical, except the nested saga is <c>Keep</c>.</summary>
    private sealed class BookTripFinalPayment : IWorkflow<Trip>
    {
        public string Id => "trip-final";

        public int Version => 1;

        public void Build(IWorkflowBuilder<Trip> flow) => flow
            .Saga(outer => outer
                .Then<BookHotel>().CompensateWith<CancelHotel>()
                .Saga(
                    inner => inner
                        .Then<ChargeDeposit>().CompensateWith<RefundDeposit>()
                        .Then<IssueTicket>().CompensateWith<VoidTicket>(),
                    NestedSagaPolicy.Keep)
                .Then<Confirm>());
    }

    private static IHost BuildHost(FakeTimeProvider time)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddMillrace(m => m
            .UseInMemoryStorage()
            .AddWorkflow<BookTrip>()
            .AddWorkflow<BookTripFinalPayment>()
            .Configure(o =>
            {
                o.MinPollDelay = TimeSpan.FromMilliseconds(5);
                o.MaxPollDelay = TimeSpan.FromMilliseconds(20);
                o.SchedulerInterval = TimeSpan.FromMilliseconds(5);
                // Compensation triggers on exhausted retries, so the budget has to run out.
                o.DefaultRetry = Retry.None;
            }));

        return builder.Build();
    }

    private static async Task<(WorkflowInstanceRecord Instance, Trip Data)> RunAsync(
        IHost host, FakeTimeProvider time, string definition, Trip seed,
        params WorkflowInstanceState[] until)
    {
        await host.StartAsync();
        var client = host.Services.GetRequiredService<IWorkflowClient>();
        var storage = host.Services.GetRequiredService<IWorkflowStorage>();
        var id = await client.StartAsync(definition, seed);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var current = await storage.GetInstanceAsync(id, CancellationToken.None);
            if (current is not null && until.Contains(current.State))
            {
                return (current, (await client.GetDataAsync<Trip>(id))!);
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
    public async Task An_inner_failure_unwinds_the_inner_saga_before_the_outer()
    {
        var time = NewTime();
        using var host = BuildHost(time);

        var (instance, data) = await RunAsync(
            host, time, "trip", new Trip { FailInner = true }, WorkflowInstanceState.Compensated);

        Assert.Equal(WorkflowInstanceState.Compensated, instance.State);

        // IssueTicket never completed, so it is not undone. The order is the whole claim of §11.29:
        // the inner saga's own step first, then the outer's — not the other way round, and not
        // interleaved. Asserting the sequence rather than the set is the point.
        Assert.Equal(["deposit", "hotel"], data.Undone);
        Assert.DoesNotContain("ticket", data.Undone);
    }

    [Fact]
    public async Task An_outer_failure_undoes_a_committed_inner_saga_when_it_declared_Unwind()
    {
        var time = NewTime();
        using var host = BuildHost(time);

        var (instance, data) = await RunAsync(
            host, time, "trip", new Trip { FailOuterAfterInner = true },
            WorkflowInstanceState.Compensated);

        Assert.Equal(WorkflowInstanceState.Compensated, instance.State);

        // The inner saga committed before the outer failed. Under Unwind it is replayed in reverse
        // — innermost step first — and only then does the outer undo its own step.
        Assert.Equal(["ticket", "deposit", "hotel"], data.Undone);
    }

    [Fact]
    public async Task An_outer_failure_leaves_a_committed_inner_saga_alone_when_it_declared_Keep()
    {
        var time = NewTime();
        using var host = BuildHost(time);

        var (instance, data) = await RunAsync(
            host, time, "trip-final", new Trip { FailOuterAfterInner = true },
            WorkflowInstanceState.Compensated);

        Assert.Equal(WorkflowInstanceState.Compensated, instance.State);

        // The payment stands. Only the outer saga's own step is undone — which is exactly the
        // guarantee the author gave up by choosing Keep, and the reason it is not the default.
        Assert.Equal(["hotel"], data.Undone);
        Assert.Contains("deposit", data.Done);
        Assert.Contains("ticket", data.Done);
    }

    [Fact]
    public async Task A_failed_inner_compensation_suspends_the_whole_instance()
    {
        var time = NewTime();
        using var host = BuildHost(time);

        var (instance, data) = await RunAsync(
            host, time,
            "trip",
            new Trip { FailInner = true, FailInnerCompensation = true },
            WorkflowInstanceState.Suspended);

        // §11.29 is explicit that this suspends the *whole* instance rather than only the inner
        // saga: a half-undone inner saga inside a still-unwinding outer one is precisely the state
        // an operator has to see before anything else moves.
        Assert.Equal(WorkflowInstanceState.Suspended, instance.State);

        // And the outer saga has not started undoing anything on top of the stuck inner one.
        Assert.DoesNotContain("hotel", data.Undone);
    }

    [Fact]
    public async Task A_trip_that_succeeds_forgets_both_sagas()
    {
        var time = NewTime();
        using var host = BuildHost(time);

        var (instance, data) = await RunAsync(
            host, time, "trip", new Trip(), WorkflowInstanceState.Completed);

        Assert.Equal(WorkflowInstanceState.Completed, instance.State);
        Assert.Empty(data.Undone);

        // A nested saga declared Unwind outlives its own commit so the outer can replay it. Once the
        // outer commits too, nothing can reach either — a record left behind would be a saga the
        // engine believes is still open.
        Assert.DoesNotContain("saga", instance.CursorJson);
    }
}
