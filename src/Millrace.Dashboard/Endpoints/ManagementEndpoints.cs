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

        api.MapPost("/recurring/{id}/trigger", TriggerRecurringAsync)
            .WithName("TriggerRecurring")
            .WithSummary("Fires a recurring definition now, without disturbing its schedule.")
            .WithDescription("An extra occurrence: the next scheduled fire time is unchanged.");

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

    private static async Task<Results<Ok, NotFound>> TriggerRecurringAsync(
        IJobClient jobs, string id, CancellationToken ct)
        => await jobs.TriggerRecurringAsync(id, ct) ? TypedResults.Ok() : TypedResults.NotFound();

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
