using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Millrace.Storage;
using Millrace.Storage.InMemory;

namespace Millrace.Dashboard.BrowserTests;

/// <summary>Which official UI package a host serves.</summary>
public enum DashboardUi
{
    /// <summary><c>Millrace.Dashboard.Ui.React</c>.</summary>
    React,

    /// <summary><c>Millrace.Dashboard.Ui.Angular</c>.</summary>
    Angular,

    /// <summary><c>Millrace.Dashboard.Ui.Blazor</c>.</summary>
    Blazor,
}

/// <summary>
/// A real dashboard on a real port, serving one UI over seeded data.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kestrel, not <c>TestServer</c>.</b> The rest of the dashboard tests use an in-memory test
/// server, which has no socket for a browser to connect to. These tests exist because a browser is
/// the only thing that executes the bundle, so the host has to be reachable — port 0 lets the OS
/// pick, and the bound address is read back from the server's own feature rather than guessed.
/// </para>
/// <para>
/// The worker pool and scheduler are removed for the same reason
/// <c>MonitoringEndpointTests</c> removes them: these tests seed jobs into specific states and then
/// look at them, and a running worker would claim and execute them mid-test.
/// </para>
/// </remarks>
public sealed class DashboardBrowserHost : IAsyncDisposable
{
    /// <summary>
    /// The queue every seeded job is written to.
    /// </summary>
    /// <remarks>
    /// Deliberately distinctive, and asserted on. Every UI lists a Queue column, so finding this
    /// string on the page proves the bundle fetched the contract, deserialized it and rendered a
    /// row — the three things that were all broken in #120 while every static check passed.
    /// </remarks>
    public const string ProbeQueue = "browser-probe";

    private readonly WebApplication _app;

    private DashboardBrowserHost(WebApplication app, string baseAddress, InMemoryStorage storage)
    {
        _app = app;
        BaseAddress = baseAddress;
        Storage = storage;
    }

    /// <summary>Where the host is listening, e.g. <c>http://127.0.0.1:53124</c>.</summary>
    public string BaseAddress { get; }

    /// <summary>The seeded store, for arranging more rows or asserting on the result of an action.</summary>
    public InMemoryStorage Storage { get; }

    /// <summary>The mount prefix, matching what the sample and the docs use.</summary>
    public const string Prefix = "/millrace";

    /// <summary>The UI's entry URL, with the trailing slash relative assets need (§11.38).</summary>
    public string UiUrl => $"{BaseAddress}{Prefix}/ui/";

    /// <summary>Starts a host serving <paramref name="ui"/> with a seeded backlog.</summary>
    public static async Task<DashboardBrowserHost> StartAsync(DashboardUi ui, CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

        // Port 0: the OS picks a free one, so parallel hosts never collide.
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services.AddMillrace(m => m.UseInMemoryStorage());
        builder.Services.AddMillraceDashboard();

        switch (ui)
        {
            case DashboardUi.React:
                builder.Services.AddMillraceReactUi();
                break;
            case DashboardUi.Angular:
                builder.Services.AddMillraceAngularUi();
                break;
            case DashboardUi.Blazor:
                builder.Services.AddMillraceBlazorUi();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(ui));
        }

        // Removed by namespace so the host's own IHostedService — the one running Kestrel —
        // survives. Same reasoning, and same trap, as MonitoringEndpointTests.
        foreach (var descriptor in builder.Services
            .Where(s => s.ServiceType == typeof(IHostedService)
                && s.ImplementationType?.Namespace == "Millrace.Workers")
            .ToList())
        {
            builder.Services.Remove(descriptor);
        }

        var app = builder.Build();
        var storage = app.Services.GetRequiredService<InMemoryStorage>();
        app.MapMillraceDashboard(Prefix);
        await app.StartAsync(ct);

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Kestrel reported no bound address.");

        var host = new DashboardBrowserHost(app, address.TrimEnd('/'), storage);
        await host.SeedAsync(ct);
        return host;
    }

    /// <summary>
    /// Writes the backlog the UIs render.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only <see cref="JobState.Scheduled"/> and <see cref="JobState.Enqueued"/>, because those are
    /// the states the storage contract lets anyone <em>insert</em> — a terminal record is reached by
    /// transition, never written directly, and the in-memory provider rejects the attempt. Two
    /// states is enough for what this suite asks: rows exist, and their states differ, so an enum
    /// that failed to deserialize (§11.24) cannot look like a correct render.
    /// </para>
    /// <para>
    /// The <see cref="JobState.Enqueued"/> one is what the cancel test acts on.
    /// </para>
    /// </remarks>
    private async Task SeedAsync(CancellationToken ct)
    {
        JobRecord Job(JobState state, DateTimeOffset? dueAt = null) => new()
        {
            Id = JobId.New(),
            Queue = ProbeQueue,
            State = state,
            DueAt = dueAt,
            Invocation = new JobInvocation
            {
                TypeName = "Sample.IReports, Sample",
                MethodName = "GenerateAsync",
                ParameterTypes = ["System.Int32, System.Private.CoreLib"],
                ArgumentsJson = ["42"],
            },
            Retry = Retry.None,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await Storage.EnqueueAsync(
            [
                Job(JobState.Enqueued),
                Job(JobState.Scheduled, DateTimeOffset.UtcNow.AddHours(1)),
            ],
            ct);
    }

    /// <summary>The id of the one job in <paramref name="state"/>, for acting on or asserting about.</summary>
    public async Task<JobId> JobIdInStateAsync(JobState state, CancellationToken ct = default)
    {
        var page = await Storage.QueryJobsAsync(
            new Storage.Monitoring.JobQuery { States = [state] }, ct);
        var job = page.Items.FirstOrDefault()
            ?? throw new InvalidOperationException($"No seeded job is in {state}.");
        return job.Id;
    }

    /// <summary>Reads a job back, to assert what an action in the browser actually did.</summary>
    public async Task<JobRecord?> GetJobAsync(JobId id, CancellationToken ct = default)
        => await Storage.GetJobAsync(id, ct);

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
