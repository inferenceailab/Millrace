namespace Millrace.Storage.Monitoring;

/// <summary>
/// A recurring definition as shown in the schedule view.
/// </summary>
/// <remarks>
/// <b>There is no last outcome.</b> <see cref="LastFireTime"/> records <em>when</em> a definition
/// last fired, not what happened: nothing links a fired job back to the definition that produced
/// it, so the outcome cannot be derived in either direction. Adding that link changes the frozen
/// job schema and is tracked separately. What this type does answer — is the schedule live, and is
/// it on time — is the question the view is opened for.
/// </remarks>
public sealed record RecurringSummary
{
    /// <summary>Consumer-chosen identity.</summary>
    public required string Id { get; init; }

    /// <summary>Five-field cron expression, UTC.</summary>
    public required string Cron { get; init; }

    public required string Queue { get; init; }

    /// <summary>Declared service type from the captured invocation, for display.</summary>
    public required string TypeName { get; init; }

    /// <summary>Method name from the captured invocation, for display.</summary>
    public required string MethodName { get; init; }

    /// <summary>Copied onto every fired job.</summary>
    public int Priority { get; init; }

    public string? TenantId { get; init; }

    /// <summary>
    /// When this definition fires next, UTC. Already in the past means the scheduler is behind, or
    /// nothing is running the scheduler role.
    /// </summary>
    public required DateTimeOffset NextFireTime { get; init; }

    /// <summary>When it last fired; null if it never has.</summary>
    public DateTimeOffset? LastFireTime { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}
