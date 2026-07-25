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

    /// <summary>
    /// Delivers a typed signal to whichever instance is waiting on
    /// <paramref name="name"/> and <paramref name="correlationId"/>, and returns whether one was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The payload type is declared by the definition, so a mismatch is a compile-time error rather
    /// than a runtime surprise. It travels as JSON, which keeps webhook and cross-language senders
    /// possible — the typed binder is applied on the engine's side of that boundary.
    /// </para>
    /// <para>
    /// Delivery is at-most-once: the bookmark is consumed atomically, so two concurrent senders
    /// cannot both resume the same wait, and the second gets <see langword="false"/>.
    /// </para>
    /// </remarks>
    ValueTask<bool> SignalAsync<TPayload>(
        string name, string correlationId, TPayload payload, CancellationToken ct = default);

    /// <summary>
    /// Delivers a signal whose payload is already JSON — the escape hatch for webhooks and senders
    /// outside this process (§11.5).
    /// </summary>
    ValueTask<bool> SignalAsync(
        string name, string correlationId, string? payloadJson, CancellationToken ct = default);
}
