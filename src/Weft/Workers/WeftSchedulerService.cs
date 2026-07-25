using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Weft.Scheduling;
using Weft.Storage;

namespace Weft.Workers;

/// <summary>
/// The opportunistic scheduler role (ARCHITECTURE.md §5.3): every node periodically activates
/// due jobs and fires due recurring definitions. All operations are atomic or fenced, so no
/// leader election is needed — concurrent passes on many nodes are safe.
/// </summary>
internal sealed class WeftSchedulerService(
    IJobStorage storage,
    TimeProvider time,
    IOptions<WeftOptions> options,
    ILogger<WeftSchedulerService> logger) : BackgroundService
{
    private readonly WeftOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.SchedulerEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(_options.SchedulerInterval, time);
        while (true)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    return;
                }

                await RunPassAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Scheduler pass failed; next pass continues normally.");
            }
        }
    }

    private async Task RunPassAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();

        await storage.ActivateDueJobsAsync(now, _options.ActivationBatchSize, ct).ConfigureAwait(false);

        var due = await storage.GetDueRecurringAsync(now, _options.ActivationBatchSize, ct).ConfigureAwait(false);
        foreach (var record in due)
        {
            if (!CronExpression.TryParse(record.Cron, out var cron))
            {
                logger.LogError(
                    "Recurring job '{RecurringId}' has an unparseable cron '{Cron}'; skipping.",
                    record.Id, record.Cron);
                continue;
            }

            // Missed occurrences are skipped: fire once now, schedule from now. A cron with no
            // future occurrence still fires its due one, then parks effectively forever.
            var next = cron!.GetNextOccurrence(now) ?? DateTimeOffset.MaxValue;
            var job = new JobRecord
            {
                Id = JobId.New(time),
                Queue = record.Queue,
                Invocation = record.Invocation,
                State = JobState.Enqueued,
                Priority = record.Priority,
                CreatedAt = now,
                Retry = record.Retry,
                TenantId = record.TenantId,
            };

            // Losing the CAS is the normal multi-node outcome — another node fired it.
            await storage.TryFireRecurringAsync(record.Id, record.NextFireTime, next, job, ct)
                .ConfigureAwait(false);
        }
    }
}
