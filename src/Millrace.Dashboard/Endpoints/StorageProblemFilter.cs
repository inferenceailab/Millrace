using Microsoft.AspNetCore.Http;

namespace Millrace.Dashboard.Endpoints;

/// <summary>
/// Turns a rejected paging cursor into <c>400 Bad Request</c>.
/// </summary>
/// <remarks>
/// <see cref="Millrace.Storage.MillraceStorageException"/> from a query means the caller supplied a cursor the
/// provider did not issue — cursors arrive straight from a query string, so that is a client error,
/// not a server fault. Without this it would surface as an unhandled exception and a 500, which
/// tells the caller nothing and looks like an outage in monitoring.
/// </remarks>
internal sealed class StorageProblemFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (Millrace.Storage.MillraceStorageException ex)
        {
            return Results.Problem(
                title: "Invalid paging cursor",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
    }
}
