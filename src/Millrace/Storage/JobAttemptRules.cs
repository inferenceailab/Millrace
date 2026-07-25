namespace Millrace.Storage;

/// <summary>
/// Which transitions end an attempt worth recording (§11.27).
/// </summary>
/// <remarks>
/// Shared by every provider for the same reason <c>JobOutcomes</c> is shared by the worker and the
/// test harness: three independent copies of a four-line rule is three chances for one database's
/// timeline to disagree with another's, and the conformance kit would then be asserting a rule that
/// exists in triplicate.
/// </remarks>
public static class JobAttemptRules
{
    /// <summary>How many unsuccessful attempts are kept per job.</summary>
    /// <remarks>
    /// A cap rather than a background prune: retention that needs a sweeper is retention that
    /// silently stops working when nobody runs the sweeper. Ten is enough to see a pattern — the
    /// same error repeating, or failures alternating with interruptions — and the counters on the
    /// job row are never pruned, so a truncated timeline can never understate how often something
    /// failed.
    /// </remarks>
    public const int HistoryLimit = 10;

    /// <summary>
    /// The outcome a transition records, or null when it ends nothing worth explaining.
    /// </summary>
    /// <param name="transition">The transition being applied.</param>
    /// <param name="priorFailures">The job's failure count <em>before</em> it is applied.</param>
    /// <remarks>
    /// <para>
    /// Three transitions end an attempt without success: a recorded failure (retrying), a
    /// dead-letter that consumed the last retry, and a fenced release back to the queue on graceful
    /// shutdown. The release is an interruption rather than a failure — it consumes no retry budget
    /// (§11.8) — and recording it is what makes a rolling deploy legible in the timeline instead of
    /// looking like silence.
    /// </para>
    /// <para>
    /// Success and cancellation record nothing: the job row already describes both. Nor does a
    /// poison-pill dead-letter, which is why the prior count matters — it dies <em>without
    /// executing</em>, so there is no attempt to describe, and it is told apart from a real failure
    /// by leaving the failure count untouched.
    /// </para>
    /// </remarks>
    public static JobAttemptOutcome? OutcomeFor(JobTransition transition, int priorFailures) =>
        transition.TargetState switch
        {
            JobState.Failed => JobAttemptOutcome.Failed,
            JobState.Dead when transition.Failures > priorFailures => JobAttemptOutcome.Failed,
            JobState.Enqueued => JobAttemptOutcome.Interrupted,
            _ => null,
        };
}
