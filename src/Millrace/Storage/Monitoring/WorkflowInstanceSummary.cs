namespace Millrace.Storage.Monitoring;

/// <summary>
/// A workflow instance as shown in a list view.
/// </summary>
/// <remarks>
/// Excludes <see cref="WorkflowInstanceRecord.DataJson"/> and
/// <see cref="WorkflowInstanceRecord.CursorJson"/> for the same reason
/// <see cref="JobSummary"/> excludes arguments: the data document is the workflow's business state,
/// frequently large and frequently personal, and a list view never renders it.
/// </remarks>
public sealed record WorkflowInstanceSummary
{
    public required WorkflowInstanceId Id { get; init; }

    public required string DefinitionId { get; init; }

    public required int DefinitionVersion { get; init; }

    public required WorkflowInstanceState State { get; init; }

    public string? TenantId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last checkpoint time — how an operator spots a stalled instance.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Optimistic concurrency token, which doubles as a checkpoint count: how many times the engine
    /// has advanced this instance.
    /// </summary>
    public required long Revision { get; init; }
}
