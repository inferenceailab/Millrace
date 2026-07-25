namespace Millrace.Workflows;

/// <summary>The node vocabulary of a workflow graph (ARCHITECTURE.md §6.1).</summary>
public enum WorkflowNodeKind
{
    /// <summary>Runs an <see cref="IActivity{TData}"/> as a Layer 1 job.</summary>
    Activity = 0,

    /// <summary>Branches on a pure predicate over the data document.</summary>
    If = 1,

    /// <summary>Runs branches concurrently; each branch is its own job chain.</summary>
    Parallel = 2,

    /// <summary>Runs a body once per item of a collection selected from the data document.</summary>
    ForEach = 3,

    /// <summary>Suspends for a duration, as a Layer 1 scheduled job.</summary>
    Delay = 4,

    /// <summary>Suspends until a correlated signal arrives; holds no job while waiting.</summary>
    WaitForSignal = 5,
}

/// <summary>
/// One node of the exported graph shape.
/// </summary>
/// <remarks>
/// <para>
/// This is the <em>shape</em>, not the behaviour: it carries names, structure and edges, but no
/// delegates. That is what makes it serializable, and it is what the dashboard renders today and a
/// designer would edit post-1.0 (N1). Runtime bindings — condition predicates, collection
/// selectors, signal binders — live beside the graph in the compiled definition and never
/// serialize.
/// </para>
/// <para>
/// Node ids are generated deterministically from build order, so the same <c>Build</c> produces the
/// same ids in every process. Cursors persisted by a running instance reference these ids, so
/// stability is a correctness requirement, not a convenience.
/// </para>
/// </remarks>
public sealed record WorkflowNode
{
    public required string Id { get; init; }

    public required WorkflowNodeKind Kind { get; init; }

    /// <summary>The next node in this sequence; null ends the sequence.</summary>
    public string? Next { get; init; }

    /// <summary>Activity type name, for <see cref="WorkflowNodeKind.Activity"/>.</summary>
    public string? ActivityType { get; init; }

    /// <summary>
    /// Rendered text of the predicate, for <see cref="WorkflowNodeKind.If"/> — for display only.
    /// The executable predicate is a binding, not part of the shape.
    /// </summary>
    public string? Condition { get; init; }

    /// <summary>Entry node of the true branch.</summary>
    public string? WhenTrue { get; init; }

    /// <summary>Entry node of the false branch; null when there is no else.</summary>
    public string? WhenFalse { get; init; }

    /// <summary>Entry node of each branch, for <see cref="WorkflowNodeKind.Parallel"/>.</summary>
    public IReadOnlyList<string> Branches { get; init; } = [];

    /// <summary>Entry node of the body, for <see cref="WorkflowNodeKind.ForEach"/>.</summary>
    public string? Body { get; init; }

    /// <summary>Rendered text of the collection selector, for display.</summary>
    public string? Collection { get; init; }

    /// <summary>Duration, for <see cref="WorkflowNodeKind.Delay"/>.</summary>
    public TimeSpan? Delay { get; init; }

    /// <summary>Signal name, for <see cref="WorkflowNodeKind.WaitForSignal"/>.</summary>
    public string? SignalName { get; init; }

    /// <summary>Payload type name, for <see cref="WorkflowNodeKind.WaitForSignal"/>.</summary>
    public string? PayloadType { get; init; }

    /// <summary>Optional timeout after which a wait gives up.</summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>
/// The serializable shape of a workflow definition — what the dashboard draws and a future
/// designer edits.
/// </summary>
public sealed record WorkflowGraph
{
    public required string DefinitionId { get; init; }

    public required int Version { get; init; }

    /// <summary>Entry node; null only for an empty definition, which validation rejects.</summary>
    public string? Start { get; init; }

    public required IReadOnlyList<WorkflowNode> Nodes { get; init; }
}
