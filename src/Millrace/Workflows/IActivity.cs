namespace Millrace.Workflows;

/// <summary>
/// One unit of work in a workflow. Ordinary code with constructor DI — no determinism constraints
/// apply, because progress is checkpointed rather than replayed (ARCHITECTURE.md §6.2).
/// </summary>
/// <remarks>
/// Every execution runs as a Layer 1 job, so an activity inherits retries, backoff, leases,
/// distribution and dashboard visibility rather than reimplementing them. It also inherits
/// at-least-once execution: an activity may run twice if a worker dies after doing the work and
/// before the checkpoint commits, so side effects should be idempotent.
/// </remarks>
/// <typeparam name="TData">The workflow's data document.</typeparam>
public interface IActivity<TData>
{
    /// <summary>Runs this step of the workflow.</summary>
    /// <remarks>
    /// <para>
    /// Returning is what reports success; throwing is what reports failure, and the surrounding
    /// job's retry policy decides what happens next. There is no return value because results are
    /// recorded by mutating <see cref="ActivityContext{TData}.Data"/> — the document is what gets
    /// checkpointed, and anything an activity keeps elsewhere is gone when the job ends.
    /// </para>
    /// <para>
    /// <paramref name="ct"/> is the job's execution token, so it fires on cooperative cancellation
    /// and on shutdown. Honouring it is what lets a deploy drain cleanly instead of waiting out
    /// every long-running step.
    /// </para>
    /// </remarks>
    Task ExecuteAsync(ActivityContext<TData> context, CancellationToken ct);
}

/// <summary>
/// What an activity is given when it runs.
/// </summary>
/// <remarks>
/// Mutating <see cref="Data"/> is how an activity records its result: the engine persists the
/// document as part of the checkpoint that also completes the activity's job.
/// </remarks>
public sealed class ActivityContext<TData>
{
    /// <summary>Constructs a context for one activity execution.</summary>
    /// <remarks>
    /// Called by the engine, which fills these from the instance it is advancing. An activity is
    /// handed a context rather than building one.
    /// </remarks>
    public ActivityContext(TData data, WorkflowInstanceId instanceId, string nodeId, string definitionId, int version)
    {
        Data = data;
        InstanceId = instanceId;
        NodeId = nodeId;
        DefinitionId = definitionId;
        Version = version;
    }

    /// <summary>The workflow's data document. Mutate it to record results.</summary>
    public TData Data { get; }

    /// <summary>The instance this execution belongs to.</summary>
    /// <remarks>
    /// The same id the activity's job record carries and the dashboard lists, so logging it is what
    /// connects an activity's own diagnostics to the instance an operator is looking at.
    /// </remarks>
    public WorkflowInstanceId InstanceId { get; }

    /// <summary>The graph node being executed — the same id the persisted cursor carries.</summary>
    public string NodeId { get; }

    /// <summary>Which workflow this is an instance of.</summary>
    /// <remarks>
    /// With <see cref="Version"/>, names the exact graph being executed — which an activity shared
    /// between several workflows needs in order to know which one it is running inside.
    /// </remarks>
    public string DefinitionId { get; }

    /// <summary>
    /// Zero-based iteration index when this activity runs inside a <c>ForEach</c> body; zero
    /// otherwise.
    /// </summary>
    /// <remarks>
    /// The body sees the index rather than the item, because the item is not separate state — it
    /// lives in <see cref="Data"/>, which is the only thing checkpointed. An activity indexes the
    /// same collection the loop selected.
    /// </remarks>
    public int LoopIndex { get; init; }

    /// <summary>
    /// The definition version this instance started on. An in-flight instance always finishes on
    /// the version it started with, so this can differ from the latest registered version.
    /// </summary>
    public int Version { get; }
}

/// <summary>
/// A workflow definition: registered code, keyed by <see cref="Id"/> and <see cref="Version"/>.
/// </summary>
/// <remarks>
/// Definitions are code, not data (§6.1) — the graph <em>shape</em> exports to JSON, but the
/// definition itself is a registered type. <c>(Id, Version)</c> is the key: in-flight instances
/// always finish on the version they started with, so old versions stay registered until their
/// instances drain.
/// </remarks>
public interface IWorkflow<TData>
{
    /// <summary>Stable identity of the workflow, the same across all its versions.</summary>
    /// <remarks>
    /// Changing it does not produce a new version of this workflow — it produces a different
    /// workflow, and instances already running go on looking for a definition under the old id.
    /// </remarks>
    string Id { get; }

    /// <summary>Version of this definition, counting from 1.</summary>
    /// <remarks>
    /// Bump it whenever the graph's shape changes. Editing a graph in place under the same version
    /// leaves in-flight instances resuming into a shape their stored cursor no longer describes,
    /// and that failure surfaces long after the deploy that caused it.
    /// </remarks>
    int Version { get; }

    /// <summary>Declares the shape of the workflow.</summary>
    /// <remarks>
    /// Called once at registration rather than per instance, and the result is compiled and
    /// validated there — so a malformed graph fails at startup instead of when an instance first
    /// reaches the bad part. It describes structure only: no activity code runs here, and the
    /// builder is recording what the steps are and how they connect.
    /// </remarks>
    void Build(IWorkflowBuilder<TData> flow);
}
