namespace Millrace.Storage;

/// <summary>Lifecycle states of a workflow instance (ARCHITECTURE.md §6).</summary>
public enum WorkflowInstanceState
{
    Running = 0,

    /// <summary>Waiting on a signal bookmark or parked for operator action; no job exists.</summary>
    Suspended = 1,

    Completed = 2,

    Failed = 3,

    Compensated = 4,

    Cancelled = 5,
}

/// <summary>
/// A workflow instance as persisted by a provider. The engine (0.3) checkpoints progress by
/// replacing the record under optimistic concurrency (<see cref="Revision"/>).
/// </summary>
public sealed record WorkflowInstanceRecord
{
    public required WorkflowInstanceId Id { get; init; }

    public required string DefinitionId { get; init; }

    public required int DefinitionVersion { get; init; }

    public required WorkflowInstanceState State { get; init; }

    /// <summary>
    /// The serialized <c>TData</c> document. This is a JSON <em>document</em>, not an opaque
    /// string: providers may store it in a native JSON column (jsonb etc.) and must preserve
    /// semantic content — lexical formatting (whitespace, object key order) need not survive.
    /// </summary>
    public required string DataJson { get; init; }

    /// <summary>
    /// Engine cursor state (graph positions); null before the first checkpoint. Same
    /// JSON-document semantics as <see cref="DataJson"/>.
    /// </summary>
    public string? CursorJson { get; init; }

    /// <summary>
    /// Optimistic concurrency token. <c>CreateInstanceAsync</c> stores 1; each successful
    /// <c>UpdateInstanceAsync(expectedRevision)</c> stores <c>expectedRevision + 1</c>.
    /// </summary>
    public required long Revision { get; init; }

    public string? TenantId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}
