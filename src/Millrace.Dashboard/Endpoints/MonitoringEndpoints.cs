using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Millrace.Storage;
using Millrace.Storage.Monitoring;

namespace Millrace.Dashboard.Endpoints;

/// <summary>
/// The read-only half of the dashboard contract (ARCHITECTURE.md §7).
/// </summary>
/// <remarks>
/// <para>
/// Responses are the frozen storage DTOs themselves (§11.12), not a second set of API models. The
/// decision that froze those shapes froze them <em>with</em> the API contract, so re-projecting
/// would create two things to keep in step and two places for a field to go missing.
/// </para>
/// <para>
/// Every list is cursor-paged and carries no total, so no view can offer page numbers.
/// </para>
/// </remarks>
internal static class MonitoringEndpoints
{
    public static void MapMonitoringEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/statistics", GetStatisticsAsync)
            .WithName("GetStatistics")
            .Produces<JobStatistics>()
            .WithSummary("Aggregate counts for the overview.")
            .WithDescription(
                "The only source of counts. List endpoints deliberately carry no totals, because "
                + "counting a filtered, continuously changing job table is the expensive part of the query.");

        api.MapGet("/jobs", QueryJobsAsync)
            .WithName("QueryJobs")
            .Produces<Page<JobSummary>>()
            .WithSummary("Jobs, newest first, filtered and cursor-paged.")
            .WithDescription(
                "Repeat 'state' to include several states. Pass 'cursor' from the previous response's "
                + "nextCursor and treat it as opaque. There is no total and no page number.");

        api.MapGet("/jobs/{id:guid}", GetJobDetailsAsync)
            .WithName("GetJobDetails")
            .Produces<JobDetails>()
            .WithSummary("Full detail for one job, including its serialized arguments.")
            .WithDescription(
                "Reports attempt, failure and interruption counts plus the most recent error. "
                + "Per-attempt history is not stored, so there is no timeline.");

        api.MapGet("/recurring", QueryRecurringAsync)
            .WithName("QueryRecurring")
            .Produces<Page<RecurringSummary>>()
            .WithSummary("Recurring definitions, soonest first.")
            .WithDescription(
                "Ordered forwards in time, unlike the job and instance lists. A next fire time in the "
                + "past means the scheduler is behind. Last outcome is not available — nothing links a "
                + "fired job back to its definition.");

        api.MapGet("/instances", QueryInstancesAsync)
            .WithName("QueryInstances")
            .Produces<Page<WorkflowInstanceSummary>>()
            .WithSummary("Workflow instances, newest first, filtered and cursor-paged.")
            .WithDescription("The workflow engine lands in 0.3; this reads whatever instances exist.");
    }

    private static async Task<JsonHttpResult<JobStatistics>> GetStatisticsAsync(
        IMonitoringStorage storage,
        CancellationToken ct,
        [FromQuery] string? tenant = null,
        [FromQuery] bool untenanted = false)
        => DashboardJson.Ok(await storage.GetStatisticsAsync(ResolveTenant(tenant, untenanted), ct));

    private static async Task<JsonHttpResult<Page<JobSummary>>> QueryJobsAsync(
        IMonitoringStorage storage,
        CancellationToken ct,
        [FromQuery(Name = "state")] JobState[]? states = null,
        [FromQuery] string? queue = null,
        [FromQuery] string? tenant = null,
        [FromQuery] bool untenanted = false,
        [FromQuery] DateTimeOffset? createdAfter = null,
        [FromQuery] DateTimeOffset? createdBefore = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = JobQuery.DefaultLimit)
        => DashboardJson.Ok(await storage.QueryJobsAsync(
            new JobQuery
            {
                States = states,
                Queue = queue,
                Tenant = ResolveTenant(tenant, untenanted),
                CreatedAfter = createdAfter,
                CreatedBefore = createdBefore,
                Cursor = cursor,
                Limit = limit,
            },
            ct));

    private static async Task<Results<JsonHttpResult<JobDetails>, NotFound>> GetJobDetailsAsync(
        IMonitoringStorage storage, Guid id, CancellationToken ct)
    {
        var details = await storage.GetJobDetailsAsync(new JobId(id), ct);
        return details is null ? TypedResults.NotFound() : DashboardJson.Ok(details);
    }

    private static async Task<JsonHttpResult<Page<RecurringSummary>>> QueryRecurringAsync(
        IMonitoringStorage storage,
        CancellationToken ct,
        [FromQuery] string? queue = null,
        [FromQuery] string? tenant = null,
        [FromQuery] bool untenanted = false,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = RecurringQuery.DefaultLimit)
        => DashboardJson.Ok(await storage.QueryRecurringAsync(
            new RecurringQuery
            {
                Queue = queue,
                Tenant = ResolveTenant(tenant, untenanted),
                Cursor = cursor,
                Limit = limit,
            },
            ct));

    private static async Task<JsonHttpResult<Page<WorkflowInstanceSummary>>> QueryInstancesAsync(
        IMonitoringStorage storage,
        CancellationToken ct,
        [FromQuery(Name = "state")] WorkflowInstanceState[]? states = null,
        [FromQuery] string? definitionId = null,
        [FromQuery] int? definitionVersion = null,
        [FromQuery] string? tenant = null,
        [FromQuery] bool untenanted = false,
        [FromQuery] DateTimeOffset? createdAfter = null,
        [FromQuery] DateTimeOffset? createdBefore = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = InstanceQuery.DefaultLimit)
        => DashboardJson.Ok(await storage.QueryInstancesAsync(
            new InstanceQuery
            {
                States = states,
                DefinitionId = definitionId,
                DefinitionVersion = definitionVersion,
                Tenant = ResolveTenant(tenant, untenanted),
                CreatedAfter = createdAfter,
                CreatedBefore = createdBefore,
                Cursor = cursor,
                Limit = limit,
            },
            ct));

    /// <summary>
    /// Maps the two query parameters onto the three-way <see cref="TenantFilter"/>.
    /// </summary>
    /// <remarks>
    /// A single <c>tenant</c> parameter cannot express this: an absent or empty value would have to
    /// mean both "every tenant" and "the untenanted scope", which are different result sets. So
    /// <c>?untenanted=true</c> selects the null scope explicitly, <c>?tenant=acme</c> selects one
    /// tenant, and neither means no constraint. <c>untenanted</c> wins if both are supplied, since
    /// it is the more specific request.
    /// </remarks>
    private static TenantFilter ResolveTenant(string? tenant, bool untenanted)
    {
        if (untenanted)
        {
            return TenantFilter.Untenanted;
        }

        return string.IsNullOrWhiteSpace(tenant) ? TenantFilter.Any : TenantFilter.For(tenant);
    }
}
