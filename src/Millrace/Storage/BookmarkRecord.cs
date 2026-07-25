namespace Millrace.Storage;

/// <summary>
/// A suspended workflow instance's wait-point for a signal (ARCHITECTURE.md §6.3). While a
/// bookmark exists no job does — a suspended workflow costs nothing.
/// </summary>
public sealed record BookmarkRecord
{
    public required Guid Id { get; init; }

    public required WorkflowInstanceId InstanceId { get; init; }

    public required string SignalName { get; init; }

    public required string CorrelationId { get; init; }

    /// <summary>Version-free type name of the expected payload (typed signals, §11.5).</summary>
    public string? PayloadTypeName { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
