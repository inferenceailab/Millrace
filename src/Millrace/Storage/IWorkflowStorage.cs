namespace Millrace.Storage;

/// <summary>
/// The Layer 0 workflow storage contract (ARCHITECTURE.md §4.1). The contract ships and is
/// conformance-tested in 0.1; the engine that drives it lands in 0.3.
/// </summary>
public interface IWorkflowStorage
{
    /// <summary>Stores a new instance with <c>Revision = 1</c>; duplicate id throws <see cref="MillraceConcurrencyException"/>.</summary>
    ValueTask CreateInstanceAsync(WorkflowInstanceRecord instance, CancellationToken ct);

    ValueTask<WorkflowInstanceRecord?> GetInstanceAsync(WorkflowInstanceId id, CancellationToken ct);

    /// <summary>
    /// Optimistic-concurrency replace: throws <see cref="MillraceConcurrencyException"/> unless the
    /// stored revision equals <paramref name="expectedRevision"/> (a missing instance is the
    /// same failure — providers must not distinguish); on success stores
    /// <c>Revision = expectedRevision + 1</c>.
    /// </summary>
    ValueTask UpdateInstanceAsync(WorkflowInstanceRecord instance, long expectedRevision, CancellationToken ct);

    ValueTask AddBookmarkAsync(BookmarkRecord bookmark, CancellationToken ct);

    /// <summary>
    /// Atomically consumes (removes and returns) the <em>oldest</em> matching bookmark —
    /// ordered by CreatedAt, then Id — or returns <see langword="null"/> when none match.
    /// At-most-once under arbitrary concurrency (§4.2.4): a signal resumes exactly one waiting
    /// instance.
    /// </summary>
    ValueTask<BookmarkRecord?> ConsumeBookmarkAsync(string signalName, string correlationId, CancellationToken ct);
}
