namespace Millrace.Storage.Monitoring;

/// <summary>
/// Everything the dashboard shows for a single job — the summary plus the fields too heavy or too
/// sensitive for a list view.
/// </summary>
/// <remarks>
/// Carries both the counters (<see cref="JobRecord.Attempt"/>, <see cref="JobRecord.Failures"/>,
/// and interruptions derived as their difference) and the per-attempt timeline in
/// <see cref="Attempts"/>. The counters remain the summary answer to "is this job failing or is
/// infrastructure killing it"; the timeline answers "and what happened each time".
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

    /// <inheritdoc cref="JobRecord.Retry"/>
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

    /// <summary>
    /// Executions that failed or were interrupted, newest first (§11.27).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Successful attempts are not here</b>, by design: a job that succeeds first time records
    /// nothing, so a healthy queue pays nothing for this. An empty list therefore means "nothing has
    /// gone wrong", not "no history kept".
    /// </para>
    /// <para>
    /// Bounded per job, so a job that fails thousands of times keeps only its most recent attempts.
    /// <see cref="JobSummary.Attempt"/> and <see cref="JobSummary.Failures"/> stay exact regardless —
    /// the counters are on the job row and are never pruned, so a truncated timeline can never
    /// understate how often something failed.
    /// </para>
    /// </remarks>
    public IReadOnlyList<JobAttempt> Attempts { get; init; } = [];

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
