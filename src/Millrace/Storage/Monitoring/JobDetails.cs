namespace Millrace.Storage.Monitoring;

/// <summary>
/// Everything the dashboard shows for a single job — the summary plus the fields too heavy or too
/// sensitive for a list view.
/// </summary>
/// <remarks>
/// <b>No per-attempt timeline.</b> The 0.1 schema stores counters and the most recent error
/// (<see cref="JobRecord.Attempt"/>, <see cref="JobRecord.Failures"/>,
/// <see cref="JobRecord.LastError"/>), not one row per attempt. So this type can say *how many*
/// times a job failed and *how many* times it was interrupted, and show the last exception — but
/// not when each earlier attempt ran, on which worker, or with which error. A timeline needs a
/// schema addition in every provider and is tracked separately.
/// </remarks>
public sealed record JobDetails
{
    /// <summary>The same projection a list view shows.</summary>
    public required JobSummary Summary { get; init; }

    /// <summary>
    /// The captured call, including serialized arguments — the reason this type is a deliberate
    /// per-job read rather than part of <see cref="JobSummary"/>.
    /// </summary>
    public required JobInvocation Invocation { get; init; }

    public required Retry Retry { get; init; }

    /// <summary>
    /// Retained even after a terminal transition released the key from its uniqueness scope
    /// (§11.8) — the field is never cleared.
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Parent job when this is a continuation.</summary>
    public JobId? ParentId { get; init; }

    /// <summary>The most recent recorded error; null if the job has never failed.</summary>
    public string? LastError { get; init; }

    /// <summary>Lease expiry while <see cref="JobState.Processing"/>.</summary>
    public DateTimeOffset? LeaseUntil { get; init; }

    /// <summary>
    /// Cooperative cancellation was requested while the job was processing. A completing worker may
    /// still win with a fenced terminal transition, so this does not promise cancellation.
    /// </summary>
    public bool CancelRequested { get; init; }

    /// <summary>Owning workflow instance, when the job is an activity execution.</summary>
    public WorkflowInstanceId? WorkflowInstanceId { get; init; }

    /// <summary>Graph node this job executes, when it is an activity execution.</summary>
    public string? ActivityNodeId { get; init; }
}
