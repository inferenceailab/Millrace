namespace Millrace.Workflows;

/// <summary>
/// The job target every workflow activity, resume and timeout runs through.
/// </summary>
/// <remarks>
/// <para>
/// Activities are not enqueued directly: the substrate enqueues a call to this service, and it
/// loads the instance, does the work, and computes where the graph goes next. That keeps every
/// workflow step an ordinary Layer 1 job — inheriting retries, leases, distribution and dashboard
/// visibility — while the graph walk stays in one place.
/// </para>
/// <para>
/// Public because the substrate resolves it by name from a serialized invocation. Not intended to
/// be called by consumers.
/// </para>
/// </remarks>
public interface IWorkflowDispatcher
{
    /// <summary>
    /// Runs the node identified by <paramref name="nodeId"/> for an instance, and describes the
    /// resulting checkpoint and follow-on jobs as side effects of this job's own completion.
    /// </summary>
    /// <param name="instanceId">The instance this execution advances.</param>
    /// <param name="nodeId">The graph node to execute.</param>
    /// <param name="joinKey">
    /// The fan-out this execution belongs to, when it is running inside a
    /// <see cref="WorkflowNodeKind.Parallel"/> or <see cref="WorkflowNodeKind.ForEach"/> branch.
    /// </param>
    /// <param name="loopIndex">Iteration index, when running a <see cref="WorkflowNodeKind.ForEach"/> body.</param>
    Task ExecuteAsync(Guid instanceId, string nodeId, string? joinKey, int loopIndex, CancellationToken ct);

    /// <summary>
    /// Resumes an instance whose signal has arrived, binding the payload into the data document and
    /// continuing past the wait.
    /// </summary>
    /// <remarks>
    /// Runs as a job rather than inline in <c>SignalAsync</c> so the resume inherits retries: the
    /// bookmark is already consumed by the time this is enqueued, so losing the resume would strand
    /// the instance.
    /// </remarks>
    Task DeliverSignalAsync(
        Guid instanceId, string signalName, string correlationId, string? payloadJson, CancellationToken ct);

    /// <summary>
    /// Gives up on a wait whose timeout elapsed, continuing past it without a payload.
    /// </summary>
    /// <remarks>
    /// Races the real signal deliberately, and resolves the race through the same at-most-once
    /// bookmark consumption: whichever of the two consumes the bookmark wins, and the loser finds
    /// nothing and does nothing. No extra coordination, and no window where both fire.
    /// </remarks>
    Task TimeoutSignalAsync(Guid instanceId, string signalName, string correlationId, CancellationToken ct);

    /// <summary>
    /// Reacts to an activity that failed past its retry policy: unwinds its saga if it was inside
    /// one, and otherwise records the instance as failed.
    /// </summary>
    /// <remarks>
    /// Enqueued by the substrate atomically with the dead-letter transition, because nothing of the
    /// engine is running when a job dies — the activity threw and the worker finished the job on its
    /// own.
    /// </remarks>
    Task FailActivityAsync(Guid instanceId, string nodeId, CancellationToken ct);

    /// <summary>
    /// Runs one step's compensating activity and schedules the next one backwards, or finishes the
    /// unwind.
    /// </summary>
    Task CompensateAsync(Guid instanceId, string sagaId, string stepNodeId, CancellationToken ct);
}
