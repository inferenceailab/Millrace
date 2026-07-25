namespace Millrace.Workflows;

/// <summary>
/// The job target every workflow activity runs through.
/// </summary>
/// <remarks>
/// <para>
/// Activities are not enqueued directly: the substrate enqueues a call to this service, and it
/// loads the instance, runs the activity, and computes where the graph goes next. That keeps every
/// activity execution an ordinary Layer 1 job — inheriting retries, leases, distribution and
/// dashboard visibility — while the graph walk stays in one place.
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
}
