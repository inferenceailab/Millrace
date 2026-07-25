using Millrace.Storage;

namespace Millrace.Invocations;

/// <summary>
/// Effects a running job asks to have committed <em>with its own completion</em>.
/// </summary>
/// <remarks>
/// <para>
/// Scoped to one job execution. The workflow engine writes to it while an activity runs; the worker
/// folds it into the terminal transition, so the instance checkpoint, the next activity's enqueue
/// and this job's completion land in one atom (§4.2 clause 7, §11.16).
/// </para>
/// <para>
/// This exists because the alternative — the engine applying its own storage calls around the job —
/// is precisely the split that cannot be made atomic. A handler cannot commit anything itself; it
/// can only describe what should commit alongside it.
/// </para>
/// </remarks>
public sealed class JobSideEffects
{
    /// <summary>Workflow instance update to apply in the same transaction, if any.</summary>
    public WorkflowCheckpoint? Checkpoint { get; set; }

    /// <summary>Jobs to insert in the same transaction.</summary>
    public List<JobRecord> Enqueue { get; } = [];

    /// <summary>
    /// Recomputes <see cref="Checkpoint"/> against the current stored state, after another writer
    /// won the revision race.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Supplied by the workflow engine. Without it a losing branch could only reach its merge again
    /// by failing and being retried, which re-executes the activity — the thing §6.2 explicitly says
    /// must not happen. With it the worker re-merges and re-applies, and the activity runs once.
    /// </para>
    /// <para>
    /// Returns false when the conflict is not recoverable — the instance vanished, or its revision
    /// moved somewhere the merge cannot be rebased onto — in which case the job fails normally.
    /// </para>
    /// </remarks>
    public Func<CancellationToken, ValueTask<bool>>? Remerge { get; set; }

    internal bool IsEmpty => Checkpoint is null && Enqueue.Count == 0;
}
