using Microsoft.Extensions.Logging;
using Millrace.Invocations;
using Millrace.Storage;
using Millrace.Workflows;

namespace Millrace.Workers;

/// <summary>
/// The rules deciding what transition a job gets when it finishes.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the worker pool and <c>Millrace.Testing</c>'s harness, which both run jobs. They were
/// independent implementations of the same rules, and nothing checked that they agreed — so a
/// consumer's test could stay green while production behaviour moved underneath it. A test that
/// lies is worse than no test, and that is the failure mode this file exists to remove.
/// </para>
/// <para>
/// Only the <em>decision</em> lives here. Leases, heartbeats, shutdown and the drain loop stay with
/// their callers, because those genuinely differ: the worker owns a lease and the harness does not.
/// </para>
/// </remarks>
internal static class JobOutcomes
{
    /// <summary>
    /// Whether a claim should dead-letter the job without executing it.
    /// </summary>
    /// <remarks>
    /// Claims started minus failures recorded is the count of executions that vanished without a
    /// verdict — crashes. Past the limit the job is presumed to kill whatever runs it, so running it
    /// again just takes another worker down.
    /// </remarks>
    public static bool IsPoisoned(JobRecord job, int interruptionLimit)
        => job.Attempt - job.Failures > interruptionLimit;

    public static string PoisonReason(JobRecord job)
        => $"Poison-pill: claimed {job.Attempt} times with only {job.Failures} recorded "
           + "failures — presumed to crash workers.";

    /// <summary>The transition for a job that ran to completion.</summary>
    public static JobTransition Succeeded(
        JobRecord job, string workerId, DateTimeOffset now, JobSideEffects? effects) => new()
    {
        JobId = job.Id,
        ExpectedWorkerId = workerId,
        ExpectedAttempt = job.Attempt,
        TargetState = JobState.Succeeded,
        Failures = job.Failures,
        FinishedAt = now,
        ActivateContinuations = true,
        Enqueue = effects is null ? [] : effects.Enqueue,
        Bookmarks = effects is null ? [] : effects.Bookmarks,
        Checkpoint = effects?.Checkpoint,
    };

    /// <summary>
    /// The transition for a job that threw: another retry, or dead-lettered.
    /// </summary>
    /// <param name="failureEffects">
    /// Work contributed by failure observers, which only applies when the job is actually dying —
    /// a job with retries left has not failed yet, and telling a saga to compensate would be wrong.
    /// </param>
    public static JobTransition Failed(
        JobRecord job, string workerId, DateTimeOffset now, Exception cause,
        IReadOnlyList<JobRecord> failureEffects)
    {
        var failures = job.Failures + 1;
        var delay = job.Retry.NextDelay(failures);

        return new JobTransition
        {
            JobId = job.Id,
            ExpectedWorkerId = workerId,
            ExpectedAttempt = job.Attempt,
            TargetState = delay is null ? JobState.Dead : JobState.Failed,
            Failures = failures,
            Error = Truncate(cause.ToString()),
            DueAt = delay is null ? null : now + delay,
            FinishedAt = delay is null ? now : null,
            CancelContinuations = delay is null,
            Enqueue = delay is null ? failureEffects : [],
        };
    }

    /// <summary>
    /// Collects what failure observers want committed with a dead-lettered job's transition.
    /// </summary>
    /// <remarks>
    /// An observer that throws must not stop the job being dead-lettered: the transition is the
    /// important part, and a lost notification is recoverable where a job stuck in Processing is not.
    /// </remarks>
    public static IReadOnlyList<JobRecord> FailureEffects(
        IEnumerable<IJobFailureObserver> observers, JobRecord job, ILogger? logger)
    {
        List<JobRecord>? records = null;

        foreach (var observer in observers)
        {
            try
            {
                var contributed = observer.OnDeadLettered(job);
                if (contributed.Count == 0)
                {
                    continue;
                }

                records ??= [];
                records.AddRange(contributed);
            }
            catch (Exception e)
            {
                logger?.LogError(
                    e, "Failure observer {Observer} threw for job {JobId}; dead-lettering anyway.",
                    observer.GetType().Name, job.Id);
            }
        }

        return records is null ? [] : records;
    }

    /// <summary>
    /// Caps a stored error, so one pathological stack trace cannot bloat a row unboundedly.
    /// </summary>
    public static string Truncate(string text) => text.Length <= 8192 ? text : text[..8192];

    /// <summary>The transition for a job dead-lettered without executing.</summary>
    public static JobTransition Poisoned(
        JobRecord job, string workerId, DateTimeOffset now, IReadOnlyList<JobRecord> failureEffects) => new()
    {
        JobId = job.Id,
        ExpectedWorkerId = workerId,
        ExpectedAttempt = job.Attempt,
        TargetState = JobState.Dead,
        Failures = job.Failures,
        Error = PoisonReason(job),
        FinishedAt = now,
        CancelContinuations = true,
        Enqueue = failureEffects,
    };
}
