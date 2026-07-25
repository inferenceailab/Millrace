using System.Text.Json;
using Millrace.Invocations;
using Millrace.Storage;

namespace Millrace.Workflows;

/// <summary>
/// Builds the Layer 1 job that runs one graph node.
/// </summary>
/// <remarks>
/// Shared by the client (first activity) and the dispatcher (every subsequent one) so both produce
/// identical invocations. The invocation targets <see cref="IWorkflowDispatcher"/> rather than the
/// activity type: the substrate resolves the target from DI by declared type name, and routing the
/// call through the dispatcher is what lets the graph walk stay in one place.
/// </remarks>
internal static class WorkflowJobFactory
{
    private static readonly string DispatcherTypeName = TypeNameFormatter.Format(typeof(IWorkflowDispatcher));

    private static readonly IReadOnlyList<string> ParameterTypes =
    [
        TypeNameFormatter.Format(typeof(Guid)),
        TypeNameFormatter.Format(typeof(string)),
        TypeNameFormatter.Format(typeof(string)),
        TypeNameFormatter.Format(typeof(int)),
        TypeNameFormatter.Format(typeof(CancellationToken)),
    ];

    public static JobRecord Create(
        WorkflowInstanceRecord instance,
        string nodeId,
        string? joinKey,
        int loopIndex,
        TimeSpan? delay,
        MillraceOptions options,
        TimeProvider time)
    {
        var now = time.GetUtcNow();
        return new JobRecord
        {
            Id = JobId.New(time),
            Queue = options.WorkflowQueue,
            State = delay is null ? JobState.Enqueued : JobState.Scheduled,
            DueAt = delay is null ? null : now + delay,
            Invocation = new JobInvocation
            {
                TypeName = DispatcherTypeName,
                MethodName = nameof(IWorkflowDispatcher.ExecuteAsync),
                ParameterTypes = ParameterTypes,
                ArgumentsJson =
                [
                    JsonSerializer.Serialize(instance.Id.Value, options.SerializerOptions),
                    JsonSerializer.Serialize(nodeId, options.SerializerOptions),
                    joinKey is null ? "null" : JsonSerializer.Serialize(joinKey, options.SerializerOptions),
                    JsonSerializer.Serialize(loopIndex, options.SerializerOptions),
                    null,
                ],
            },
            Retry = options.DefaultRetry,
            CreatedAt = now,
            TenantId = instance.TenantId,
            // Correlation the dashboard reads: an activity job always names the instance and node
            // it belongs to.
            WorkflowInstanceId = instance.Id,
            ActivityNodeId = nodeId,
        };
    }
}
