namespace Millrace.Storage;

/// <summary>
/// A suspended workflow instance's wait-point for a signal (ARCHITECTURE.md §6.3). While a
/// bookmark exists no job does — a suspended workflow costs nothing.
/// </summary>
public sealed record BookmarkRecord
{
    /// <summary>The bookmark's own identity, so a delivered signal can consume exactly one.</summary>
    public required Guid Id { get; init; }

    /// <summary>The instance that resumes when this bookmark is matched.</summary>
    /// <remarks>
    /// The bookmark records <em>that</em> an instance is waiting. <em>Where</em> it resumes lives in
    /// the instance's cursor, which is written in the same atom — so the two cannot disagree about
    /// whether a wait exists.
    /// </remarks>
    public required WorkflowInstanceId InstanceId { get; init; }

    /// <summary>Name of the signal being waited for.</summary>
    /// <remarks>Matched together with <see cref="CorrelationId"/>; neither identifies a wait alone.</remarks>
    public required string SignalName { get; init; }

    /// <summary>
    /// Which particular thing the signal is about — the order, the document, the user.
    /// </summary>
    /// <remarks>
    /// This is what keeps a signal from waking every instance waiting on the same name. Chosen by
    /// the application, and it has to match on both sides exactly.
    /// </remarks>
    public required string CorrelationId { get; init; }

    /// <summary>Version-free type name of the expected payload (typed signals, §11.5).</summary>
    public string? PayloadTypeName { get; init; }

    /// <summary>When the instance began waiting — the age of the wait, not of the instance.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}
