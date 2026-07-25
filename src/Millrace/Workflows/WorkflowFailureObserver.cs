using Microsoft.Extensions.Options;
using Millrace.Storage;

namespace Millrace.Workflows;

/// <summary>
/// Turns a dead-lettered activity job into a workflow failure notification.
/// </summary>
/// <remarks>
/// Registered only when workflows are registered, so an application using the job substrate alone
/// never pays for this and the worker stays unaware that workflows exist.
/// </remarks>
internal sealed class WorkflowFailureObserver(
    TimeProvider time, IOptions<MillraceOptions> options) : IJobFailureObserver
{
    public IReadOnlyList<JobRecord> OnDeadLettered(JobRecord job)
    {
        // A job enqueued by a consumer directly has no workflow to tell. A failed *compensation*
        // does notify — that is how a half-undone saga reaches Suspended — but a failed
        // notification must not notify again, or a broken instance would loop forever.
        if (job.WorkflowInstanceId is not { } instanceId
            || job.ActivityNodeId is not { Length: > 0 } nodeId
            || job.Invocation.MethodName is nameof(IWorkflowDispatcher.FailActivityAsync))
        {
            return [];
        }

        return [WorkflowJobFactory.CreateFailure(job, instanceId, nodeId, options.Value, time)];
    }
}
