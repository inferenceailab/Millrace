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
    /// <inheritdoc cref="WorkflowInstanceRecord.Id"/>
    public required WorkflowInstanceId Id { get; init; }

    /// <inheritdoc cref="WorkflowInstanceRecord.DefinitionId"/>
    public required string DefinitionId { get; init; }

    /// <inheritdoc cref="WorkflowInstanceRecord.DefinitionVersion"/>
    public required int DefinitionVersion { get; init; }

    /// <inheritdoc cref="WorkflowInstanceRecord.State"/>
    public required WorkflowInstanceState State { get; init; }

    /// <inheritdoc cref="WorkflowInstanceRecord.TenantId"/>
    public string? TenantId { get; init; }

    /// <inheritdoc cref="WorkflowInstanceRecord.CreatedAt"/>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last checkpoint time — how an operator spots a stalled instance.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Optimistic concurrency token, which doubles as a checkpoint count: how many times the engine
    /// has advanced this instance.
    /// </summary>
    public required long Revision { get; init; }
}
