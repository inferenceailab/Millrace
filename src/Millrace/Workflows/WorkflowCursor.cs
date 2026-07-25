using System.Text.Json.Serialization;

namespace Millrace.Workflows;

/// <summary>
/// The engine's position in a graph, persisted alongside the data document.
/// </summary>
/// <remarks>
/// <para>
/// A single pointer is not enough: <see cref="WorkflowNodeKind.Parallel"/> and
/// <see cref="WorkflowNodeKind.ForEach"/> put several activities in flight at once, and the
/// sequence after them may not resume until every one has finished. So the cursor tracks open
/// <see cref="Joins"/> rather than one current node — each a countdown with the node to continue
/// from once it reaches zero.
/// </para>
/// <para>
/// Where the instance is *right now* lives in the dispatched jobs themselves, each carrying its own
/// node id. The cursor holds only what no single job can know: how many siblings are still
/// outstanding.
/// </para>
/// </remarks>
public sealed record WorkflowCursor
{
    /// <summary>Open fan-outs, keyed by the id of the node that opened them.</summary>
    public IReadOnlyDictionary<string, WorkflowJoin> Joins { get; init; } =
        new Dictionary<string, WorkflowJoin>(StringComparer.Ordinal);

    /// <summary>
    /// Open signal waits, keyed <c>signalName|correlationId</c>.
    /// </summary>
    /// <remarks>
    /// The bookmark records that <em>an</em> instance is waiting; this records <em>where</em> it will
    /// resume. Keeping it here rather than on the bookmark leaves the storage schema alone, and the
    /// two are written in the same atom anyway, so they cannot disagree.
    /// </remarks>
    public IReadOnlyDictionary<string, WorkflowWait> Waits { get; init; } =
        new Dictionary<string, WorkflowWait>(StringComparer.Ordinal);

    /// <summary>True once no activity remains in flight and no join is open.</summary>
    public bool Completed { get; init; }

    [JsonIgnore]
    public bool HasOpenJoins => Joins.Count > 0;

    /// <summary>The key a wait is stored under.</summary>
    public static string WaitKey(string signalName, string correlationId) => $"{signalName}|{correlationId}";
}

/// <summary>Where a suspended instance continues when its signal arrives, or its wait times out.</summary>
public sealed record WorkflowWait
{
    /// <summary>The <see cref="WorkflowNodeKind.WaitForSignal"/> node that is parked.</summary>
    public required string NodeId { get; init; }

    /// <summary>The fan-out this wait sits inside, if any.</summary>
    public string? JoinKey { get; init; }
}

/// <summary>One open fan-out: how many branches are still running, and where to go after.</summary>
public sealed record WorkflowJoin
{
    /// <summary>Branches not yet finished. The last one to finish continues the sequence.</summary>
    public required int Remaining { get; init; }

    /// <summary>Node to continue from when <see cref="Remaining"/> reaches zero; null ends the flow.</summary>
    public string? ContinueAt { get; init; }

    /// <summary>
    /// The join this one sits inside, if the fan-out was itself opened inside a branch.
    /// </summary>
    /// <remarks>
    /// Nested fan-outs need this: when an inner join completes and its continuation runs out, the
    /// <em>outer</em> join is what must be decremented. Without the link the outer join would never
    /// reach zero and the instance would hang with no error.
    /// </remarks>
    public string? ParentJoin { get; init; }
}
