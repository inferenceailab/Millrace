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

    /// <summary>
    /// A sequence whose completed steps are undone in reverse if a later one fails past its retry
    /// policy.
    /// </summary>
    Saga = 6,
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
    /// <summary>Identifies the node within its graph.</summary>
    /// <remarks>
    /// Generated from build order, not chosen — and a persisted cursor refers to instances by these
    /// ids, so an edit that shifts them silently repoints every in-flight instance. That is what
    /// versioning a definition protects against, and why node ids are a correctness concern rather
    /// than a naming one.
    /// </remarks>
    public required string Id { get; init; }

    /// <summary>What kind of node this is, which decides how the engine advances past it.</summary>
    /// <remarks>
    /// Also decides which of the optional fields below carry anything: an activity node names an
    /// activity type, a branch names its arms, and the rest stay null.
    /// </remarks>
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

    /// <summary>
    /// The activity that undoes this one, for a step inside a <see cref="WorkflowNodeKind.Saga"/>.
    /// </summary>
    /// <remarks>
    /// A step without one is still part of the saga and still recorded as completed — it simply has
    /// nothing to undo, which is normal for a read or a notification.
    /// </remarks>
    public string? Compensation { get; init; }

    /// <summary>
    /// The saga this node belongs to, assigned at compile time by walking each saga's body.
    /// </summary>
    /// <remarks>
    /// Computed rather than threaded through the running jobs: a failed step has to find its saga
    /// from the graph alone, because the job that failed carries only its own node id.
    /// </remarks>
    public string? SagaId { get; init; }

    /// <summary>
    /// What happens when this step exhausts its retries (§6.4, §11.28).
    /// </summary>
    /// <remarks>
    /// Null means <see cref="StepFailurePolicy.Retry"/> — the retry policy governs, and exhausting
    /// it falls through to the saga's default behaviour of unwinding. Serialized, so an instance
    /// that started under one policy keeps it even if the definition is edited.
    /// </remarks>
    public StepFailurePolicy? OnFailure { get; init; }
}

/// <summary>
/// What a saga step does when its retries are exhausted (§6.4).
/// </summary>
/// <remarks>
/// These decide what <em>exhaustion</em> means, not whether to retry: by the time any of them is
/// consulted the job has already spent its retry budget. So <see cref="Retry"/> is not "try again",
/// it is "the retry policy was the whole answer" — which is why it is the default and why the other
/// three are the interesting ones.
/// </remarks>
public enum StepFailurePolicy
{
    /// <summary>
    /// The retry policy governs; exhausting it unwinds the saga. The default, and the only
    /// behaviour before §11.28.
    /// </summary>
    Retry = 0,

    /// <summary>Unwind the saga immediately. Identical to <see cref="Retry"/> once retries are spent.</summary>
    Compensate = 1,

    /// <summary>
    /// Park the instance for an operator, undoing nothing.
    /// </summary>
    /// <remarks>
    /// For a step where unwinding is the wrong reflex — a partial refund, an external system that
    /// cannot be un-called — so a human decides before anything else moves. The saga's completed
    /// steps stay completed and the unwind can still be started later.
    /// </remarks>
    Suspend = 2,

    /// <summary>
    /// Fail the instance immediately, skipping the unwind.
    /// </summary>
    /// <remarks>
    /// For a step whose failure means the saga's earlier work should <em>stand</em> — undoing it
    /// would be worse than leaving it. Terminal: unlike <see cref="Suspend"/>, nothing resumes from
    /// here.
    /// </remarks>
    Terminate = 3,
}

/// <summary>
/// The serializable shape of a workflow definition — what the dashboard draws and a future
/// designer edits.
/// </summary>
public sealed record WorkflowGraph
{
    /// <summary>The workflow this is the shape of.</summary>
    public required string DefinitionId { get; init; }

    /// <summary>The version this shape belongs to.</summary>
    /// <remarks>
    /// Exported alongside the nodes so a rendered graph says which version it is drawing — two
    /// versions of one workflow are different shapes, and a picture without this cannot say which.
    /// </remarks>
    public required int Version { get; init; }

    /// <summary>Entry node; null only for an empty definition, which validation rejects.</summary>
    public string? Start { get; init; }

    /// <summary>Every node in the graph, flat.</summary>
    /// <remarks>
    /// A flat list rather than a tree: nesting is expressed by nodes referring to each other by id,
    /// which is what lets a cursor name a position with a single string and lets the whole shape
    /// survive a round trip through JSON.
    /// </remarks>
    public required IReadOnlyList<WorkflowNode> Nodes { get; init; }
}
