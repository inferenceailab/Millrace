namespace Millrace.Storage.Monitoring;

/// <summary>
/// The dashboard read model (ARCHITECTURE.md §4.1). Separate from <see cref="IJobStorage"/> so the
/// hot path stays lean, but <b>required of a supported provider</b> (§11.14): a provider that omits
/// it leaves the dashboard blank, so <c>MapMillraceDashboard</c> fails at startup naming the
/// provider rather than letting an end user discover it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads are not linearized against the hot path.</b> These queries are allowed to observe a
/// slightly stale snapshot and MUST NOT take locks that could delay claiming or applying a
/// transition. A job may change state between appearing in a page and being fetched by id; callers
/// treat every result as a point-in-time observation.
/// </para>
/// <para>
/// <b>Paging is keyset, ordered <c>CreatedAt DESC, Id DESC</c></b> (§11.12). Within that ordering a
/// full traversal never returns the same row twice and never skips a row that existed, unchanged
/// and matching, for the whole traversal. Rows that change state mid-traversal may appear or vanish
/// — no pagination scheme can prevent that, and the contract does not pretend otherwise.
/// </para>
/// <para>
/// <b>Cursors are opaque and provider-defined.</b> A provider MUST reject a cursor it cannot decode
/// — malformed or truncated — with <see cref="MillraceStorageException"/>. Silently treating an
/// unrecognized cursor as "start from the beginning" would turn a client bug into an infinite paging
/// loop. Providers sharing an encoding (as the bundled ones do, via
/// <see cref="MonitoringCursor"/>) will decode each other's cursors; that is harmless, since a
/// dashboard is bound to one provider and a cursor cannot legitimately cross between them.
/// </para>
/// <para>
/// <b>Tenancy.</b> Every query carries a <see cref="TenantFilter"/>; providers apply it exactly,
/// distinguishing "any tenant" from "the untenanted scope". This interface performs no
/// authorization of its own — the dashboard decides who may ask (§11.13).
/// </para>
/// </remarks>
public interface IMonitoringStorage
{
    /// <summary>
    /// Aggregate counts for the overview, scoped by <paramref name="tenant"/>.
    /// </summary>
    /// <remarks>
    /// Counts are a consistent-enough snapshot, not a serializable one: providers MAY compute the
    /// components independently, so figures need not sum consistently across a concurrent
    /// transition. Callers must not derive invariants from them.
    /// </remarks>
    ValueTask<JobStatistics> GetStatisticsAsync(TenantFilter tenant, CancellationToken ct);

    /// <summary>
    /// One page of jobs matching <paramref name="query"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="JobQuery.Limit"/> is clamped, never rejected. A cursor is only meaningful with the
    /// filters that produced it; presenting one with different filters yields undefined ordering and
    /// providers MAY throw <see cref="MillraceStorageException"/> rather than return nonsense.
    /// </remarks>
    /// <exception cref="MillraceStorageException">The cursor was not issued by this provider.</exception>
    ValueTask<Page<JobSummary>> QueryJobsAsync(JobQuery query, CancellationToken ct);

    /// <summary>
    /// One page of workflow instances matching <paramref name="query"/>. Same paging, cursor and
    /// tenancy rules as <see cref="QueryJobsAsync"/>.
    /// </summary>
    /// <exception cref="MillraceStorageException">The cursor was not issued by this provider.</exception>
    ValueTask<Page<WorkflowInstanceSummary>> QueryInstancesAsync(InstanceQuery query, CancellationToken ct);

    /// <summary>
    /// Full detail for one job, or <see langword="null"/> if no such job exists.
    /// </summary>
    /// <remarks>
    /// Named distinctly from <see cref="IJobStorage.GetJobAsync"/> on purpose: a provider
    /// implementing both interfaces on one class — as the bundled providers do — cannot have two
    /// methods differing only by return type.
    /// </remarks>
    ValueTask<JobDetails?> GetJobDetailsAsync(JobId id, CancellationToken ct);
}
