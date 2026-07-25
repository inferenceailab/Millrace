namespace Weft.Storage;

/// <summary>
/// A recurring (cron) job definition. Firing is fenced by compare-and-set on
/// <see cref="NextFireTime"/> with the fired job inserted in the same atomic operation, giving
/// exactly-once enqueue per occurrence (see <see cref="IJobStorage.TryFireRecurringAsync"/>).
/// </summary>
public sealed record RecurringJobRecord
{
    /// <summary>Consumer-chosen identity; upserts with the same id update the definition.</summary>
    public required string Id { get; init; }

    /// <summary>Five-field cron expression, UTC (see <c>Weft.Scheduling.CronExpression</c>).</summary>
    public required string Cron { get; init; }

    public required string Queue { get; init; }

    public required JobInvocation Invocation { get; init; }

    public required Retry Retry { get; init; }

    /// <summary>Copied onto every fired job.</summary>
    public int Priority { get; init; }

    public string? TenantId { get; init; }

    public required DateTimeOffset NextFireTime { get; init; }

    public DateTimeOffset? LastFireTime { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}
