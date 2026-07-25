using System.Linq.Expressions;
using Millrace.Invocations;
using Millrace.Storage;

namespace Millrace;

/// <summary>
/// Jobs to enqueue together, in one round trip and one transaction.
/// </summary>
/// <remarks>
/// <para>
/// Fan-out over a thousand items is a thousand round trips with
/// <see cref="IJobClient.EnqueueAsync{T}"/>. The storage contract has always inserted
/// all-or-nothing (§4.2); this is the client surface that reaches it.
/// </para>
/// <para>
/// <b>All-or-nothing is the point, not a side effect.</b> A partially-enqueued fan-out is worse
/// than none: the caller cannot tell which half landed, and retrying duplicates the rest unless
/// every item carries an idempotency key.
/// </para>
/// </remarks>
public sealed class JobBatch
{
    private readonly List<Func<JobFactory, JobRecord>> _items = [];

    /// <summary>How many jobs the batch will insert.</summary>
    public int Count => _items.Count;

    /// <summary>Adds a fire-and-forget job.</summary>
    public JobBatch Enqueue<T>(Expression<Func<T, Task>> call, EnqueueOptions? options = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(call);
        _items.Add(factory => factory.Create(call, options, JobState.Enqueued, dueAt: null));
        return this;
    }

    /// <summary>Adds a job that becomes claimable after <paramref name="delay"/>.</summary>
    public JobBatch Schedule<T>(Expression<Func<T, Task>> call, TimeSpan delay, EnqueueOptions? options = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentOutOfRangeException.ThrowIfNegative(delay.Ticks, nameof(delay));
        _items.Add(factory => factory.Create(call, options, JobState.Scheduled, factory.Now + delay));
        return this;
    }

    /// <summary>Adds a job that becomes claimable at <paramref name="at"/>.</summary>
    public JobBatch Schedule<T>(Expression<Func<T, Task>> call, DateTimeOffset at, EnqueueOptions? options = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(call);
        _items.Add(factory => factory.Create(call, options, JobState.Scheduled, at));
        return this;
    }

    internal IReadOnlyList<JobRecord> Build(JobFactory factory)
        => [.. _items.Select(item => item(factory))];
}

/// <summary>
/// Compares <c>(TenantId, IdempotencyKey)</c> pairs ordinally, so the batch's duplicate check
/// scopes keys per tenant exactly as the storage contract does (§11.8).
/// </summary>
internal sealed class StringTupleComparer : IEqualityComparer<(string? TenantId, string? IdempotencyKey)>
{
    public static readonly StringTupleComparer Instance = new();

    public bool Equals((string? TenantId, string? IdempotencyKey) x, (string? TenantId, string? IdempotencyKey) y)
        => string.Equals(x.TenantId, y.TenantId, StringComparison.Ordinal)
           && string.Equals(x.IdempotencyKey, y.IdempotencyKey, StringComparison.Ordinal);

    public int GetHashCode((string? TenantId, string? IdempotencyKey) obj)
        => HashCode.Combine(
            obj.TenantId is null ? 0 : StringComparer.Ordinal.GetHashCode(obj.TenantId),
            obj.IdempotencyKey is null ? 0 : StringComparer.Ordinal.GetHashCode(obj.IdempotencyKey));
}

/// <summary>Turns a captured call into a record, with the client's ambient settings applied.</summary>
/// <remarks>
/// Exists so <see cref="JobBatch"/> can stay a plain description of intent, built without a client
/// and without touching storage — the record is only materialised when the batch is submitted.
/// </remarks>
public sealed class JobFactory
{
    private readonly Func<JobInvocation, EnqueueOptions?, JobState, DateTimeOffset?, JobRecord> _build;
    private readonly System.Text.Json.JsonSerializerOptions _json;

    internal JobFactory(
        Func<JobInvocation, EnqueueOptions?, JobState, DateTimeOffset?, JobRecord> build,
        System.Text.Json.JsonSerializerOptions json,
        DateTimeOffset now)
    {
        _build = build;
        _json = json;
        Now = now;
    }

    internal DateTimeOffset Now { get; }

    internal JobRecord Create<T>(
        Expression<Func<T, Task>> call, EnqueueOptions? options, JobState state, DateTimeOffset? dueAt)
        where T : class
        => _build(InvocationCapture.Capture(call, _json), options, state, dueAt);
}
