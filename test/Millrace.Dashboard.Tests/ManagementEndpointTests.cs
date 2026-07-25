using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Millrace.Storage;
using Millrace.Storage.InMemory;
using Xunit;

namespace Millrace.Dashboard.Tests;

/// <summary>
/// The management actions (#41, §7).
/// </summary>
/// <remarks>
/// These mutate, which is what makes §11.13's startup requirement load-bearing: a read-only
/// dashboard left open leaks payloads, an open management surface lets anyone cancel production
/// work. So authorization is asserted here on a write, not only on a read.
/// </remarks>
public sealed class ManagementEndpointTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private InMemoryStorage _storage = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddMillrace(m => m.UseInMemoryStorage());
        builder.Services.AddMillraceDashboard();

        // No worker: these tests assert what the actions did to storage, and a worker racing them
        // would be asserting a race instead.
        foreach (var descriptor in builder.Services
            .Where(s => s.ServiceType == typeof(IHostedService)
                && s.ImplementationType?.Namespace == "Millrace.Workers")
            .ToList())
        {
            builder.Services.Remove(descriptor);
        }

        _app = builder.Build();
        _storage = _app.Services.GetRequiredService<InMemoryStorage>();
        _app.MapMillraceDashboard("/millrace");
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private static JobInvocation Invocation() => new()
    {
        TypeName = "Sample.IService, Sample",
        MethodName = "RunAsync",
        ParameterTypes = [],
        ArgumentsJson = [],
    };

    private async Task<JobRecord> SeedJobAsync()
    {
        var job = new JobRecord
        {
            Id = JobId.New(),
            Queue = "default",
            State = JobState.Enqueued,
            Invocation = Invocation(),
            Retry = Retry.None,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await _storage.EnqueueAsync([job], TestContext.Current.CancellationToken);
        return job;
    }

    [Fact]
    public async Task Cancelling_a_job_moves_it_to_cancelled()
    {
        var job = await SeedJobAsync();

        var response = await _client.PostAsync(
            $"/millrace/api/v1/jobs/{job.Id.Value}/cancel", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await _storage.GetJobAsync(job.Id, TestContext.Current.CancellationToken);
        Assert.Equal(JobState.Cancelled, stored!.State);
    }

    [Fact]
    public async Task Cancelling_an_unknown_or_terminal_job_is_404()
    {
        var job = await SeedJobAsync();
        await _storage.TryCancelAsync(job.Id, TestContext.Current.CancellationToken);

        var unknown = await _client.PostAsync(
            $"/millrace/api/v1/jobs/{Guid.NewGuid()}/cancel", null, TestContext.Current.CancellationToken);
        var terminal = await _client.PostAsync(
            $"/millrace/api/v1/jobs/{job.Id.Value}/cancel", null, TestContext.Current.CancellationToken);

        // Deliberately indistinguishable, as in the storage contract: an operator cannot act on the
        // difference.
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, terminal.StatusCode);
    }

    [Fact]
    public async Task Triggering_a_recurring_definition_enqueues_an_extra_occurrence()
    {
        var next = DateTimeOffset.UtcNow.AddHours(6);
        await _storage.UpsertRecurringAsync(
            new RecurringJobRecord
            {
                Id = "nightly",
                Cron = "0 3 * * *",
                Queue = "default",
                Invocation = Invocation(),
                Retry = Retry.None,
                NextFireTime = next,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            TestContext.Current.CancellationToken);

        var response = await _client.PostAsync(
            "/millrace/api/v1/recurring/nightly/trigger", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var monitoring = _app.Services.GetRequiredService<Millrace.Storage.Monitoring.IMonitoringStorage>();
        var page = await monitoring.QueryJobsAsync(
            new Millrace.Storage.Monitoring.JobQuery(), TestContext.Current.CancellationToken);
        Assert.Single(page.Items);

        // An extra occurrence, not a rescheduled one: the cadence must be untouched.
        var definition = await _storage.GetRecurringAsync("nightly", TestContext.Current.CancellationToken);
        Assert.Equal(next, definition!.NextFireTime);
    }

    [Fact]
    public async Task Triggering_an_unknown_definition_is_404()
    {
        var response = await _client.PostAsync(
            "/millrace/api/v1/recurring/nope/trigger", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Signalling_nothing_waiting_is_404()
    {
        var response = await _client.PostAsync(
            "/millrace/api/v1/signals/approval/order-1",
            new StringContent("""{"IsApproved":true}""", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Management_actions_appear_in_the_openapi_document()
    {
        var document = await _client.GetStringAsync(
            $"/millrace/openapi/{MillraceDashboard.DocumentName}.json", TestContext.Current.CancellationToken);

        Assert.Contains("/cancel", document, StringComparison.Ordinal);
        Assert.Contains("/trigger", document, StringComparison.Ordinal);
        Assert.Contains("/millrace/api/v1/signals/", document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unauthorized_caller_cannot_mutate_anything()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddMillrace(m => m.UseInMemoryStorage());
        builder.Services.AddMillraceDashboard();
        builder.Services.AddMillraceDashboardAuthorization((_, _) => ValueTask.FromResult(false));

        // No worker here either: otherwise it claims the seeded job and dead-letters it, and the
        // assertion below would be measuring the worker rather than the authorization filter.
        foreach (var descriptor in builder.Services
            .Where(s => s.ServiceType == typeof(IHostedService)
                && s.ImplementationType?.Namespace == "Millrace.Workers")
            .ToList())
        {
            builder.Services.Remove(descriptor);
        }

        await using var app = builder.Build();
        var storage = app.Services.GetRequiredService<InMemoryStorage>();
        app.MapMillraceDashboard("/millrace");
        await app.StartAsync(TestContext.Current.CancellationToken);

        var job = new JobRecord
        {
            Id = JobId.New(),
            Queue = "default",
            State = JobState.Enqueued,
            Invocation = Invocation(),
            Retry = Retry.None,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await storage.EnqueueAsync([job], TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().PostAsync(
            $"/millrace/api/v1/jobs/{job.Id.Value}/cancel", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // And the job is untouched — the authorization filter runs before anything reaches storage.
        var stored = await storage.GetJobAsync(job.Id, TestContext.Current.CancellationToken);
        Assert.Equal(JobState.Enqueued, stored!.State);
    }
}
