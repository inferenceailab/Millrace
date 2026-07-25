using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Millrace.Dashboard;

/// <summary>
/// How the dashboard serializes its responses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enums are written as names</b>, because the contract says <c>"state": "Dead"</c> and every UI
/// declares these as string unions. Without this they were written as integers, which typed C#
/// clients round-tripped invisibly while every JavaScript client silently misread — see §11.24.
/// </para>
/// <para>
/// <b>Scoped deliberately, not global.</b> <c>ConfigureHttpJsonOptions</c> would have been one line,
/// but the dashboard is mounted into somebody else's application: changing the process-wide options
/// would change how <em>their</em> minimal APIs serialize, which is not a library's business. These
/// options are handed to each result instead, so nothing outside the dashboard's own endpoints is
/// affected.
/// </para>
/// <para>
/// It also stops short of the storage layer on purpose. Providers persist <c>Retry</c> as JSON, so
/// converting enums at the type level would have changed a <em>stored</em> format to fix a wire
/// format — a much larger blast radius than the bug.
/// </para>
/// </remarks>
internal static class DashboardJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>200 with a body, serialized with the dashboard's own options.</summary>
    public static JsonHttpResult<T> Ok<T>(T value) => TypedResults.Json(value, Options);
}
