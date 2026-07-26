namespace Millrace.Storage.Monitoring;

/// <summary>
/// A recurring definition as shown in the schedule view.
/// </summary>
/// <remarks>
/// Answers both questions an operator opens the view for: <em>is this schedule live and on time</em>
/// (<see cref="NextFireTime"/>, and whether it is already past), and <em>did the last run work</em>
/// (<see cref="LastOutcome"/>, via <see cref="JobRecord.RecurringId"/> — §11.26).
/// </remarks>
public sealed record RecurringSummary
{
    /// <summary>Consumer-chosen identity.</summary>
    public required string Id { get; init; }

    /// <summary>Five-field cron expression, UTC.</summary>
    public required string Cron { get; init; }

    /// <inheritdoc cref="RecurringJobRecord.Queue"/>
    public required string Queue { get; init; }

    /// <summary>Declared service type from the captured invocation, for display.</summary>
    public required string TypeName { get; init; }

    /// <summary>Method name from the captured invocation, for display.</summary>
    public required string MethodName { get; init; }

    /// <summary>Copied onto every fired job.</summary>
    public int Priority { get; init; }

    /// <inheritdoc cref="RecurringJobRecord.TenantId"/>
    public string? TenantId { get; init; }

    /// <summary>
    /// When this definition fires next, UTC. Already in the past means the scheduler is behind, or
    /// nothing is running the scheduler role.
    /// </summary>
    public required DateTimeOffset NextFireTime { get; init; }

    /// <summary>When it last fired; null if it never has.</summary>
    public DateTimeOffset? LastFireTime { get; init; }

    /// <summary>
    /// What became of the most recently created job this definition produced, or null if it has
    /// produced none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <em>most recently created</em> job, not the most recently finished: an occurrence still
    /// running should read <see cref="JobState.Processing"/> rather than showing last night's
    /// success and implying the current run is fine.
    /// </para>
    /// <para>
    /// Null is not "unknown" — it means this definition has never produced a job, which for a
    /// definition whose <see cref="NextFireTime"/> is long past is itself the answer.
    /// </para>
    /// </remarks>
    public JobState? LastOutcome { get; init; }

    /// <summary>The job behind <see cref="LastOutcome"/>, so the view can link to its error.</summary>
    public JobId? LastJobId { get; init; }

    /// <inheritdoc cref="RecurringJobRecord.CreatedAt"/>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <inheritdoc cref="RecurringJobRecord.UpdatedAt"/>
    public required DateTimeOffset UpdatedAt { get; init; }
}
