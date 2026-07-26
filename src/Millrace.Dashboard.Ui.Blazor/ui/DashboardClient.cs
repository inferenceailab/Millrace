using System.Net.Http.Json;
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
    // Concatenated rather than resolved against HttpClient.BaseAddress, which discards the base
    // path the moment a request begins with "/". The routes have to keep their leading slash: it is
    // what the contract calls them, and what the parity check looks for in this assembly.
    private string Url(string route) => apiBase + route;

    /// <summary>Contract version and which provider is behind this mount.</summary>
    public Task<DashboardInfo?> GetInfoAsync(CancellationToken ct = default)
        => http.GetFromJsonAsync<DashboardInfo>(Url("/info"), ct);

    /// <summary>Counts by state, for the header.</summary>
    public Task<JobStatistics?> GetStatisticsAsync(CancellationToken ct = default)
        => http.GetFromJsonAsync<JobStatistics>(Url("/statistics"), ct);

    /// <summary>A page of jobs, newest first, optionally filtered to one state.</summary>
    public Task<Page<JobSummary>?> GetJobsAsync(JobState? state, CancellationToken ct = default)
        => http.GetFromJsonAsync<Page<JobSummary>>(
            Url(state is null ? "/jobs" : $"/jobs?state={state}"), ct);

    /// <summary>Everything about one job, including its arguments and attempt history.</summary>
    public Task<JobDetails?> GetJobAsync(JobId id, CancellationToken ct = default)
        => http.GetFromJsonAsync<JobDetails>(Url($"/jobs/{id}"), ct);

    /// <summary>A page of workflow instances.</summary>
    public Task<Page<WorkflowInstanceSummary>?> GetInstancesAsync(CancellationToken ct = default)
        => http.GetFromJsonAsync<Page<WorkflowInstanceSummary>>(Url("/instances"), ct);

    /// <summary>Every recurring definition and what became of its last run.</summary>
    public Task<Page<RecurringSummary>?> GetRecurringAsync(CancellationToken ct = default)
        => http.GetFromJsonAsync<Page<RecurringSummary>>(Url("/recurring"), ct);

    /// <summary>Asks a job to stop. Cooperative while it is running — see <c>IJobClient.CancelAsync</c>.</summary>
    public Task<HttpResponseMessage> CancelAsync(JobId id, CancellationToken ct = default)
        => http.PostAsync(Url($"/jobs/{id}/cancel"), content: null, ct);

    /// <summary>Runs a finished job again, as a new job linked back to it.</summary>
    public Task<HttpResponseMessage> RequeueAsync(JobId id, CancellationToken ct = default)
        => http.PostAsync(Url($"/jobs/{id}/requeue"), content: null, ct);

    /// <summary>Shortens a retry backoff without spending retry budget (§11.32).</summary>
    public Task<HttpResponseMessage> RunNowAsync(JobId id, CancellationToken ct = default)
        => http.PostAsync(Url($"/jobs/{id}/run-now"), content: null, ct);

    /// <summary>Fires a recurring definition now, leaving its schedule alone.</summary>
    public Task<HttpResponseMessage> TriggerRecurringAsync(string id, CancellationToken ct = default)
        => http.PostAsync(Url($"/recurring/{id}/trigger"), content: null, ct);

    /// <summary>Moves a suspended unwind forward on an operator's instruction (§11.30).</summary>
    public Task<HttpResponseMessage> ResolveCompensationAsync(
        WorkflowInstanceId id, string action, CancellationToken ct = default)
        => http.PostAsync(Url($"/instances/{id}/compensation/{action}"), content: null, ct);

    /// <summary>Delivers a signal to whichever instance is waiting on it.</summary>
    public Task<HttpResponseMessage> SignalAsync(
        string name, string correlationId, CancellationToken ct = default)
        => http.PostAsync(Url($"/signals/{name}/{correlationId}"), content: null, ct);

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
