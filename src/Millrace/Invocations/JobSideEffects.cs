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

    internal bool IsEmpty => Checkpoint is null && Enqueue.Count == 0;
}
