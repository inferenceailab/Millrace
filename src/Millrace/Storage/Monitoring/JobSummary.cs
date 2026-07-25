namespace Millrace.Storage.Monitoring;

/// <summary>
/// A job as shown in a list view.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately excludes the serialized arguments.</b> <see cref="JobInvocation.ArgumentsJson"/>
/// routinely carries personal data, and a list view would ship it for every row on every page to
/// render columns that never display it. The type and method names are enough to identify a job in
/// a list; the arguments appear only in <see cref="JobDetails"/>, which is a per-job read an
/// operator asks for explicitly.
/// </para>
/// <para>
/// Field meanings are those of <see cref="JobRecord"/>. In particular <see cref="Attempt"/> counts
/// executions <em>started</em> and <see cref="Failures"/> counts recorded failures — see
/// <see cref="Interruptions"/>.
/// </para>
/// </remarks>
public sealed record JobSummary
{
    public required JobId Id { get; init; }

    public required string Queue { get; init; }

    public required JobState State { get; init; }

    /// <summary>Declared service type from the captured invocation, for display.</summary>
    public required string TypeName { get; init; }

    /// <summary>Method name from the captured invocation, for display.</summary>
    public required string MethodName { get; init; }

    public int Priority { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Activation time while <see cref="JobState.Scheduled"/> or <see cref="JobState.Failed"/>.</summary>
    public DateTimeOffset? DueAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>Executions started, including lease-expiry reclaims.</summary>
    public int Attempt { get; init; }

    /// <summary>Recorded failures. Retry budget consumes this, never <see cref="Attempt"/>.</summary>
    public int Failures { get; init; }

    /// <summary>
    /// Executions that started but neither succeeded nor recorded a failure — crashes, deploys and
    /// lost leases.
    /// </summary>
    /// <remarks>
    /// Derived from the §11.8 attempt/failure split rather than stored. A job with a high
    /// interruption count and no failures is being killed by infrastructure, not by its own code —
    /// a distinction an operator otherwise has to infer from logs. Note this is a count, not a
    /// timeline: per-attempt history is not persisted by the 0.1 schema.
    /// </remarks>
    public int Interruptions => Attempt - Failures;

    public string? TenantId { get; init; }

    /// <summary>Owning worker while <see cref="JobState.Processing"/>.</summary>
    public string? WorkerId { get; init; }
}
