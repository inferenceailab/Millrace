using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Weft.Storage.InMemory;

/// <summary>
/// The bundled in-memory provider (ARCHITECTURE.md §4 P5) — for development, samples, and
/// tests. Explicitly not durable. A single lock serializes every operation, which makes the
/// atomicity contract trivially true; the value of this implementation is precision, not speed.
/// </summary>
public sealed class InMemoryStorage : IJobStorage, IWorkflowStorage, IStorageNotifier
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;

    private readonly Dictionary<JobId, JobEntry> _jobs = [];
    private readonly Dictionary<(string? TenantId, string Key), JobId> _activeKeys = [];
    private readonly Dictionary<string, RecurringJobRecord> _recurring = [];
    private readonly Dictionary<WorkflowInstanceId, WorkflowInstanceRecord> _instances = [];
    private readonly List<BookmarkRecord> _bookmarks = [];
    private readonly List<Listener> _listeners = [];
    private long _sequence;

    public InMemoryStorage(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public StorageCapabilities Capabilities => StorageCapabilities.Notifications;

    // ---------------------------------------------------------------- IJobStorage

    public ValueTask<IReadOnlyList<JobId>> EnqueueAsync(IReadOnlyList<JobRecord> jobs, CancellationToken ct)
    {
        List<string> wakeups;
        var ids = new JobId[jobs.Count];

        lock (_gate)
        {
            var undo = new List<Action>();
            wakeups = [];
            try
            {
                for (var i = 0; i < jobs.Count; i++)
                {
                    ids[i] = InsertCore(jobs[i], undo, wakeups);
                }
            }
            catch
            {
                Rollback(undo);
                throw;
            }
        }

        Publish(wakeups);
        return ValueTask.FromResult<IReadOnlyList<JobId>>(ids);
    }

    public ValueTask<IReadOnlyList<JobRecord>> ClaimAsync(ClaimRequest request, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var queues = request.Queues.ToHashSet(StringComparer.Ordinal);
        var claimed = new List<JobRecord>();

        lock (_gate)
        {
            var eligible = _jobs.Values
                .Where(e => queues.Contains(e.Record.Queue) && IsClaimable(e.Record, now))
                .OrderByDescending(e => e.Record.Priority)
                .ThenBy(e => e.Sequence)
                .Take(request.MaxCount);

            foreach (var entry in eligible.ToList())
            {
                entry.Record = entry.Record with
                {
                    State = JobState.Processing,
                    WorkerId = request.WorkerId,
                    LeaseUntil = now + request.LeaseDuration,
                    Attempt = entry.Record.Attempt + 1,
                };
                claimed.Add(entry.Record);
            }
        }

        return ValueTask.FromResult<IReadOnlyList<JobRecord>>(claimed);

        static bool IsClaimable(JobRecord job, DateTimeOffset now) =>
            job.State == JobState.Enqueued
            || (job.State == JobState.Processing && job.LeaseUntil is { } lease && lease <= now);
    }

    public ValueTask<IReadOnlyList<JobId>> RenewLeasesAsync(
        string workerId, IReadOnlyList<JobId> jobs, TimeSpan lease, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var renewed = new List<JobId>();

        lock (_gate)
        {
            foreach (var id in jobs)
            {
                if (!_jobs.TryGetValue(id, out var entry)
                    || entry.Record.State != JobState.Processing
                    || !string.Equals(entry.Record.WorkerId, workerId, StringComparison.Ordinal))
                {
                    continue;
                }

                // Lease expiry alone never ends ownership: an expired-but-unreclaimed lease is
                // resurrected here. Cancel-requested jobs keep their lease but are omitted from
                // the result so the worker disambiguates via GetJobAsync.
                entry.Record = entry.Record with { LeaseUntil = now + lease };
                if (!entry.Record.CancelRequested)
                {
                    renewed.Add(id);
                }
            }
        }

        return ValueTask.FromResult<IReadOnlyList<JobId>>(renewed);
    }

    public ValueTask<bool> ApplyAsync(JobTransition transition, CancellationToken ct)
    {
        List<string> wakeups;

        lock (_gate)
        {
            if (!_jobs.TryGetValue(transition.JobId, out var entry)
                || entry.Record.State != JobState.Processing
                || !string.Equals(entry.Record.WorkerId, transition.ExpectedWorkerId, StringComparison.Ordinal)
                || entry.Record.Attempt != transition.ExpectedAttempt)
            {
                return ValueTask.FromResult(false);
            }

            var undo = new List<Action>();
            wakeups = [];
            try
            {
                TransitionCore(entry, transition, undo, wakeups);

                foreach (var record in transition.Enqueue)
                {
                    InsertCore(record, undo, wakeups);
                }

                if (transition.ActivateContinuations)
                {
                    ActivateChildren(transition.JobId, undo, wakeups);
                }

                if (transition.CancelContinuations)
                {
                    CancelAwaitingClosure(transition.JobId, undo);
                }
            }
            catch
            {
                Rollback(undo);
                throw;
            }
        }

        Publish(wakeups);
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> TryCancelAsync(JobId id, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var entry) || entry.Record.State.IsTerminal())
            {
                return ValueTask.FromResult(false);
            }

            if (entry.Record.State == JobState.Processing)
            {
                entry.Record = entry.Record with { CancelRequested = true };
                return ValueTask.FromResult(true);
            }

            var undo = new List<Action>();
            try
            {
                CancelEntry(entry, undo);
                CancelAwaitingClosure(id, undo);
            }
            catch
            {
                Rollback(undo);
                throw;
            }

            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<JobRecord?> GetJobAsync(JobId id, CancellationToken ct)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_jobs.TryGetValue(id, out var entry) ? entry.Record : null);
        }
    }

    public ValueTask<int> ActivateDueJobsAsync(DateTimeOffset now, int batchSize, CancellationToken ct)
    {
        List<string> wakeups = [];
        int activated;

        lock (_gate)
        {
            var due = _jobs.Values
                .Where(e => e.Record.State is JobState.Scheduled or JobState.Failed
                    && e.Record.DueAt is { } dueAt && dueAt <= now)
                .OrderBy(e => e.Record.DueAt)
                .ThenBy(e => e.Sequence)
                .Take(batchSize)
                .ToList();

            foreach (var entry in due)
            {
                entry.Record = entry.Record with { State = JobState.Enqueued, DueAt = null };
                wakeups.Add(entry.Record.Queue);
            }

            activated = due.Count;
        }

        Publish(wakeups);
        return ValueTask.FromResult(activated);
    }

    public ValueTask UpsertRecurringAsync(RecurringJobRecord record, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_recurring.TryGetValue(record.Id, out var stored))
            {
                _recurring[record.Id] = record with
                {
                    NextFireTime = string.Equals(stored.Cron, record.Cron, StringComparison.Ordinal)
                        ? stored.NextFireTime
                        : record.NextFireTime,
                    LastFireTime = stored.LastFireTime,
                    CreatedAt = stored.CreatedAt,
                };
            }
            else
            {
                _recurring[record.Id] = record;
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<RecurringJobRecord?> GetRecurringAsync(string id, CancellationToken ct)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_recurring.TryGetValue(id, out var record) ? record : null);
        }
    }

    public ValueTask RemoveRecurringAsync(string id, CancellationToken ct)
    {
        lock (_gate)
        {
            _recurring.Remove(id);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<RecurringJobRecord>> GetDueRecurringAsync(
        DateTimeOffset now, int batchSize, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<RecurringJobRecord> due = _recurring.Values
                .Where(r => r.NextFireTime <= now)
                .OrderBy(r => r.NextFireTime)
                .ThenBy(r => r.Id, StringComparer.Ordinal)
                .Take(batchSize)
                .ToList();
            return ValueTask.FromResult(due);
        }
    }

    public ValueTask<bool> TryFireRecurringAsync(
        string id, DateTimeOffset expectedFireTime, DateTimeOffset nextFireTime,
        JobRecord job, CancellationToken ct)
    {
        List<string> wakeups;

        lock (_gate)
        {
            if (!_recurring.TryGetValue(id, out var stored) || stored.NextFireTime != expectedFireTime)
            {
                return ValueTask.FromResult(false);
            }

            var undo = new List<Action>();
            wakeups = [];
            try
            {
                InsertCore(job, undo, wakeups);
            }
            catch
            {
                Rollback(undo);
                throw;
            }

            _recurring[id] = stored with
            {
                NextFireTime = nextFireTime,
                LastFireTime = expectedFireTime,
                UpdatedAt = _time.GetUtcNow(),
            };
        }

        Publish(wakeups);
        return ValueTask.FromResult(true);
    }

    // ---------------------------------------------------------------- IWorkflowStorage

    public ValueTask CreateInstanceAsync(WorkflowInstanceRecord instance, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_instances.TryAdd(instance.Id, instance with { Revision = 1 }))
            {
                throw new WeftConcurrencyException(
                    $"Workflow instance '{instance.Id}' already exists.");
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<WorkflowInstanceRecord?> GetInstanceAsync(WorkflowInstanceId id, CancellationToken ct)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_instances.TryGetValue(id, out var instance) ? instance : null);
        }
    }

    public ValueTask UpdateInstanceAsync(
        WorkflowInstanceRecord instance, long expectedRevision, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_instances.TryGetValue(instance.Id, out var stored) || stored.Revision != expectedRevision)
            {
                throw new WeftConcurrencyException(
                    $"Workflow instance '{instance.Id}' revision conflict (expected {expectedRevision}).");
            }

            _instances[instance.Id] = instance with { Revision = expectedRevision + 1 };
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask AddBookmarkAsync(BookmarkRecord bookmark, CancellationToken ct)
    {
        lock (_gate)
        {
            _bookmarks.Add(bookmark);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<BookmarkRecord?> ConsumeBookmarkAsync(
        string signalName, string correlationId, CancellationToken ct)
    {
        lock (_gate)
        {
            BookmarkRecord? oldest = null;
            foreach (var bookmark in _bookmarks)
            {
                if (!string.Equals(bookmark.SignalName, signalName, StringComparison.Ordinal)
                    || !string.Equals(bookmark.CorrelationId, correlationId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (oldest is null
                    || bookmark.CreatedAt < oldest.CreatedAt
                    || (bookmark.CreatedAt == oldest.CreatedAt
                        && bookmark.Id.CompareTo(oldest.Id) < 0))
                {
                    oldest = bookmark;
                }
            }

            if (oldest is not null)
            {
                _bookmarks.Remove(oldest);
            }

            return ValueTask.FromResult(oldest);
        }
    }

    // ---------------------------------------------------------------- IStorageNotifier

    public async IAsyncEnumerable<QueueSignal> ListenAsync(
        IReadOnlySet<string> queues, [EnumeratorCancellation] CancellationToken ct)
    {
        var listener = new Listener(
            Channel.CreateUnbounded<QueueSignal>(new UnboundedChannelOptions { SingleReader = true }),
            queues);

        lock (_gate)
        {
            _listeners.Add(listener);
        }

        try
        {
            await foreach (var signal in listener.Channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return signal;
            }
        }
        finally
        {
            lock (_gate)
            {
                _listeners.Remove(listener);
            }
        }
    }

    // ---------------------------------------------------------------- internals

    /// <summary>
    /// Inserts one record with the full EnqueueAsync semantics: duplicate-id rejection,
    /// idempotency dedup, and the Awaiting parent fixup. Registers exact undo actions so the
    /// caller can roll the whole batch back on a later failure.
    /// </summary>
    private JobId InsertCore(JobRecord record, List<Action> undo, List<string> wakeups)
    {
        var effective = record;

        if (effective.State == JobState.Awaiting)
        {
            if (effective.ParentId is not { } parentId)
            {
                throw new ArgumentException(
                    $"Job '{effective.Id}' is Awaiting but has no ParentId.", nameof(record));
            }

            if (!_jobs.TryGetValue(parentId, out var parent))
            {
                throw new WeftParentJobNotFoundException(parentId);
            }

            // Fixup: a parent that is already terminal resolves the child immediately. Holding
            // the single lock makes this trivially serializable with the parent's ApplyAsync.
            effective = parent.Record.State switch
            {
                JobState.Succeeded => effective with { State = JobState.Enqueued },
                JobState.Dead or JobState.Cancelled => effective with
                {
                    State = JobState.Cancelled,
                    FinishedAt = _time.GetUtcNow(),
                },
                _ => effective,
            };
        }
        else if (effective.State is not (JobState.Scheduled or JobState.Enqueued))
        {
            throw new ArgumentException(
                $"Job '{effective.Id}' has non-insertable state {effective.State}.", nameof(record));
        }

        var keyScope = ActiveKeyScope(effective);
        if (keyScope is { } scope && !effective.State.IsTerminal()
            && _activeKeys.TryGetValue(scope, out var holder))
        {
            return holder; // duplicate active key: no-op, existing job's id
        }

        if (!_jobs.TryAdd(effective.Id, new JobEntry { Record = effective, Sequence = ++_sequence }))
        {
            throw new ArgumentException($"Job '{effective.Id}' already exists.", nameof(record));
        }

        var insertedId = effective.Id;
        undo.Add(() => _jobs.Remove(insertedId));

        if (keyScope is { } added && !effective.State.IsTerminal())
        {
            _activeKeys[added] = effective.Id;
            undo.Add(() => _activeKeys.Remove(added));
        }

        if (effective.State == JobState.Enqueued)
        {
            wakeups.Add(effective.Queue);
        }

        return effective.Id;
    }

    private void TransitionCore(JobEntry entry, JobTransition transition, List<Action> undo, List<string> wakeups)
    {
        var previous = entry.Record;
        var hadKey = ActiveKeyScope(previous) is { } scope && _activeKeys.TryGetValue(scope, out var h)
            && h == previous.Id;
        undo.Add(() =>
        {
            entry.Record = previous;
            if (hadKey)
            {
                _activeKeys[ActiveKeyScope(previous)!.Value] = previous.Id;
            }
        });

        entry.Record = transition.TargetState switch
        {
            JobState.Succeeded or JobState.Dead or JobState.Cancelled => previous with
            {
                State = transition.TargetState,
                Failures = transition.Failures,
                LastError = transition.Error ?? previous.LastError,
                FinishedAt = transition.FinishedAt ?? _time.GetUtcNow(),
                WorkerId = null,
                LeaseUntil = null,
            },
            JobState.Failed => previous with
            {
                State = JobState.Failed,
                Failures = transition.Failures,
                LastError = transition.Error,
                DueAt = transition.DueAt,
                WorkerId = null,
                LeaseUntil = null,
            },
            JobState.Enqueued => previous with
            {
                State = JobState.Enqueued,
                Failures = transition.Failures,
                WorkerId = null,
                LeaseUntil = null,
            },
            _ => throw new ArgumentException(
                $"Invalid transition target state {transition.TargetState}.", nameof(transition)),
        };

        if (entry.Record.State.IsTerminal())
        {
            ReleaseActiveKey(entry.Record);
        }
        else if (entry.Record.State == JobState.Enqueued)
        {
            wakeups.Add(entry.Record.Queue);
        }
    }

    private void ActivateChildren(JobId parentId, List<Action> undo, List<string> wakeups)
    {
        foreach (var entry in _jobs.Values)
        {
            if (entry.Record.ParentId != parentId || entry.Record.State != JobState.Awaiting)
            {
                continue;
            }

            var previous = entry.Record;
            undo.Add(() => entry.Record = previous);
            entry.Record = previous with { State = JobState.Enqueued };
            wakeups.Add(entry.Record.Queue);
        }
    }

    /// <summary>Cancels the transitive Awaiting-descendant closure — descending only through
    /// nodes being cancelled; active (already activated) children keep their own fate.</summary>
    private void CancelAwaitingClosure(JobId rootId, List<Action> undo)
    {
        var frontier = new Queue<JobId>();
        frontier.Enqueue(rootId);

        while (frontier.TryDequeue(out var parentId))
        {
            foreach (var entry in _jobs.Values)
            {
                if (entry.Record.ParentId != parentId || entry.Record.State != JobState.Awaiting)
                {
                    continue;
                }

                CancelEntry(entry, undo);
                frontier.Enqueue(entry.Record.Id);
            }
        }
    }

    private void CancelEntry(JobEntry entry, List<Action> undo)
    {
        var previous = entry.Record;
        var hadKey = ActiveKeyScope(previous) is { } scope && _activeKeys.TryGetValue(scope, out var h)
            && h == previous.Id;
        undo.Add(() =>
        {
            entry.Record = previous;
            if (hadKey)
            {
                _activeKeys[ActiveKeyScope(previous)!.Value] = previous.Id;
            }
        });

        entry.Record = previous with
        {
            State = JobState.Cancelled,
            FinishedAt = _time.GetUtcNow(),
            WorkerId = null,
            LeaseUntil = null,
        };
        ReleaseActiveKey(entry.Record);
    }

    private void ReleaseActiveKey(JobRecord record)
    {
        if (ActiveKeyScope(record) is { } scope
            && _activeKeys.TryGetValue(scope, out var holder)
            && holder == record.Id)
        {
            _activeKeys.Remove(scope);
        }
    }

    private static (string?, string)? ActiveKeyScope(JobRecord record)
        => record.IdempotencyKey is { } key ? (record.TenantId, key) : null;

    private static void Rollback(List<Action> undo)
    {
        for (var i = undo.Count - 1; i >= 0; i--)
        {
            undo[i]();
        }
    }

    private void Publish(List<string> wakeups)
    {
        if (wakeups.Count == 0)
        {
            return;
        }

        Listener[] listeners;
        lock (_gate)
        {
            if (_listeners.Count == 0)
            {
                return;
            }

            listeners = [.. _listeners];
        }

        foreach (var queue in wakeups)
        {
            foreach (var listener in listeners)
            {
                if (listener.Queues.Contains(queue))
                {
                    listener.Channel.Writer.TryWrite(new QueueSignal(queue));
                }
            }
        }
    }

    private sealed class JobEntry
    {
        public required JobRecord Record { get; set; }

        public required long Sequence { get; init; }
    }

    private sealed record Listener(Channel<QueueSignal> Channel, IReadOnlySet<string> Queues);
}
