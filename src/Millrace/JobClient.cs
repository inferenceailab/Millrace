using System.Linq.Expressions;
using Microsoft.Extensions.Options;
using Millrace.Invocations;
using Millrace.Scheduling;
using Millrace.Storage;
using Millrace.Tenancy;

namespace Millrace;

/// <summary>Default <see cref="IJobClient"/>: captures expressions into records and hands them to storage.</summary>
public sealed class JobClient(
    IJobStorage storage,
    ITenantContextAccessor tenants,
    TimeProvider time,
    IOptions<MillraceOptions> options) : IJobClient
{
    private readonly MillraceOptions _options = options.Value;

    public async ValueTask<JobId> EnqueueAsync<T>(
        Expression<Func<T, Task>> call, EnqueueOptions? options = null, CancellationToken ct = default)
        where T : class
    {
        var record = Build(InvocationCapture.Capture(call, _options.SerializerOptions),
            options, JobState.Enqueued, dueAt: null, parentId: null);
        var ids = await storage.EnqueueAsync([record], ct).ConfigureAwait(false);
        return ids[0];
    }

    public ValueTask<JobId> ScheduleAsync<T>(
        Expression<Func<T, Task>> call, TimeSpan delay, EnqueueOptions? options = null, CancellationToken ct = default)
        where T : class
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delay.Ticks, nameof(delay));
        return ScheduleAsync(call, time.GetUtcNow() + delay, options, ct);
    }

    public async ValueTask<JobId> ScheduleAsync<T>(
        Expression<Func<T, Task>> call, DateTimeOffset at, EnqueueOptions? options = null, CancellationToken ct = default)
        where T : class
    {
        var record = Build(InvocationCapture.Capture(call, _options.SerializerOptions),
            options, JobState.Scheduled, dueAt: at, parentId: null);
        var ids = await storage.EnqueueAsync([record], ct).ConfigureAwait(false);
        return ids[0];
    }

    public async ValueTask<JobId> ContinueWithAsync<T>(
        JobId parentId, Expression<Func<T, Task>> call, EnqueueOptions? options = null, CancellationToken ct = default)
        where T : class
    {
        var record = Build(InvocationCapture.Capture(call, _options.SerializerOptions),
            options, JobState.Awaiting, dueAt: null, parentId: parentId);
        var ids = await storage.EnqueueAsync([record], ct).ConfigureAwait(false);
        return ids[0];
    }

    public ValueTask UpsertRecurringAsync<T>(
        string recurringId, string cron, Expression<Func<T, Task>> call,
        EnqueueOptions? options = null, CancellationToken ct = default)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recurringId);
        if (options?.IdempotencyKey is not null)
        {
            throw new ArgumentException(
                "Idempotency keys are not supported on recurring jobs in 0.1 — the recurring id " +
                "is the definition's identity.", nameof(options));
        }

        var expression = CronExpression.Parse(cron);
        var now = time.GetUtcNow();
        var next = expression.GetNextOccurrence(now)
            ?? throw new ArgumentException($"Cron expression '{cron}' never fires.", nameof(cron));

        var record = new RecurringJobRecord
        {
            Id = recurringId,
            Cron = cron,
            Queue = options?.Queue ?? MillraceOptions.DefaultQueue,
            Invocation = InvocationCapture.Capture(call, _options.SerializerOptions),
            Retry = options?.Retry ?? _options.DefaultRetry,
            Priority = options?.Priority ?? 0,
            TenantId = tenants.TenantId,
            NextFireTime = next,
            CreatedAt = now,
            UpdatedAt = now,
        };
        return storage.UpsertRecurringAsync(record, ct);
    }

    public ValueTask RemoveRecurringAsync(string recurringId, CancellationToken ct = default)
        => storage.RemoveRecurringAsync(recurringId, ct);

    public ValueTask<bool> CancelAsync(JobId id, CancellationToken ct = default)
        => storage.TryCancelAsync(id, ct);

    public ValueTask<IReadOnlyList<JobId>> EnqueueBatchAsync(JobBatch batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.Count == 0)
        {
            // An empty batch is not an error — a fan-out over an empty collection is ordinary — and
            // a round trip to insert nothing would be waste.
            return new ValueTask<IReadOnlyList<JobId>>([]);
        }

        var factory = new JobFactory(
            (invocation, options, state, dueAt) => Build(invocation, options, state, dueAt, parentId: null),
            _options.SerializerOptions,
            time.GetUtcNow());

        var records = batch.Build(factory);

        // The storage contract leaves this to the caller and lets providers reject it or not, so
        // left alone the behaviour would differ by database: one deduplicates, another violates a
        // unique index. Checking here makes a likely mistake — building a batch in a loop and
        // reusing a key — fail the same way everywhere, before anything is written.
        var duplicate = records
            .Where(r => r.IdempotencyKey is not null)
            .GroupBy(r => (r.TenantId, r.IdempotencyKey), StringTupleComparer.Instance)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Two jobs in the batch share idempotency key '{duplicate.Key.IdempotencyKey}'. Keys are "
                + "unique among active jobs, so a batch cannot hold the same key twice — the second would "
                + "silently resolve to the first.",
                nameof(batch));
        }

        return storage.EnqueueAsync(records, ct);
    }

    public async ValueTask<JobId?> RequeueAsync(JobId id, CancellationToken ct = default)
    {
        var original = await storage.GetJobAsync(id, ct).ConfigureAwait(false);
        if (original is null)
        {
            return null;
        }

        if (!original.State.IsTerminal() && original.State != JobState.Failed)
        {
            throw new InvalidOperationException(
                $"Job '{id}' is {original.State} and has not finished. Requeueing a job that is still "
                + "scheduled, queued or running would duplicate work — cancel it first if that is the "
                + "intent.");
        }

        var copy = new JobRecord
        {
            Id = JobId.New(time),
            Queue = original.Queue,
            State = JobState.Enqueued,
            Priority = original.Priority,
            Invocation = original.Invocation,
            Retry = original.Retry,
            // Carried deliberately: if another active job now holds the key, the existing enqueue
            // semantics make this a no-op returning that job — which is the correct answer to
            // "run this again" when an equivalent run is already in flight.
            IdempotencyKey = original.IdempotencyKey,
            TenantId = original.TenantId,
            CreatedAt = time.GetUtcNow(),
            RequeuedFrom = original.Id,
            // Not copied: ParentId, WorkflowInstanceId and ActivityNodeId. A requeue is a fresh
            // piece of work, not a continuation or a workflow step — reattaching it to either would
            // make it advance state that has already moved on.
        };

        var ids = await storage.EnqueueAsync([copy], ct).ConfigureAwait(false);
        return ids[0];
    }

    public async ValueTask<bool> TriggerRecurringAsync(string recurringId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recurringId);

        var definition = await storage.GetRecurringAsync(recurringId, ct).ConfigureAwait(false);
        if (definition is null)
        {
            return false;
        }

        // A plain enqueue, deliberately not TryFireRecurringAsync: that operation exists to fence
        // the *scheduled* occurrence across nodes, and borrowing it here would advance NextFireTime
        // and silently skip the occurrence the operator was not asking to replace.
        var now = time.GetUtcNow();
        await storage.EnqueueAsync(
            [
                new JobRecord
                {
                    Id = JobId.New(time),
                    Queue = definition.Queue,
                    State = JobState.Enqueued,
                    Priority = definition.Priority,
                    Invocation = definition.Invocation,
                    Retry = definition.Retry,
                    TenantId = definition.TenantId,
                    CreatedAt = now,
                },
            ],
            ct).ConfigureAwait(false);

        return true;
    }

    private JobRecord Build(
        JobInvocation invocation, EnqueueOptions? options, JobState state,
        DateTimeOffset? dueAt, JobId? parentId) => new()
    {
        Id = JobId.New(time),
        Queue = options?.Queue ?? MillraceOptions.DefaultQueue,
        Invocation = invocation,
        State = state,
        Priority = options?.Priority ?? 0,
        CreatedAt = time.GetUtcNow(),
        DueAt = dueAt,
        Retry = options?.Retry ?? _options.DefaultRetry,
        IdempotencyKey = options?.IdempotencyKey,
        TenantId = tenants.TenantId,
        ParentId = parentId,
        // Captured here because this is the only place the enqueuing trace is still ambient: by the
        // time a worker picks the job up it is a different process with no context to inherit.
        TraceParent = System.Diagnostics.Activity.Current?.Id,
    };
}
