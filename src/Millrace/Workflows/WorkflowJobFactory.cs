using System.Text.Json;
using Millrace.Invocations;
using Millrace.Storage;

namespace Millrace.Workflows;

/// <summary>
/// Builds the Layer 1 jobs that drive a workflow: activity dispatches, signal deliveries and wait
/// timeouts.
/// </summary>
/// <remarks>
/// Shared by the client and the dispatcher so every producer emits identical invocations. They
/// target <see cref="IWorkflowDispatcher"/> rather than an activity type: the substrate resolves the
/// target from DI by declared type name, and routing through the dispatcher keeps the graph walk in
/// one place.
/// </remarks>
internal static class WorkflowJobFactory
{
    private static readonly string DispatcherTypeName = TypeNameFormatter.Format(typeof(IWorkflowDispatcher));
    private static readonly string GuidType = TypeNameFormatter.Format(typeof(Guid));
    private static readonly string StringType = TypeNameFormatter.Format(typeof(string));
    private static readonly string IntType = TypeNameFormatter.Format(typeof(int));
    private static readonly string TokenType = TypeNameFormatter.Format(typeof(CancellationToken));

    public static JobRecord Create(
        WorkflowInstanceRecord instance, string nodeId, string? joinKey, int loopIndex, TimeSpan? delay,
        MillraceOptions options, TimeProvider time)
        => Build(
            instance, nodeId, delay, options, time,
            nameof(IWorkflowDispatcher.ExecuteAsync),
            [GuidType, StringType, StringType, IntType, TokenType],
            [
                Json(instance.Id.Value, options),
                Json(nodeId, options),
                Json(joinKey, options),
                Json(loopIndex, options),
                null,
            ]);

    public static JobRecord CreateSignalDelivery(
        WorkflowInstanceRecord instance, string nodeId, string signalName, string correlationId,
        string? payloadJson, MillraceOptions options, TimeProvider time)
        => Build(
            instance, nodeId, delay: null, options, time,
            nameof(IWorkflowDispatcher.DeliverSignalAsync),
            [GuidType, StringType, StringType, StringType, TokenType],
            [
                Json(instance.Id.Value, options),
                Json(signalName, options),
                Json(correlationId, options),
                Json(payloadJson, options),
                null,
            ]);

    public static JobRecord CreateWaitTimeout(
        WorkflowInstanceRecord instance, string nodeId, string signalName, string correlationId,
        TimeSpan timeout, MillraceOptions options, TimeProvider time)
        => Build(
            instance, nodeId, timeout, options, time,
            nameof(IWorkflowDispatcher.TimeoutSignalAsync),
            [GuidType, StringType, StringType, TokenType],
            [
                Json(instance.Id.Value, options),
                Json(signalName, options),
                Json(correlationId, options),
                null,
            ]);

    public static JobRecord CreateFailure(
        JobRecord failed, WorkflowInstanceId instanceId, string nodeId, MillraceOptions options, TimeProvider time)
        => Build(
            instanceId, failed.TenantId, nodeId, delay: null, options, time,
            nameof(IWorkflowDispatcher.FailActivityAsync),
            [GuidType, StringType, TokenType],
            [Json(instanceId.Value, options), Json(nodeId, options), null]);

    public static JobRecord CreateCompensation(
        WorkflowInstanceRecord instance, string sagaId, string stepNodeId,
        MillraceOptions options, TimeProvider time)
        => Build(
            instance.Id, instance.TenantId, stepNodeId, delay: null, options, time,
            nameof(IWorkflowDispatcher.CompensateAsync),
            [GuidType, StringType, StringType, TokenType],
            [Json(instance.Id.Value, options), Json(sagaId, options), Json(stepNodeId, options), null]);

    private static string Json<T>(T value, MillraceOptions options)
        => JsonSerializer.Serialize(value, options.SerializerOptions);

    private static JobRecord Build(
        WorkflowInstanceRecord instance, string nodeId, TimeSpan? delay, MillraceOptions options,
        TimeProvider time, string method, IReadOnlyList<string> parameterTypes,
        IReadOnlyList<string?> argumentsJson)
        => Build(
            instance.Id, instance.TenantId, nodeId, delay, options, time, method, parameterTypes, argumentsJson);

    /// <summary>
    /// The id-and-tenant form, for the failure notification: it is built from a dead job rather than
    /// from an instance record, because the worker has the job and not the instance.
    /// </summary>
    private static JobRecord Build(
        WorkflowInstanceId instanceId, string? tenantId, string nodeId, TimeSpan? delay,
        MillraceOptions options, TimeProvider time, string method, IReadOnlyList<string> parameterTypes,
        IReadOnlyList<string?> argumentsJson)
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
                MethodName = method,
                ParameterTypes = parameterTypes,
                ArgumentsJson = argumentsJson,
            },
            Retry = options.DefaultRetry,
            CreatedAt = now,
            TenantId = tenantId,
            // Correlation the dashboard reads: every workflow job names its instance and node.
            WorkflowInstanceId = instanceId,
            ActivityNodeId = nodeId,
        };
    }
}
