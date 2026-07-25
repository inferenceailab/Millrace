using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Millrace.Invocations;
using Millrace.Storage;

namespace Millrace.Workers;

/// <summary>
/// The per-node worker pool (ARCHITECTURE.md §5.3): claims under leases, executes with bounded
/// parallelism, renews leases on a heartbeat, and shuts down in two phases (drain, then abandon
/// with a fenced release back to Enqueued so interruptions never consume retry budget).
/// </summary>
internal sealed class MillraceWorkerService(
    IJobStorage storage,
    JobExecutor executor,
    TimeProvider time,
    IOptions<MillraceOptions> options,
    ILogger<MillraceWorkerService> logger) : BackgroundService
{
    private enum CancelReason { None, LeaseLost, Superseded, CancelRequested }

    private readonly MillraceOptions _options = options.Value;
    private readonly string _workerId = $"{Environment.MachineName}:{Guid.CreateVersion7():n}";
    private readonly ConcurrentDictionary<(JobId Id, int Attempt), InFlightJob> _inFlight = new();
    private readonly CancellationTokenSource _abandon = new();
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private bool _draining;
    private Task? _heartbeat;
    private Task? _notifierPump;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.WorkerEnabled)
        {
            return;
        }

        var queues = _options.Queues.ToArray();
        _heartbeat = Task.Run(() => HeartbeatLoopAsync(stoppingToken), CancellationToken.None);

        if (storage.Capabilities.HasFlag(StorageCapabilities.Notifications)
            && storage is IStorageNotifier notifier)
        {
            var queueSet = queues.ToHashSet(StringComparer.Ordinal);
            _notifierPump = Task.Run(() => PumpNotificationsAsync(notifier, queueSet, stoppingToken), CancellationToken.None);
        }

        var pollDelay = _options.MinPollDelay;
        while (!stoppingToken.IsCancellationRequested && !Volatile.Read(ref _draining))
        {
            var free = _options.MaxParallelism - _inFlight.Count;
            if (free <= 0)
            {
                await WaitForSlotAsync(stoppingToken).ConfigureAwait(false);
                continue;
            }

            IReadOnlyList<JobRecord> batch;
            try
            {
                batch = await storage.ClaimAsync(
                    new ClaimRequest(_workerId, queues, Math.Min(free, _options.ClaimBatchSize), _options.LeaseDuration),
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Claim failed; backing off.");
                await SafeDelayAsync(pollDelay, stoppingToken).ConfigureAwait(false);
                continue;
            }

            if (batch.Count == 0)
            {
                await WaitForWorkAsync(pollDelay, stoppingToken).ConfigureAwait(false);
                pollDelay = Min(pollDelay * 2, _options.MaxPollDelay);
                continue;
            }

            pollDelay = _options.MinPollDelay;
            foreach (var job in batch)
            {
                Dispatch(job);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _draining, true);

        // Phase 1: drain — job tokens stay unsignalled, the heartbeat keeps renewing leases.
        var running = InFlightTasks();
        if (running.Length > 0)
        {
            await Task.WhenAny(Task.WhenAll(running), SafeDelayAsync(_options.ShutdownTimeout, CancellationToken.None))
                .ConfigureAwait(false);
        }

        // Phase 2: abandon — cancel job tokens; completions during grace apply their own
        // fenced release. Anything still stuck afterwards is released here; its own later
        // apply fence-rejects benignly.
        await _abandon.CancelAsync().ConfigureAwait(false);
        var remaining = InFlightTasks();
        if (remaining.Length > 0)
        {
            await Task.WhenAny(Task.WhenAll(remaining), SafeDelayAsync(_options.ShutdownGrace, CancellationToken.None))
                .ConfigureAwait(false);
        }

        foreach (var inFlight in _inFlight.Values.ToArray())
        {
            await ApplyTransitionAsync(Release(inFlight.Job)).ConfigureAwait(false);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        Task[] InFlightTasks() =>
            [.. _inFlight.Values.Select(f => f.Task).Where(t => t is not null).Cast<Task>()];
    }

    private void Dispatch(JobRecord job)
    {
        // Self-reclaim: a claim for a JobId we already track proves the older attempt's lease
        // expired — the newer claim owns the job now.
        foreach (var (key, older) in _inFlight)
        {
            if (key.Id == job.Id && key.Attempt < job.Attempt)
            {
                older.Cancel(CancelReason.Superseded);
            }
        }

        var inFlight = new InFlightJob(job, CancellationTokenSource.CreateLinkedTokenSource(_abandon.Token));
        if (!_inFlight.TryAdd((job.Id, job.Attempt), inFlight))
        {
            inFlight.Dispose();
            return;
        }

        inFlight.Task = Task.Run(() => RunJobAsync(inFlight), CancellationToken.None);
    }

    private async Task RunJobAsync(InFlightJob inFlight)
    {
        var job = inFlight.Job;
        try
        {
            if (job.CancelRequested)
            {
                await ApplyTransitionAsync(Terminal(job, JobState.Cancelled, job.Failures,
                    "Cancelled by request.", cancelContinuations: true)).ConfigureAwait(false);
                return;
            }

            if (job.Attempt - job.Failures > _options.InterruptionLimit)
            {
                await ApplyTransitionAsync(Terminal(job, JobState.Dead, job.Failures,
                    $"Poison-pill: claimed {job.Attempt} times with only {job.Failures} recorded " +
                    "failures — presumed to crash workers.", cancelContinuations: true)).ConfigureAwait(false);
                return;
            }

            try
            {
                var effects = await executor.ExecuteAsync(job, inFlight.Cts.Token).ConfigureAwait(false);
                await ApplyWithRemergeAsync(job, effects).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (inFlight.Cts.IsCancellationRequested)
            {
                if (inFlight.Reason != CancelReason.None)
                {
                    // Lease lost, superseded by our own reclaim, or cancel-requested (the
                    // heartbeat applied the Cancelled transition): drop with no transition.
                    return;
                }

                // Shutdown abandon: fenced release back to the queue — no retry budget spent.
                await ApplyTransitionAsync(Release(job)).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                var failures = job.Failures + 1;
                var error = Truncate(e.ToString());
                if (job.Retry.NextDelay(failures) is { } delay)
                {
                    await ApplyTransitionAsync(new JobTransition
                    {
                        JobId = job.Id,
                        ExpectedWorkerId = _workerId,
                        ExpectedAttempt = job.Attempt,
                        TargetState = JobState.Failed,
                        Failures = failures,
                        DueAt = time.GetUtcNow() + delay,
                        Error = error,
                    }).ConfigureAwait(false);
                }
                else
                {
                    await ApplyTransitionAsync(Terminal(job, JobState.Dead, failures, error,
                        cancelContinuations: true)).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _inFlight.TryRemove((job.Id, job.Attempt), out _);
            inFlight.Dispose();
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            await HeartbeatCoreAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // A dead heartbeat means silent lease expiry and duplicate execution — never
            // let it die without a trace.
            logger.LogCritical(e,
                "Heartbeat loop crashed; in-flight leases will expire and their jobs may re-run elsewhere.");
        }
    }

    private async Task HeartbeatCoreAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval, time);
        while (true)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Highest tracked attempt per job — older superseded attempts are excluded.
            var tracked = _inFlight.ToArray()
                .GroupBy(kv => kv.Key.Id)
                .Select(g => g.OrderByDescending(kv => kv.Key.Attempt).First())
                .ToArray();
            if (tracked.Length == 0)
            {
                continue;
            }

            IReadOnlyList<JobId> renewed;
            try
            {
                renewed = await storage.RenewLeasesAsync(
                    _workerId, [.. tracked.Select(kv => kv.Key.Id)], _options.LeaseDuration, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Lease renewal failed; will retry next heartbeat.");
                continue;
            }

            var renewedSet = renewed.ToHashSet();
            foreach (var (key, inFlight) in tracked)
            {
                if (renewedSet.Contains(key.Id))
                {
                    continue;
                }

                // Missing from the renewal result: either the lease was lost or cancellation
                // was requested — disambiguate. A failed lookup proves neither, so keep the
                // job running and retry next heartbeat rather than cancelling a healthy job.
                JobRecord? current;
                try
                {
                    current = await storage.GetJobAsync(key.Id, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Post-renewal lookup for job {JobId} failed; retrying next heartbeat.", key.Id);
                    continue;
                }

                if (current is { State: JobState.Processing, CancelRequested: true }
                    && string.Equals(current.WorkerId, _workerId, StringComparison.Ordinal)
                    && current.Attempt == key.Attempt)
                {
                    inFlight.Cancel(CancelReason.CancelRequested);
                    await ApplyTransitionAsync(Terminal(inFlight.Job, JobState.Cancelled,
                        inFlight.Job.Failures, "Cancelled by request.", cancelContinuations: true))
                        .ConfigureAwait(false);
                }
                else
                {
                    logger.LogWarning(
                        "Lease lost for job {JobId} (attempt {Attempt}); cancelling the local execution.",
                        key.Id, key.Attempt);
                    inFlight.Cancel(CancelReason.LeaseLost);
                }
            }
        }
    }

    private async Task PumpNotificationsAsync(
        IStorageNotifier notifier, IReadOnlySet<string> queues, CancellationToken ct)
    {
        try
        {
            await foreach (var _ in notifier.ListenAsync(queues, ct).ConfigureAwait(false))
            {
                _wake.Writer.TryWrite(0);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Storage notifier stopped; falling back to polling.");
        }
    }

    private async Task WaitForWorkAsync(TimeSpan pollDelay, CancellationToken ct)
    {
        if (_notifierPump is null)
        {
            await SafeDelayAsync(pollDelay, ct).ConfigureAwait(false);
            return;
        }

        // A signal that arrived during the (empty) claim round-trip means work may already be
        // there — re-claim immediately instead of discarding it and sleeping.
        if (_wake.Reader.TryRead(out _))
        {
            return;
        }

        // Wake on a queue signal, bounded by the poll delay (notifications are a hint, not a
        // guarantee — the poll ceiling keeps us live even if every signal is dropped).
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var signal = _wake.Reader.WaitToReadAsync(linked.Token).AsTask();
        var timeout = Task.Delay(pollDelay, time, linked.Token);
        await Task.WhenAny(signal, timeout).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
    }

    private async Task WaitForSlotAsync(CancellationToken ct)
    {
        var running = _inFlight.Values.Select(f => f.Task).Where(t => t is not null).Cast<Task>().ToArray();
        if (running.Length == 0)
        {
            await SafeDelayAsync(TimeSpan.FromMilliseconds(10), ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await Task.WhenAny(running).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Applies a successful job's transition, rebasing its checkpoint if another writer wins the
    /// revision race.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Retrying the transition unchanged would conflict forever — the revision it expects is already
    /// gone. So a conflict asks the side effects to recompute against current state and applies the
    /// rebased transition instead. The activity is not re-executed, which is what §6.2 requires of a
    /// checkpoint conflict.
    /// </para>
    /// <para>
    /// Bounded: a heavily contended instance must eventually fail the job rather than spin holding a
    /// lease. Exhausting the attempts falls through to ordinary failure handling, where the retry
    /// policy takes over and the activity does run again.
    /// </para>
    /// </remarks>
    private async Task ApplyWithRemergeAsync(JobRecord job, JobSideEffects effects)
    {
        const int MaxRemerges = 5;

        for (var attempt = 0; ; attempt++)
        {
            var transition = Terminal(
                job, JobState.Succeeded, job.Failures, error: null,
                activateContinuations: true, effects: effects);

            try
            {
                await storage.ApplyAsync(transition, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (MillraceStorageException e) when (
                effects.Remerge is not null && attempt < MaxRemerges)
            {
                logger.LogDebug(
                    e, "Checkpoint for job {JobId} lost the revision race (attempt {Attempt}); re-merging.",
                    job.Id, attempt + 1);

                if (!await effects.Remerge(CancellationToken.None).ConfigureAwait(false))
                {
                    throw;
                }
            }
        }
    }

    private async Task ApplyTransitionAsync(JobTransition transition)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var applied = await storage.ApplyAsync(transition, CancellationToken.None).ConfigureAwait(false);
                if (!applied)
                {
                    logger.LogDebug(
                        "Transition for job {JobId} to {State} was fence-rejected (lease lost).",
                        transition.JobId, transition.TargetState);
                }

                return;
            }
            catch (Exception e) when (attempt < 2)
            {
                logger.LogWarning(e, "ApplyAsync failed (attempt {Attempt}); retrying.", attempt + 1);
                await SafeDelayAsync(TimeSpan.FromMilliseconds(200 * (attempt + 1)), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                logger.LogError(e,
                    "Dropping transition for job {JobId} to {State}; the lease will expire and the job re-runs.",
                    transition.JobId, transition.TargetState);
                return;
            }
        }
    }

    private JobTransition Terminal(
        JobRecord job, JobState state, int failures, string? error,
        bool activateContinuations = false, bool cancelContinuations = false,
        JobSideEffects? effects = null) => new()
    {
        JobId = job.Id,
        ExpectedWorkerId = _workerId,
        ExpectedAttempt = job.Attempt,
        TargetState = state,
        Failures = failures,
        Error = error,
        FinishedAt = time.GetUtcNow(),
        ActivateContinuations = activateContinuations,
        CancelContinuations = cancelContinuations,
        // Only a successful execution contributes effects: a failing activity must not advance its
        // workflow, and a cancelled one has nothing to say.
        Enqueue = effects is null ? [] : effects.Enqueue,
        Checkpoint = effects?.Checkpoint,
    };

    private JobTransition Release(JobRecord job) => new()
    {
        JobId = job.Id,
        ExpectedWorkerId = _workerId,
        ExpectedAttempt = job.Attempt,
        TargetState = JobState.Enqueued,
        Failures = job.Failures,
    };

    private async Task SafeDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, time, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static string Truncate(string text) =>
        text.Length <= 8192 ? text : text[..8192];

    public override void Dispose()
    {
        _abandon.Dispose();
        base.Dispose();
    }

    private sealed class InFlightJob(JobRecord job, CancellationTokenSource cts) : IDisposable
    {
        private volatile CancelReason _reason;

        public JobRecord Job { get; } = job;

        public CancellationTokenSource Cts { get; } = cts;

        public Task? Task { get; set; }

        public CancelReason Reason => _reason;

        public void Cancel(CancelReason reason)
        {
            _reason = reason;
            try
            {
                Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The job finished and disposed its CTS between our snapshot and this call.
            }
        }

        public void Dispose() => Cts.Dispose();
    }
}
