namespace Millrace.Workflows;

/// <summary>
/// Starts and inspects workflow instances (ARCHITECTURE.md §6).
/// </summary>
public interface IWorkflowClient
{
    /// <summary>
    /// Starts an instance of the latest registered version of <paramref name="definitionId"/>.
    /// </summary>
    /// <remarks>
    /// The instance record and the first activity job are created together, so an instance can
    /// never exist with nothing scheduled to advance it.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No such definition is registered.</exception>
    ValueTask<WorkflowInstanceId> StartAsync<TData>(
        string definitionId, TData data, CancellationToken ct = default);

    /// <summary>
    /// Starts an instance pinned to a specific version, for a caller that must not drift onto a
    /// newer definition mid-rollout.
    /// </summary>
    ValueTask<WorkflowInstanceId> StartAsync<TData>(
        string definitionId, int version, TData data, CancellationToken ct = default);

    /// <summary>Reads an instance's current data document, or null if there is no such instance.</summary>
    ValueTask<TData?> GetDataAsync<TData>(WorkflowInstanceId id, CancellationToken ct = default);
}
