using Millrace.Storage;

namespace Millrace.Workflows;

/// <summary>
/// Lets a layer above react when the substrate dead-letters a job, by contributing work that
/// commits with that job's own terminal transition.
/// </summary>
/// <remarks>
/// <para>
/// A saga compensates when an activity fails <em>past its retry policy</em> — but at that moment
/// nothing of the engine is running: the activity threw, and the worker dead-letters the job on its
/// own. Without this the workflow would never learn that one of its steps died, and the instance
/// would sit in <c>Running</c> forever with no failure recorded anywhere.
/// </para>
/// <para>
/// The observer returns records rather than writing anything, for the same reason the dispatcher
/// does: a notification inserted separately could be lost while the job still went Dead, which is
/// precisely the half-state the atomic transition exists to prevent.
/// </para>
/// </remarks>
public interface IJobFailureObserver
{
    /// <summary>
    /// Jobs to insert atomically with <paramref name="job"/>'s dead-letter transition. Empty when
    /// this observer has nothing to say about the job.
    /// </summary>
    IReadOnlyList<JobRecord> OnDeadLettered(JobRecord job);
}
