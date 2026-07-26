namespace Millrace.Storage;

/// <summary>
/// A recurring (cron) job definition. Firing is fenced by compare-and-set on
/// <see cref="NextFireTime"/> with the fired job inserted in the same atomic operation, giving
/// exactly-once enqueue per occurrence (see <see cref="IJobStorage.TryFireRecurringAsync"/>).
/// </summary>
public sealed record RecurringJobRecord
{
    /// <summary>Consumer-chosen identity; upserts with the same id update the definition.</summary>
    public required string Id { get; init; }

    /// <summary>Five-field cron expression, UTC (see <c>Millrace.Scheduling.CronExpression</c>).</summary>
    public required string Cron { get; init; }

    /// <summary>Queue that fired occurrences are enqueued to.</summary>
    public required string Queue { get; init; }

    /// <summary>What each occurrence runs.</summary>
    /// <remarks>
    /// Copied onto the fired job, so an occurrence already enqueued keeps running the invocation
    /// captured when it fired even if the definition is edited underneath it.
    /// </remarks>
    public required JobInvocation Invocation { get; init; }

    /// <summary>Retry policy copied onto every fired occurrence.</summary>
    /// <remarks>
    /// The definition holds the policy; each occurrence carries its own copy and retries
    /// independently. One occurrence exhausting its retries says nothing about the next.
    /// </remarks>
    public required Retry Retry { get; init; }

    /// <summary>Copied onto every fired job.</summary>
    public int Priority { get; init; }

    /// <summary>Owning tenant, copied onto every fired occurrence.</summary>
    public string? TenantId { get; init; }

    /// <summary>When the next occurrence is due.</summary>
    /// <remarks>
    /// Also the fence value: firing is a compare-and-set on this field, which is what makes an
    /// occurrence enqueue exactly once no matter how many nodes notice it is due at the same
    /// moment. An upsert only moves it when the cron expression itself changed — resaving a
    /// definition with the same schedule does not rewind or skip the occurrence already pending.
    /// </remarks>
    public required DateTimeOffset NextFireTime { get; init; }

    /// <summary>The occurrence most recently fired.</summary>
    /// <remarks>
    /// The scheduled time of that occurrence, not the wall clock when firing happened — a
    /// definition that fires late records what it was due for, so a backlog cannot disguise itself
    /// as punctuality.
    /// </remarks>
    public DateTimeOffset? LastFireTime { get; init; }

    /// <summary>When the definition was first registered; preserved across upserts.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the definition was last upserted.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}
