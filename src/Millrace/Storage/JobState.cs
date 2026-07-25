namespace Millrace.Storage;

/// <summary>Lifecycle states of a job (ARCHITECTURE.md §5.1).</summary>
public enum JobState
{
    /// <summary>Waiting for <see cref="JobRecord.DueAt"/>; activated by <c>ActivateDueJobsAsync</c>.</summary>
    Scheduled = 0,

    /// <summary>Claimable by workers.</summary>
    Enqueued = 1,

    /// <summary>Claimed under a lease (<see cref="JobRecord.WorkerId"/> / <see cref="JobRecord.LeaseUntil"/>).</summary>
    Processing = 2,

    /// <summary>Completed successfully. Terminal.</summary>
    Succeeded = 3,

    /// <summary>A failed attempt awaiting its retry delay; <see cref="JobRecord.DueAt"/> is the activation time.</summary>
    Failed = 4,

    /// <summary>Retries exhausted (or poison-pilled). Terminal.</summary>
    Dead = 5,

    /// <summary>Cancelled before or during execution. Terminal.</summary>
    Cancelled = 6,

    /// <summary>A continuation parked until its <see cref="JobRecord.ParentId"/> reaches a terminal state.</summary>
    Awaiting = 7,
}

/// <summary>Helpers over <see cref="JobState"/>.</summary>
public static class JobStateExtensions
{
    /// <summary>Terminal states release idempotency keys and never transition again.</summary>
    public static bool IsTerminal(this JobState state)
        => state is JobState.Succeeded or JobState.Dead or JobState.Cancelled;
}
