using System.Text.Json;
using System.Text.Json.Nodes;
using Millrace.Workflows;
using Xunit;

namespace Millrace.Tests.Workflows;

public sealed class InvoiceData
{
    public string InvoiceId { get; set; } = "inv-1";
    public decimal Amount { get; set; }
    public bool Approved { get; set; }
    public List<string> LineItems { get; set; } = [];
}

public sealed record ManagerDecision(bool IsApproved);

public sealed class Validate : IActivity<InvoiceData>
{
    public Task ExecuteAsync(ActivityContext<InvoiceData> context, CancellationToken ct) => Task.CompletedTask;
}

public sealed class PostToErp : IActivity<InvoiceData>
{
    public Task ExecuteAsync(ActivityContext<InvoiceData> context, CancellationToken ct) => Task.CompletedTask;
}

public sealed class SendReceipt : IActivity<InvoiceData>
{
    public Task ExecuteAsync(ActivityContext<InvoiceData> context, CancellationToken ct) => Task.CompletedTask;
}

public sealed class UpdateAnalytics : IActivity<InvoiceData>
{
    public Task ExecuteAsync(ActivityContext<InvoiceData> context, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Exercises every node kind in one definition.</summary>
public sealed class InvoiceApproval : IWorkflow<InvoiceData>
{
    public string Id => "invoice-approval";

    public int Version => 2;

    public void Build(IWorkflowBuilder<InvoiceData> flow) => flow
        .StartWith<Validate>()
        .If(
            d => d.Amount > 10_000,
            approved => approved.WaitForSignal<ManagerDecision>(
                "manager-approval",
                d => d.InvoiceId,
                (d, signal) => d.Approved = signal.IsApproved,
                timeout: TimeSpan.FromDays(7)))
        .Delay(TimeSpan.FromMinutes(5))
        .ForEach(d => d.LineItems, body => body.Then<PostToErp>())
        .Parallel(
            branch => branch.Then<SendReceipt>(),
            branch => branch.Then<UpdateAnalytics>());
}

public sealed class WorkflowDefinitionTests
{
    private static WorkflowDefinition Compile() => WorkflowDefinition.Compile(new InvoiceApproval());

    [Fact]
    public void A_definition_is_keyed_by_id_and_version()
    {
        var definition = Compile();

        Assert.Equal("invoice-approval", definition.Id);
        Assert.Equal(2, definition.Version);
        Assert.Equal(typeof(InvoiceData), definition.DataType);
    }

    [Fact]
    public void The_sequence_is_linked_in_declaration_order()
    {
        var graph = Compile().Graph;
        var byId = graph.Nodes.ToDictionary(n => n.Id);

        var order = new List<WorkflowNodeKind>();
        for (var id = graph.Start; id is not null; id = byId[id].Next)
        {
            order.Add(byId[id].Kind);
        }

        Assert.Equal(
            [
                WorkflowNodeKind.Activity,
                WorkflowNodeKind.If,
                WorkflowNodeKind.Delay,
                WorkflowNodeKind.ForEach,
                WorkflowNodeKind.Parallel,
            ],
            order);
    }

    [Fact]
    public void Branch_bodies_are_reachable_but_not_part_of_the_outer_sequence()
    {
        var graph = Compile().Graph;
        var byId = graph.Nodes.ToDictionary(n => n.Id);

        var branch = graph.Nodes.Single(n => n.Kind == WorkflowNodeKind.If);
        Assert.NotNull(branch.WhenTrue);
        Assert.Null(branch.WhenFalse); // no else was declared
        Assert.Equal(WorkflowNodeKind.WaitForSignal, byId[branch.WhenTrue].Kind);

        // The branch body ends rather than rejoining: the engine returns to the If's Next.
        Assert.Null(byId[branch.WhenTrue].Next);
    }

    [Fact]
    public void Parallel_records_every_branch_entry()
    {
        var graph = Compile().Graph;
        var byId = graph.Nodes.ToDictionary(n => n.Id);
        var parallel = graph.Nodes.Single(n => n.Kind == WorkflowNodeKind.Parallel);

        Assert.Equal(2, parallel.Branches.Count);
        Assert.All(parallel.Branches, id => Assert.Equal(WorkflowNodeKind.Activity, byId[id].Kind));
    }

    [Fact]
    public void Node_ids_are_deterministic_across_compilations()
    {
        // Persisted cursors reference node ids, so a rebuild in another process must produce the
        // same ids or every in-flight instance would be stranded.
        var first = Compile().Graph.Nodes.Select(n => n.Id).ToList();
        var second = Compile().Graph.Nodes.Select(n => n.Id).ToList();

        Assert.Equal(first, second);
        Assert.Equal(first.Count, first.Distinct().Count());
    }

    [Fact]
    public void Conditions_and_collections_are_rendered_for_display()
    {
        var graph = Compile().Graph;

        var branch = graph.Nodes.Single(n => n.Kind == WorkflowNodeKind.If);
        var each = graph.Nodes.Single(n => n.Kind == WorkflowNodeKind.ForEach);

        Assert.Contains("Amount", branch.Condition);
        Assert.Contains("LineItems", each.Collection);
    }

    [Fact]
    public void Signal_nodes_carry_name_payload_type_and_timeout()
    {
        var signal = Compile().Graph.Nodes.Single(n => n.Kind == WorkflowNodeKind.WaitForSignal);

        Assert.Equal("manager-approval", signal.SignalName);
        Assert.Contains("ManagerDecision", signal.PayloadType);
        Assert.Equal(TimeSpan.FromDays(7), signal.Timeout);
    }

    [Fact]
    public void The_exported_shape_is_json_and_carries_no_executable_state()
    {
        var json = Compile().ExportGraph();
        var document = JsonNode.Parse(json)!.AsObject();

        Assert.Equal("invoice-approval", (string?)document["definitionId"]);
        Assert.Equal(2, (int?)document["version"]);
        Assert.NotNull(document["start"]);
        Assert.Equal(5 + 4, document["nodes"]!.AsArray().Count); // 5 in sequence + 4 in branches

        // Enums export as names so a renderer never depends on numeric ordering.
        Assert.Contains("\"kind\":\"Activity\"", json, StringComparison.Ordinal);
        // The shape is data: no delegate, lambda or type handle can appear in it.
        Assert.DoesNotContain("System.Func", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_exported_shape_round_trips()
    {
        var definition = Compile();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

        var restored = JsonSerializer.Deserialize<WorkflowGraph>(definition.ExportGraph(), options);

        Assert.NotNull(restored);
        Assert.Equal(definition.Graph.Start, restored.Start);
        Assert.Equal(definition.Graph.Nodes.Count, restored.Nodes.Count);
    }

    // ---------------------------------------------------------------- validation

    private sealed class Empty : IWorkflow<InvoiceData>
    {
        public string Id => "empty";
        public int Version => 1;
        public void Build(IWorkflowBuilder<InvoiceData> flow) { }
    }

    private sealed class Blank : IWorkflow<InvoiceData>
    {
        public string Id => "  ";
        public int Version => 1;
        public void Build(IWorkflowBuilder<InvoiceData> flow) => flow.StartWith<Validate>();
    }

    private sealed class ZeroVersion : IWorkflow<InvoiceData>
    {
        public string Id => "zero";
        public int Version => 0;
        public void Build(IWorkflowBuilder<InvoiceData> flow) => flow.StartWith<Validate>();
    }

    [Fact]
    public void A_definition_with_no_steps_is_rejected()
        => Assert.Contains("no steps", Assert.Throws<ArgumentException>(() => WorkflowDefinition.Compile(new Empty())).Message);

    [Fact]
    public void A_blank_id_is_rejected()
        => Assert.Contains("no Id", Assert.Throws<ArgumentException>(() => WorkflowDefinition.Compile(new Blank())).Message);

    [Fact]
    public void A_version_below_one_is_rejected()
        => Assert.Contains("version", Assert.Throws<ArgumentException>(() => WorkflowDefinition.Compile(new ZeroVersion())).Message);

    [Fact]
    public void A_single_branch_parallel_is_rejected()
    {
        // One branch is a plain sequence; accepting it would put a needless fan-out in the graph.
        var state = WorkflowDefinition.Compile(new InvoiceApproval());
        Assert.NotNull(state);

        var ex = Assert.Throws<ArgumentException>(() =>
            WorkflowDefinition.Compile(new SingleBranchParallel()));
        Assert.Contains("at least two branches", ex.Message);
    }

    private sealed class SingleBranchParallel : IWorkflow<InvoiceData>
    {
        public string Id => "one-branch";
        public int Version => 1;
        public void Build(IWorkflowBuilder<InvoiceData> flow)
            => flow.Parallel(branch => branch.Then<SendReceipt>());
    }

    [Fact]
    public void A_non_positive_delay_is_rejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => WorkflowDefinition.Compile(new ZeroDelay()));

    private sealed class ZeroDelay : IWorkflow<InvoiceData>
    {
        public string Id => "zero-delay";
        public int Version => 1;
        public void Build(IWorkflowBuilder<InvoiceData> flow) => flow.StartWith<Validate>().Delay(TimeSpan.Zero);
    }
}
