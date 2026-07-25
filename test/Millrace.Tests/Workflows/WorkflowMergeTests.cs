using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Millrace.Storage;
using Millrace.Workflows;
using Xunit;

namespace Millrace.Tests.Workflows;

/// <summary>
/// Checkpoint conflicts rebase the merge instead of re-running the activity (#67, §6.2).
/// </summary>
public sealed class WorkflowMergeTests
{
    // ---------------------------------------------------------------- the merge rule

    private static string Merge(string before, string after, string fresh)
        => JsonMerge.Apply(JsonNode.Parse(before), JsonNode.Parse(after), JsonNode.Parse(fresh))!.ToJsonString();

    [Fact]
    public void Disjoint_edits_from_both_sides_survive()
    {
        // The whole reason §6.2 asks parallel branches to write disjoint regions.
        var merged = Merge(
            before: """{"a":0,"b":0}""",
            after: """{"a":1,"b":0}""",
            fresh: """{"a":0,"b":2}""");

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"a":1,"b":2}"""), JsonNode.Parse(merged)));
    }

    [Fact]
    public void An_untouched_property_keeps_the_winners_value()
    {
        var merged = Merge(
            before: """{"a":0}""",
            after: """{"a":0}""",
            fresh: """{"a":9}""");

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"a":9}"""), JsonNode.Parse(merged)));
    }

    [Fact]
    public void Both_sides_writing_the_same_property_is_last_writer_wins()
    {
        // No merge can do better without a domain rule; the point is that it is defined.
        var merged = Merge(
            before: """{"a":0}""",
            after: """{"a":1}""",
            fresh: """{"a":2}""");

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"a":1}"""), JsonNode.Parse(merged)));
    }

    [Fact]
    public void Nested_objects_merge_per_property()
    {
        var merged = Merge(
            before: """{"o":{"x":0,"y":0}}""",
            after: """{"o":{"x":1,"y":0}}""",
            fresh: """{"o":{"x":0,"y":5}}""");

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"o":{"x":1,"y":5}}"""), JsonNode.Parse(merged)));
    }

    [Fact]
    public void Arrays_replace_wholesale()
    {
        // Elements have no identity to merge on, so the activity's array is the change.
        var merged = Merge(
            before: """{"xs":[1]}""",
            after: """{"xs":[1,2]}""",
            fresh: """{"xs":[1,3]}""");

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"xs":[1,2]}"""), JsonNode.Parse(merged)));
    }

    [Fact]
    public void A_removed_property_stays_removed()
    {
        var merged = Merge(
            before: """{"a":1,"b":1}""",
            after: """{"b":1}""",
            fresh: """{"a":1,"b":1,"c":1}""");

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"b":1,"c":1}"""), JsonNode.Parse(merged)));
    }

    // ---------------------------------------------------------------- end to end

    public sealed class Counters
    {
        public int A { get; set; }
        public int B { get; set; }
        public int C { get; set; }
    }

    /// <summary>
    /// Counts executions outside the data document.
    /// </summary>
    /// <remarks>
    /// It has to live outside. A list inside the document would be merged by the array rule —
    /// replaced wholesale, since elements have no identity — so it could never evidence how many
    /// times anything ran.
    /// </remarks>
    public sealed class ExecutionCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Record() => Interlocked.Increment(ref _count);
    }

    public abstract class Bump(ExecutionCounter counter, Func<Counters, Action> select) : IActivity<Counters>
    {
        public Task ExecuteAsync(ActivityContext<Counters> context, CancellationToken ct)
        {
            select(context.Data)();
            counter.Record();
            return Task.CompletedTask;
        }
    }

    public sealed class BumpA(ExecutionCounter c) : Bump(c, d => () => d.A++);
    public sealed class BumpB(ExecutionCounter c) : Bump(c, d => () => d.B++);
    public sealed class BumpC(ExecutionCounter c) : Bump(c, d => () => d.C++);

    private sealed class ThreeWays : IWorkflow<Counters>
    {
        public string Id => "three-ways";
        public int Version => 1;
        public void Build(IWorkflowBuilder<Counters> flow) => flow
            .Parallel(
                a => a.Then<BumpA>(),
                b => b.Then<BumpB>(),
                c => c.Then<BumpC>());
    }

    [Fact]
    public async Task A_contended_fan_out_runs_each_activity_exactly_once()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddSingleton<ExecutionCounter>();
        builder.Services.AddMillrace(m => m
            .UseInMemoryStorage()
            .AddWorkflow<ThreeWays>()
            .Configure(o =>
            {
                o.MinPollDelay = TimeSpan.FromMilliseconds(5);
                o.MaxPollDelay = TimeSpan.FromMilliseconds(20);
                o.SchedulerInterval = TimeSpan.FromMilliseconds(5);
                // The point of the test: with no retry policy, a losing branch has no second chance
                // through the substrate. It must converge purely by re-merging.
                o.DefaultRetry = Retry.None;
            }));

        using var host = builder.Build();
        await host.StartAsync();

        var id = await host.Services.GetRequiredService<IWorkflowClient>()
            .StartAsync("three-ways", new Counters());

        var storage = host.Services.GetRequiredService<IWorkflowStorage>();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        WorkflowInstanceRecord? instance = null;
        while (DateTime.UtcNow < deadline)
        {
            instance = await storage.GetInstanceAsync(id, CancellationToken.None);
            if (instance is not null && instance.State != WorkflowInstanceState.Running)
            {
                break;
            }

            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(15);
        }

        var data = await host.Services.GetRequiredService<IWorkflowClient>().GetDataAsync<Counters>(id);

        Assert.Equal(WorkflowInstanceState.Completed, instance!.State);
        Assert.NotNull(data);

        // Each branch's edit survived the others — the merge rebases rather than overwrites.
        Assert.Equal(1, data.A);
        Assert.Equal(1, data.B);
        Assert.Equal(1, data.C);

        // And no activity ran twice, which is what would happen if a conflict retried the job
        // instead of rebasing the merge.
        Assert.Equal(3, host.Services.GetRequiredService<ExecutionCounter>().Count);
    }
}
