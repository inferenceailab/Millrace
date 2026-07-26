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
    /// <inheritdoc cref="JobRecord.Id"/>
    public required JobId Id { get; init; }

    /// <inheritdoc cref="JobRecord.Queue"/>
    public required string Queue { get; init; }

    /// <inheritdoc cref="JobRecord.State"/>
    public required JobState State { get; init; }

    /// <summary>Declared service type from the captured invocation, for display.</summary>
    public required string TypeName { get; init; }

    /// <summary>Method name from the captured invocation, for display.</summary>
    public required string MethodName { get; init; }

    /// <inheritdoc cref="JobRecord.Priority"/>
    public int Priority { get; init; }

    /// <inheritdoc cref="JobRecord.CreatedAt"/>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Activation time while <see cref="JobState.Scheduled"/> or <see cref="JobState.Failed"/>.</summary>
    public DateTimeOffset? DueAt { get; init; }

    /// <inheritdoc cref="JobRecord.FinishedAt"/>
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
    /// <para>
    /// Derived from the §11.8 attempt/failure split rather than stored. A job with a high
    /// interruption count and no failures is being killed by infrastructure, not by its own code —
    /// a distinction an operator otherwise has to infer from logs.
    /// </para>
    /// <para>
    /// <b>It counts the attempt in flight.</b> A job claimed and still running reports one
    /// interruption it has not had yet, because this is arithmetic over two counters and cannot
    /// tell a live lease from an expired one without a clock. Distinguishing them needs
    /// <see cref="JobRecord.LeaseUntil"/> and the current time, which a derived property does not
    /// have — and pushing that into every provider would trade a documented approximation for three
    /// chances to disagree.
    /// </para>
    /// <para>
    /// So this stays the cheap summary — <em>is this job failing, or is infrastructure killing
    /// it</em> — and <see cref="JobDetails.Attempts"/> is the exact answer, holding a row only for
    /// executions that actually ended (§11.27).
    /// </para>
    /// </remarks>
    public int Interruptions => Attempt - Failures;

    /// <inheritdoc cref="JobRecord.TenantId"/>
    public string? TenantId { get; init; }

    /// <summary>Owning worker while <see cref="JobState.Processing"/>.</summary>
    public string? WorkerId { get; init; }
}
