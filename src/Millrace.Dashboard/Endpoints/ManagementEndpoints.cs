using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Millrace.Workflows;

namespace Millrace.Dashboard.Endpoints;

/// <summary>
/// The management half of the dashboard contract (ARCHITECTURE.md §7).
/// </summary>
/// <remarks>
/// <para>
/// These mutate, so they are <c>POST</c> and they are the reason §11.13 makes an unconfigured
/// authorization hook a startup error: a read-only dashboard left open leaks job payloads, but an
/// open management surface lets anyone cancel production work.
/// </para>
/// <para>
/// Every action goes through the same client APIs a consumer would call. The dashboard is a client
/// of the engine, not a privileged path into it — anything it can do is reachable from code.
/// </para>
/// </remarks>
internal static class ManagementEndpoints
{
    public static void MapManagementEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapPost("/jobs/{id:guid}/cancel", CancelJobAsync)
            .WithName("CancelJob")
            .WithSummary("Cancels a job.")
            .WithDescription(
                "Pre-active states cancel outright, along with their continuation closure. A running "
                + "job is asked to stop cooperatively, so 200 is not a promise the work did not happen.");

        api.MapPost("/jobs/{id:guid}/requeue", RequeueJobAsync)
            .WithName("RequeueJob")
            .Produces<RequeuedJob>()
            .WithSummary("Runs a finished job again as a new job.")
            .WithDescription(
                "Returns the new job's id. The original is left untouched — terminal records are "
                + "immutable — and the new job links back to it. Requeueing a job that has not "
                + "finished is refused with 409.");

        api.MapPost("/jobs/{id:guid}/run-now", RunJobNowAsync)
            .WithName("RunJobNow")
            .WithSummary("Runs a job that is waiting out its retry backoff, now.")
            .WithDescription(
                "Shortens the wait and nothing else: no retry budget is consumed, because nothing "
                + "was attempted. 404 means the job is not awaiting a retry — running, terminal, or "
                + "scheduled but never yet run — which is the ordinary answer for a stale button.");

        api.MapPost("/recurring/{id}/trigger", TriggerRecurringAsync)
            .WithName("TriggerRecurring")
            .WithSummary("Fires a recurring definition now, without disturbing its schedule.")
            .WithDescription("An extra occurrence: the next scheduled fire time is unchanged.");

        // One route with the action in the path rather than three siblings: they are three answers
        // to one question — what to do about a compensation that failed — and an operator picks
        // exactly one (§11.30).
        api.MapPost("/instances/{id:guid}/compensation/{action}", RecoverCompensationAsync)
            .WithName("RecoverCompensation")
            .WithSummary("Moves an unwind that a failed compensation left suspended.")
            .WithDescription(
                "Action is retry, skip or abandon. 202 means the decision was accepted, not that it "
                + "has been applied: it runs as a job, so it inherits retries and appears in the job "
                + "list like any other work. 404 means there was nothing to recover — the instance is "
                + "not suspended, or somebody already recovered it — which is the ordinary answer for "
                + "a stale button rather than a fault.");

        api.MapPost("/signals/{name}/{correlationId}", SendSignalAsync)
            .WithName("SendSignal")
            .WithSummary("Delivers a signal to a waiting workflow instance.")
            .WithDescription(
                "The request body is the payload, as JSON. Delivery is at-most-once: 404 means no "
                + "instance was waiting on that name and correlation id.");
    }

    private static async Task<Results<Ok, NotFound>> CancelJobAsync(
        IJobClient jobs, Guid id, CancellationToken ct)
        => await jobs.CancelAsync(new JobId(id), ct)
            ? TypedResults.Ok()
            // Terminal or unknown are deliberately indistinguishable here, as in the storage
            // contract: neither is something an operator can act on differently.
            : TypedResults.NotFound();

    private static async Task<Results<JsonHttpResult<RequeuedJob>, NotFound, Conflict<string>>> RequeueJobAsync(
        IJobClient jobs, Guid id, CancellationToken ct)
    {
        try
        {
            var requeued = await jobs.RequeueAsync(new JobId(id), ct);
            return requeued is { } newId
                ? DashboardJson.Ok(new RequeuedJob(newId))
                : TypedResults.NotFound();
        }
        catch (InvalidOperationException e)
        {
            // The job has not finished. A client error, and one the operator can act on — cancel
            // first — so it must not read as a server fault.
            return TypedResults.Conflict(e.Message);
        }
    }

    /// <param name="Id">The new job's id.</param>
    private sealed record RequeuedJob(JobId Id);

    private static async Task<Results<Ok, NotFound>> RunJobNowAsync(
        IJobClient jobs, Guid id, CancellationToken ct)
        => await jobs.RunNowAsync(new JobId(id), ct) ? TypedResults.Ok() : TypedResults.NotFound();

    private static async Task<Results<Ok, NotFound>> TriggerRecurringAsync(
        IJobClient jobs, string id, CancellationToken ct)
        => await jobs.TriggerRecurringAsync(id, ct) ? TypedResults.Ok() : TypedResults.NotFound();

    private static async Task<Results<Accepted, NotFound, BadRequest<string>>> RecoverCompensationAsync(
        IWorkflowClient workflows, Guid id, string action, CancellationToken ct)
    {
        if (!Enum.TryParse<CompensationRecovery>(action, ignoreCase: true, out var recovery))
        {
            return TypedResults.BadRequest(
                $"Unknown recovery action '{action}'. Expected retry, skip or abandon.");
        }

        // 202, not 200: the decision is carried into the engine by a job, so this reports that it
        // was accepted rather than that the unwind has moved. Saying 200 would promise something
        // the caller could then fail to observe on the very next read.
        return await workflows.RecoverCompensationAsync(new WorkflowInstanceId(id), recovery, ct)
            ? TypedResults.Accepted((string?)null)
            : TypedResults.NotFound();
    }

    private static async Task<Results<Ok, NotFound>> SendSignalAsync(
        IWorkflowClient workflows, string name, string correlationId, HttpRequest request, CancellationToken ct)
    {
        // Taken as raw JSON rather than a typed model: the definition declares the payload type, and
        // the engine binds on its side of the wire (§11.5). That is also what keeps webhook senders
        // possible.
        using var reader = new StreamReader(request.Body);
        var payload = await reader.ReadToEndAsync(ct);

        return await workflows.SignalAsync(name, correlationId, string.IsNullOrWhiteSpace(payload) ? null : payload, ct)
            ? TypedResults.Ok()
            : TypedResults.NotFound();
    }
}
