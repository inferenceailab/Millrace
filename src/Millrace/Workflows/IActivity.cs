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

    public WorkflowInstanceId InstanceId { get; }

    /// <summary>The graph node being executed — the same id the persisted cursor carries.</summary>
    public string NodeId { get; }

    public string DefinitionId { get; }

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
    string Id { get; }

    int Version { get; }

    void Build(IWorkflowBuilder<TData> flow);
}
