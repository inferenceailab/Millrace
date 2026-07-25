namespace Millrace.Storage;

/// <summary>How an execution ended, for the attempts that did not simply succeed.</summary>
public enum JobAttemptOutcome
{
    /// <summary>The job threw and the failure was recorded. Consumes retry budget.</summary>
    Failed = 0,

    /// <summary>
    /// The execution ended without a verdict — a crash, a deploy, a lost lease, a graceful
    /// shutdown. Consumes no retry budget (§11.8).
    /// </summary>
    Interrupted = 1,
}

/// <summary>
/// One execution of a job that did not succeed (§11.27).
/// </summary>
/// <remarks>
/// <para>
/// <b>Only attempts worth explaining are recorded.</b> A job that succeeds on its first attempt
/// writes no row at all, so the common path costs nothing and the table is bounded by failure
/// volume rather than by throughput. The successful attempt is already fully described by the job
/// row itself — its worker, its finish time, its state — so a row for it would be duplication that
/// every healthy queue pays for.
/// </para>
/// <para>
/// This is the timeline §7 promised and the 0.1 schema could not source. It reports when each
/// unsuccessful attempt <em>ended</em>, on which worker, and why; it does not report when the
/// attempt started, because nothing records that and adding it would mean widening the claim path
/// for a nicety.
/// </para>
/// </remarks>
public sealed record JobAttempt
{
    /// <summary>The attempt number, matching <see cref="JobRecord.Attempt"/> at the time.</summary>
    public required int Attempt { get; init; }

    public required JobAttemptOutcome Outcome { get; init; }

    /// <summary>When the attempt ended.</summary>
    public required DateTimeOffset RecordedAt { get; init; }

    /// <summary>The worker that held the lease, where known.</summary>
    public string? WorkerId { get; init; }

    /// <summary>
    /// The exception, for <see cref="JobAttemptOutcome.Failed"/>. Always null for an interruption —
    /// an execution that vanished had nothing to report, and that absence is the diagnosis.
    /// </summary>
    public string? Error { get; init; }
}
