using Millrace.Workflows;
using Xunit;

namespace Millrace.Tests.Workflows;

/// <summary>
/// How a nested saga compiles (§11.35).
/// </summary>
/// <remarks>
/// The runtime derives the enclosing saga from the graph rather than storing it on the cursor, so
/// these assertions are what everything in <c>WorkflowDispatcher</c> rests on: the inner saga's
/// steps belong to the inner saga, and the inner saga's own node belongs to the outer.
/// </remarks>
public sealed class NestedSagaGraphTests
{
    private sealed class Data
    {
        public List<string> Log { get; set; } = [];
    }

    private sealed class Step : IActivity<Data>
    {
        public Task ExecuteAsync(ActivityContext<Data> c, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Undo : IActivity<Data>
    {
        public Task ExecuteAsync(ActivityContext<Data> c, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Nested : IWorkflow<Data>
    {
        public string Id => "nested";

        public int Version => 1;

        public void Build(IWorkflowBuilder<Data> flow) => flow
            .Saga(outer => outer
                .Then<Step>().CompensateWith<Undo>()
                .Saga(
                    inner => inner.Then<Step>().CompensateWith<Undo>(),
                    NestedSagaPolicy.Unwind)
                .Then<Step>().CompensateWith<Undo>());
    }

    private static WorkflowGraph Graph() => WorkflowDefinition.Compile(new Nested()).Graph;

    [Fact]
    public void The_inner_saga_node_belongs_to_the_outer_saga()
    {
        var graph = Graph();
        var sagas = graph.Nodes.Where(n => n.Kind == WorkflowNodeKind.Saga).ToList();

        Assert.Equal(2, sagas.Count);

        // Exactly one saga is enclosed, and its SagaId names the other. That link is the whole
        // mechanism: the dispatcher reads it to find where an unwind propagates to.
        var inner = Assert.Single(sagas, s => s.SagaId is not null);
        var outer = Assert.Single(sagas, s => s.SagaId is null);
        Assert.Equal(outer.Id, inner.SagaId);
    }

    [Fact]
    public void The_inner_saga_owns_its_own_body()
    {
        var graph = Graph();
        var inner = graph.Nodes.Single(n => n.Kind == WorkflowNodeKind.Saga && n.SagaId is not null);
        var body = graph.Nodes.Single(n => n.Id == inner.Body);

        // The outer walk follows Next past the inner saga node rather than descending into it, so a
        // step inside the inner body must never be claimed by the outer.
        Assert.Equal(inner.Id, body.SagaId);
    }

    [Fact]
    public void The_outer_saga_claims_its_own_steps()
    {
        var graph = Graph();
        var outer = graph.Nodes.Single(n => n.Kind == WorkflowNodeKind.Saga && n.SagaId is null);

        var outerActivities = graph.Nodes
            .Where(n => n.Kind == WorkflowNodeKind.Activity && n.SagaId == outer.Id)
            .ToList();

        Assert.Equal(2, outerActivities.Count);
    }

    [Fact]
    public void The_nesting_policy_is_recorded_on_the_inner_saga_only()
    {
        var graph = Graph();
        var inner = graph.Nodes.Single(n => n.Kind == WorkflowNodeKind.Saga && n.SagaId is not null);
        var outer = graph.Nodes.Single(n => n.Kind == WorkflowNodeKind.Saga && n.SagaId is null);

        Assert.Equal(NestedSagaPolicy.Unwind, inner.Nesting);

        // Null on a top-level saga, where the question §11.35 answers does not arise.
        Assert.Null(outer.Nesting);
    }
}
