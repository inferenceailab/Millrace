namespace Millrace.Storage;

/// <summary>
/// A workflow instance update carried inside a <see cref="JobTransition"/>, so the checkpoint
/// commits in the same transaction as the activity job's own transition.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> §6.2 makes the activity the unit of at-least-once execution and the
/// checkpoint the unit of exactly-once <em>progress</em>. Two separate calls cannot deliver that,
/// in either order: checkpoint-then-transition lets a crash re-run an activity from a cursor that
/// already moved past it, and transition-then-checkpoint lets a crash strand the instance forever
/// with no failure recorded. The coupling has to be expressible in one atom, which is what this is.
/// </para>
/// <para>
/// It is the direct analogue of <see cref="JobTransition.Enqueue"/>: the engine computes a command
/// and the provider applies it all-or-nothing, so no engine logic — cursor arithmetic, merge
/// policy, graph traversal — moves into a provider (§4 P2).
/// </para>
/// <para>
/// <b>Conflict signalling.</b> A stale <see cref="ExpectedRevision"/> throws
/// <see cref="MillraceConcurrencyException"/> rather than returning false, because the caller must
/// react: reload the instance and retry the merge. A fence rejection still returns false, because
/// there the loser simply drops. Keeping those two outcomes distinguishable is why the checkpoint
/// does not reuse the boolean.
/// </para>
/// </remarks>
public sealed record WorkflowCheckpoint
{
    /// <summary>
    /// The instance as it should be stored. The provider writes it with
    /// <c>Revision = ExpectedRevision + 1</c>, exactly as <c>UpdateInstanceAsync</c> does.
    /// </summary>
    public required WorkflowInstanceRecord Instance { get; init; }

    /// <summary>
    /// The revision the engine read. The update applies only if the stored revision still matches.
    /// </summary>
    public required long ExpectedRevision { get; init; }
}
