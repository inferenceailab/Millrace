namespace Millrace.Storage.Monitoring;

/// <summary>
/// Aggregate counts for the dashboard overview.
/// </summary>
/// <remarks>
/// This is the <em>only</em> place counts come from. §11.12 removed totals from list responses
/// because counting a filtered, continuously-changing job table is the expensive part of the query;
/// concentrating aggregates here lets a provider maintain or approximate them cheaply instead of
/// paying for a count on every page render.
/// </remarks>
public sealed record JobStatistics
{
    /// <summary>
    /// Job count per state. Providers MUST include an entry for every <see cref="JobState"/>,
    /// using zero rather than omission, so callers never distinguish "none" from "not reported".
    /// </summary>
    public required IReadOnlyDictionary<JobState, long> JobsByState { get; init; }

    /// <summary>
    /// Claimable depth per queue: <see cref="JobState.Enqueued"/> jobs only. Queues with no
    /// claimable work MAY be omitted — a queue is not a persistent entity, so there is no complete
    /// set to enumerate.
    /// </summary>
    public required IReadOnlyDictionary<string, long> EnqueuedByQueue { get; init; }

    /// <summary>Workflow instance count per state, with the same completeness rule as
    /// <see cref="JobsByState"/>.</summary>
    public required IReadOnlyDictionary<WorkflowInstanceState, long> InstancesByState { get; init; }

    /// <summary>Number of recurring definitions registered.</summary>
    public required long RecurringDefinitions { get; init; }

    /// <summary>
    /// Recurring definitions whose next fire time is already in the past — the scheduler is behind,
    /// or nothing is running the scheduler role at all.
    /// </summary>
    public required long OverdueRecurringDefinitions { get; init; }
}
