using System.Text.Json;
using Microsoft.Extensions.Options;
using Millrace.Storage;

namespace Millrace.Workflows;

/// <inheritdoc cref="IWorkflowClient"/>
internal sealed class WorkflowClient(
    IWorkflowStorage workflows,
    IJobStorage jobs,
    WorkflowRegistry registry,
    Tenancy.ITenantContextAccessor tenants,
    TimeProvider time,
    IOptions<MillraceOptions> options) : IWorkflowClient
{
    private readonly MillraceOptions _options = options.Value;

    public ValueTask<WorkflowInstanceId> StartAsync<TData>(
        string definitionId, TData data, CancellationToken ct = default)
    {
        var definition = registry.GetLatest(definitionId)
            ?? throw new InvalidOperationException(
                $"No workflow '{definitionId}' is registered. Register it with AddWorkflow<T>().");

        return StartCoreAsync(definition, data, ct);
    }

    public ValueTask<WorkflowInstanceId> StartAsync<TData>(
        string definitionId, int version, TData data, CancellationToken ct = default)
    {
        if (!registry.TryGet(definitionId, version, out var definition))
        {
            throw new InvalidOperationException(
                $"Workflow '{definitionId}' version {version} is not registered.");
        }

        return StartCoreAsync(definition, data, ct);
    }

    public async ValueTask<TData?> GetDataAsync<TData>(WorkflowInstanceId id, CancellationToken ct = default)
    {
        var instance = await workflows.GetInstanceAsync(id, ct).ConfigureAwait(false);
        return instance is null
            ? default
            : JsonSerializer.Deserialize<TData>(instance.DataJson, _options.SerializerOptions);
    }

    private async ValueTask<WorkflowInstanceId> StartCoreAsync<TData>(
        WorkflowDefinition definition, TData data, CancellationToken ct)
    {
        if (definition.DataType != typeof(TData))
        {
            throw new ArgumentException(
                $"Workflow '{definition.Id}' takes {definition.DataType.Name}, not {typeof(TData).Name}.",
                nameof(data));
        }

        var start = definition.Graph.Start
            ?? throw new InvalidOperationException(
                $"Workflow '{definition.Id}' has no start node, which registration should have rejected.");

        var now = time.GetUtcNow();
        var instance = new WorkflowInstanceRecord
        {
            Id = WorkflowInstanceId.New(time),
            DefinitionId = definition.Id,
            DefinitionVersion = definition.Version,
            State = WorkflowInstanceState.Running,
            DataJson = JsonSerializer.Serialize(data, _options.SerializerOptions),
            CursorJson = JsonSerializer.Serialize(new WorkflowCursor(), _options.SerializerOptions),
            Revision = 1,
            TenantId = tenants.TenantId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await workflows.CreateInstanceAsync(instance, ct).ConfigureAwait(false);

        // The instance exists before its first job, so a crash between the two leaves an instance
        // that has not started rather than a job with nothing to advance. The reverse ordering
        // would dispatch an activity against an instance that does not exist yet.
        var startNode = definition.Graph.Nodes.First(n => n.Id == start);
        var first = WorkflowJobFactory.Create(
            instance, start, joinKey: null, loopIndex: 0,
            delay: startNode.Kind == WorkflowNodeKind.Delay ? startNode.Delay : null,
            _options, time);

        await jobs.EnqueueAsync([first], ct).ConfigureAwait(false);
        return instance.Id;
    }
}
