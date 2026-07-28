using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Millrace.Storage;
using Millrace.Storage.Monitoring;

namespace Millrace.Dashboard.Ui.Blazor.App;

/// <summary>
/// What <c>GET /info</c> returns.
/// </summary>
/// <remarks>
/// The one contract response this app declares for itself rather than sharing. Every other type it
/// reads lives in <c>Millrace</c>, which depends on the BCL alone; this one is declared inside
/// <c>Millrace.Dashboard</c>, an ASP.NET Core assembly a WebAssembly app cannot reference. Two
/// strings, and the parity check would catch the endpoint disappearing — but it is a mirror, and
/// mirrors are what §11.23 set out to avoid.
/// </remarks>
public sealed record DashboardInfo(string ApiVersion, string StorageProvider);

/// <summary>What <c>POST /jobs/{id}/requeue</c> returns: the id of the job it created.</summary>
/// <remarks>
/// Declared here for the same reason as <see cref="DashboardInfo"/> — the server's own type lives in
/// <c>Millrace.Dashboard</c>, an ASP.NET Core assembly a WebAssembly app cannot reference. One
/// property, and the view needs it to navigate to the new job rather than leaving the operator on
/// the finished one they just requeued.
/// </remarks>
public sealed record RequeuedJob(JobId Id);

/// <summary>
/// The whole v1 contract, once, in C#.
/// </summary>
/// <remarks>
/// <para>
/// The React and Angular UIs share a TypeScript client that mirrors the contract types by hand
/// (§11.21). This one does not mirror anything — it deserializes into the same
/// <see cref="JobSummary"/>, <see cref="JobDetails"/> and <see cref="WorkflowInstanceSummary"/> the
/// server writes, so a contract change that would silently break a TypeScript client stops this one
/// compiling. That is the shared core §11.23 said Blazor gets and the others cannot.
/// </para>
/// <para>
/// Every route lives here and nowhere else. The parity check (§11.22) reads the compiled assembly
/// looking for these literals, so a view that invented its own URL would pass unnoticed — keeping
/// them in one place is what makes the check meaningful rather than incidental.
/// </para>
/// </remarks>
/// <param name="http">Transport only; the base address is not used.</param>
/// <param name="apiBase">
/// Absolute base of the v1 API, without a trailing slash — e.g. <c>https://host/millrace/api/v1</c>.
/// </param>
public sealed class DashboardClient(HttpClient http, string apiBase)
{
    /// <summary>Rows per page, matching what the React and Angular UIs request.</summary>
    public const int PageSize = 25;

    /// <summary>
    /// The reading half of <c>DashboardJson.Options</c>, which is what the server writes with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves are required and neither is a default. <see cref="JsonSerializerDefaults.Web"/>
    /// gives the camelCase the server emits, and the enum converters read the string enums §11.24
    /// exists to guarantee — <c>"state": "Scheduled"</c>, not <c>"state": 0</c>. Without them every
    /// list request throws on its first row, which is precisely what this client did until it was
    /// first run in a browser: it compiled, the parity check passed because the routes were all
    /// present, and no test had ever executed a response.
    /// </para>
    /// <para>
    /// <b>Listed one generic converter per enum, not one <c>JsonStringEnumConverter</c> for all of
    /// them.</b> The non-generic form is a factory that builds a converter per enum type by
    /// reflection, and a published Blazor app is trimmed: the factory survives, finds nothing it can
    /// construct for <see cref="JobState"/>, and serialization falls back to numbers. It fails
    /// silently and only in a Release publish — the same shape of "works on my machine" the handoff
    /// keeps collecting, except the machine is the debugger. Naming each enum keeps the types alive
    /// through the trimmer.
    /// </para>
    /// <para>
    /// The server's own options live in an ASP.NET Core assembly a WebAssembly app cannot reference,
    /// so this is a second declaration of one format. <c>BlazorWireFormatTests</c> pins the two
    /// together rather than trusting them to stay in step.
    /// </para>
    /// </remarks>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter<JobState>(),
            new JsonStringEnumConverter<WorkflowInstanceState>(),
            new JsonStringEnumConverter<JobAttemptOutcome>(),
            new JsonStringEnumConverter<RetryKind>(),
        },
    };

    // Concatenated rather than resolved against HttpClient.BaseAddress, which discards the base
    // path the moment a request begins with "/". The routes have to keep their leading slash: it is
    // what the contract calls them, and what the parity check looks for in this assembly.
    private string Url(string route) => apiBase + route;

    /// <summary>Contract version and which provider is behind this mount.</summary>
    public Task<DashboardInfo?> GetInfoAsync(CancellationToken ct = default)
        => http.GetFromJsonAsync<DashboardInfo>(Url("/info"), JsonOptions, ct);

    /// <summary>Counts by state, for the header.</summary>
    public Task<JobStatistics?> GetStatisticsAsync(CancellationToken ct = default)
        => http.GetFromJsonAsync<JobStatistics>(Url("/statistics"), JsonOptions, ct);

    /// <summary>A page of jobs, newest first, optionally filtered by state and queue.</summary>
    public Task<Page<JobSummary>?> GetJobsAsync(
        JobState? state = null,
        string? queue = null,
        string? cursor = null,
        CancellationToken ct = default)
        => http.GetFromJsonAsync<Page<JobSummary>>(
            Url("/jobs") + Query(("state", state?.ToString()), ("queue", queue), ("cursor", cursor)),
            JsonOptions,
            ct);

    /// <summary>Everything about one job, including its arguments and attempt history.</summary>
    public Task<JobDetails?> GetJobAsync(JobId id, CancellationToken ct = default)
        => http.GetFromJsonAsync<JobDetails>(Url($"/jobs/{id}"), JsonOptions, ct);

    /// <summary>A page of workflow instances, optionally filtered to one definition.</summary>
    public Task<Page<WorkflowInstanceSummary>?> GetInstancesAsync(
        string? definitionId = null, string? cursor = null, CancellationToken ct = default)
        => http.GetFromJsonAsync<Page<WorkflowInstanceSummary>>(
            Url("/instances") + Query(("definitionId", definitionId), ("cursor", cursor)),
            JsonOptions,
            ct);

    /// <summary>Every recurring definition and what became of its last run.</summary>
    public Task<Page<RecurringSummary>?> GetRecurringAsync(
        string? cursor = null, CancellationToken ct = default)
        => http.GetFromJsonAsync<Page<RecurringSummary>>(
            Url("/recurring") + Query(("cursor", cursor)), JsonOptions, ct);

    /// <summary>Asks a job to stop. Cooperative while it is running — see <c>IJobClient.CancelAsync</c>.</summary>
    public async Task CancelAsync(JobId id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync(Url($"/jobs/{id}/cancel"), content: null, ct);
        await ThrowIfFailedAsync(response, ct);
    }

    /// <summary>Runs a finished job again, as a new job linked back to it.</summary>
    /// <remarks>
    /// Returns the new job's id so the caller can follow it. A 409 here is the operator's to act on
    /// — the job has not finished yet — so it surfaces as an exception carrying the server's own
    /// explanation rather than as a null that reads like "nothing happened".
    /// </remarks>
    public async Task<RequeuedJob> RequeueAsync(JobId id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync(Url($"/jobs/{id}/requeue"), content: null, ct);
        await ThrowIfFailedAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<RequeuedJob>(JsonOptions, ct))!;
    }

    /// <summary>Shortens a retry backoff without spending retry budget (§11.32).</summary>
    public async Task RunNowAsync(JobId id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync(Url($"/jobs/{id}/run-now"), content: null, ct);
        await ThrowIfFailedAsync(response, ct);
    }

    /// <summary>Fires a recurring definition now, leaving its schedule alone.</summary>
    public async Task TriggerRecurringAsync(string id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync(
            Url($"/recurring/{Uri.EscapeDataString(id)}/trigger"), content: null, ct);
        await ThrowIfFailedAsync(response, ct);
    }

    /// <summary>
    /// Moves a suspended unwind forward on an operator's instruction (§11.30), returning whether
    /// there was anything to move.
    /// </summary>
    /// <remarks>
    /// A 404 means the instance is no longer suspended — a stale button rather than a fault, since
    /// something else may have resolved it first. Re-reading is the right answer either way, so it
    /// returns <see langword="false"/> instead of throwing.
    /// </remarks>
    public async Task<bool> ResolveCompensationAsync(
        WorkflowInstanceId id, string action, CancellationToken ct = default)
    {
        using var response = await http.PostAsync(
            Url($"/instances/{id}/compensation/{action}"), content: null, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await ThrowIfFailedAsync(response, ct);
        return true;
    }

    /// <summary>
    /// Delivers a signal to whichever instance is waiting on <paramref name="name"/> and
    /// <paramref name="correlationId"/>, returning whether one was.
    /// </summary>
    /// <remarks>
    /// <paramref name="payloadJson"/> is sent as the raw body: the workflow definition declares the
    /// payload type and the engine binds on its side of the wire (§11.5), so there is no schema here
    /// to build a form from.
    /// <para>
    /// Delivery is at-most-once, so a 404 means nothing was waiting — a normal answer rather than a
    /// fault, which is why it returns <see langword="false"/> instead of throwing.
    /// </para>
    /// </remarks>
    public async Task<bool> SignalAsync(
        string name, string correlationId, string? payloadJson = null, CancellationToken ct = default)
    {
        using var content = string.IsNullOrWhiteSpace(payloadJson)
            ? null
            : new StringContent(payloadJson, Encoding.UTF8, "application/json");

        using var response = await http.PostAsync(
            Url($"/signals/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(correlationId)}"), content, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await ThrowIfFailedAsync(response, ct);
        return true;
    }

    /// <summary>
    /// Builds a query string from the parameters that were actually given.
    /// </summary>
    /// <remarks>
    /// Values are escaped: a queue name or definition id is operator-supplied text, and one
    /// containing an ampersand would otherwise silently become two parameters. The limit is always
    /// sent so a page size is never left to the server's default drifting.
    /// </remarks>
    private static string Query(params (string Name, string? Value)[] parameters)
    {
        var parts = parameters
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value!)}")
            .Append($"limit={PageSize}");

        return "?" + string.Join('&', parts);
    }

    /// <summary>
    /// Turns a failed response into an exception carrying what the server said.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/> discards the body, which is where
    /// the useful half lives — a 409 from requeue explains that the job has not finished, and
    /// "Response status code does not indicate success: 409" does not.
    /// </remarks>
    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            string.IsNullOrWhiteSpace(body) ? $"{(int)response.StatusCode} {response.ReasonPhrase}" : body,
            inner: null,
            response.StatusCode);
    }

    /// <summary>
    /// Turns the address the bundle was served from into the API base.
    /// </summary>
    /// <remarks>
    /// The UI is served at <c>{prefix}/ui</c> and the API at <c>{prefix}/api/v1</c>, so one prebuilt
    /// bundle works at whatever prefix the consumer mounted — the same reasoning that put
    /// <c>setApiBase()</c> on the shared TypeScript client (§11.23).
    /// </remarks>
    public static string ApiBaseFrom(string uiBaseAddress)
    {
        var trimmed = uiBaseAddress.TrimEnd('/');
        if (trimmed.EndsWith("/ui", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^"/ui".Length];
        }

        return trimmed + "/api/v1";
    }
}
