using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Millrace.Storage;
using Millrace.Storage.InMemory;
using Millrace.Storage.Monitoring;
using Xunit;

namespace Millrace.Dashboard.Tests;

/// <summary>
/// The read-only endpoints over a real HTTP surface (#26–#30).
/// </summary>
/// <remarks>
/// These exercise things a storage-level test cannot: query-string binding, the three-way tenant
/// filter expressed as two parameters, and that a bad cursor from a client is a 400 rather than a
/// 500.
/// </remarks>
public sealed class MonitoringEndpointTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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

        // Drop the worker and scheduler. These tests seed jobs and then read them back; with the
        // worker pool running it would claim and execute them mid-test, so the dashboard would be
        // reporting a race rather than the rows under test. Removed by namespace so this does not
        // also strip the host's own IHostedService, which is what runs the test server.
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

    private static JobRecord Job(
        string queue = "default", JobState state = JobState.Enqueued, string? tenantId = null,
        DateTimeOffset? createdAt = null) => new()
    {
        Id = JobId.New(),
        Queue = queue,
        State = state,
        Invocation = new JobInvocation
        {
            TypeName = "Sample.IReports, Sample",
            MethodName = "GenerateAsync",
            ParameterTypes = ["System.Int32, System.Private.CoreLib"],
            ArgumentsJson = ["42"],
        },
        Retry = Retry.None,
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        TenantId = tenantId,
    };

    private async Task<T> GetAsync<T>(string url)
    {
        var response = await _client.GetAsync(url, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Json, TestContext.Current.CancellationToken))!;
    }

    // ---------------------------------------------------------------- #26 statistics

    [Fact]
    public async Task Statistics_report_counts_by_state_and_queue()
    {
        await _storage.EnqueueAsync([Job(), Job(), Job(queue: "reports")], TestContext.Current.CancellationToken);

        var stats = await GetAsync<JobStatistics>("/millrace/api/v1/statistics");

        Assert.Equal(3, stats.JobsByState[JobState.Enqueued]);
        Assert.Equal(2, stats.EnqueuedByQueue["default"]);
        Assert.Equal(1, stats.EnqueuedByQueue["reports"]);
    }

    [Fact]
    public async Task Statistics_honour_the_tenant_query_parameters()
    {
        await _storage.EnqueueAsync(
            [Job(), Job(tenantId: "acme"), Job(tenantId: "acme")], TestContext.Current.CancellationToken);

        var any = await GetAsync<JobStatistics>("/millrace/api/v1/statistics");
        var acme = await GetAsync<JobStatistics>("/millrace/api/v1/statistics?tenant=acme");
        var untenanted = await GetAsync<JobStatistics>("/millrace/api/v1/statistics?untenanted=true");

        Assert.Equal(3, any.JobsByState[JobState.Enqueued]);
        Assert.Equal(2, acme.JobsByState[JobState.Enqueued]);
        Assert.Equal(1, untenanted.JobsByState[JobState.Enqueued]);
    }

    // ---------------------------------------------------------------- #27 job list

    [Fact]
    public async Task Jobs_are_returned_newest_first_and_carry_no_total()
    {
        var older = Job(createdAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var newer = Job(createdAt: DateTimeOffset.UtcNow);
        await _storage.EnqueueAsync([older, newer], TestContext.Current.CancellationToken);

        var raw = await _client.GetStringAsync("/millrace/api/v1/jobs", TestContext.Current.CancellationToken);
        var page = await GetAsync<Page<JobSummary>>("/millrace/api/v1/jobs");

        Assert.Equal([newer.Id, older.Id], page.Items.Select(i => i.Id).ToList());
        // §11.12: no total, so no client can render a page number.
        Assert.DoesNotContain("total", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Job_list_filters_by_repeated_state_parameters()
    {
        var enqueued = Job();
        var scheduled = Job(state: JobState.Scheduled);
        await _storage.EnqueueAsync([enqueued, scheduled], TestContext.Current.CancellationToken);

        var single = await GetAsync<Page<JobSummary>>("/millrace/api/v1/jobs?state=Scheduled");
        var both = await GetAsync<Page<JobSummary>>("/millrace/api/v1/jobs?state=Scheduled&state=Enqueued");

        Assert.Equal(scheduled.Id, Assert.Single(single.Items).Id);
        Assert.Equal(2, both.Items.Count);
    }

    [Fact]
    public async Task Job_list_pages_by_opaque_cursor()
    {
        var now = DateTimeOffset.UtcNow;
        var jobs = Enumerable.Range(0, 5).Select(i => Job(createdAt: now.AddSeconds(-i))).ToList();
        await _storage.EnqueueAsync(jobs, TestContext.Current.CancellationToken);

        var first = await GetAsync<Page<JobSummary>>("/millrace/api/v1/jobs?limit=2");
        Assert.NotNull(first.NextCursor);

        var second = await GetAsync<Page<JobSummary>>(
            $"/millrace/api/v1/jobs?limit=2&cursor={Uri.EscapeDataString(first.NextCursor)}");

        Assert.Equal(2, first.Items.Count);
        Assert.Equal(2, second.Items.Count);
        Assert.Empty(first.Items.Select(i => i.Id).Intersect(second.Items.Select(i => i.Id)));
    }

    [Fact]
    public async Task A_rejected_cursor_is_a_client_error_not_a_server_fault()
    {
        var response = await _client.GetAsync(
            "/millrace/api/v1/jobs?cursor=!!nonsense!!", TestContext.Current.CancellationToken);

        // A cursor arrives from a query string, so garbage in it is the caller's mistake. Returning
        // 500 would tell the caller nothing and read as an outage in monitoring.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Job_summaries_omit_the_serialized_arguments()
    {
        await _storage.EnqueueAsync([Job()], TestContext.Current.CancellationToken);

        var raw = await _client.GetStringAsync("/millrace/api/v1/jobs", TestContext.Current.CancellationToken);

        // The list ships no payloads: arguments routinely carry personal data and no column shows them.
        Assert.DoesNotContain("argumentsJson", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GenerateAsync", raw, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- #28 job detail

    [Fact]
    public async Task Job_detail_carries_arguments_and_the_interruption_count()
    {
        var job = Job();
        await _storage.EnqueueAsync([job], TestContext.Current.CancellationToken);

        var details = await GetAsync<JobDetails>($"/millrace/api/v1/jobs/{job.Id.Value}");

        Assert.Equal(job.Id, details.Summary.Id);
        Assert.Equal(["42"], details.Invocation.ArgumentsJson);
        Assert.Equal(0, details.Summary.Interruptions);
    }

    [Fact]
    public async Task Job_detail_is_404_for_an_unknown_id()
    {
        var response = await _client.GetAsync(
            $"/millrace/api/v1/jobs/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Job_detail_rejects_a_non_guid_id_by_not_matching_the_route()
    {
        var response = await _client.GetAsync(
            "/millrace/api/v1/jobs/not-a-guid", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------- #29 recurring

    [Fact]
    public async Task Recurring_definitions_are_listed_soonest_first()
    {
        var now = DateTimeOffset.UtcNow;
        await _storage.UpsertRecurringAsync(Recurring("nightly", now.AddHours(6)), TestContext.Current.CancellationToken);
        await _storage.UpsertRecurringAsync(Recurring("hourly", now.AddMinutes(30)), TestContext.Current.CancellationToken);

        var page = await GetAsync<Page<RecurringSummary>>("/millrace/api/v1/recurring");

        Assert.Equal(["hourly", "nightly"], page.Items.Select(r => r.Id).ToList());
        Assert.All(page.Items, r => Assert.Equal("* * * * *", r.Cron));
        // No outcome field exists to populate — see #61.
        Assert.All(page.Items, r => Assert.Null(r.LastFireTime));

        RecurringJobRecord Recurring(string id, DateTimeOffset next) => new()
        {
            Id = id,
            Cron = "* * * * *",
            Queue = "default",
            Invocation = Job().Invocation,
            Retry = Retry.None,
            NextFireTime = next,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    // ---------------------------------------------------------------- #30 instances

    [Fact]
    public async Task Instances_are_listed_and_filtered_by_definition()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (definition, index) in new[] { "alpha", "alpha", "beta" }.Select((d, i) => (d, i)))
        {
            await _storage.CreateInstanceAsync(
                new WorkflowInstanceRecord
                {
                    Id = WorkflowInstanceId.New(),
                    DefinitionId = definition,
                    DefinitionVersion = 1,
                    State = WorkflowInstanceState.Running,
                    DataJson = """{"v":1}""",
                    Revision = 1,
                    CreatedAt = now.AddSeconds(index),
                    UpdatedAt = now.AddSeconds(index),
                },
                TestContext.Current.CancellationToken);
        }

        var all = await GetAsync<Page<WorkflowInstanceSummary>>("/millrace/api/v1/instances");
        var alpha = await GetAsync<Page<WorkflowInstanceSummary>>("/millrace/api/v1/instances?definitionId=alpha");

        Assert.Equal(3, all.Items.Count);
        Assert.Equal(2, alpha.Items.Count);
    }

    [Fact]
    public async Task Instance_summaries_omit_the_data_document()
    {
        await _storage.CreateInstanceAsync(
            new WorkflowInstanceRecord
            {
                Id = WorkflowInstanceId.New(),
                DefinitionId = "alpha",
                DefinitionVersion = 1,
                State = WorkflowInstanceState.Running,
                DataJson = """{"secret":"do-not-ship-this"}""",
                Revision = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            TestContext.Current.CancellationToken);

        var raw = await _client.GetStringAsync("/millrace/api/v1/instances", TestContext.Current.CancellationToken);

        Assert.DoesNotContain("do-not-ship-this", raw, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- contract document

    [Fact]
    public async Task Every_read_endpoint_appears_in_the_openapi_document()
    {
        var document = await _client.GetStringAsync(
            $"/millrace/openapi/{MillraceDashboard.DocumentName}.json", TestContext.Current.CancellationToken);

        foreach (var path in new[] { "statistics", "jobs", "recurring", "instances" })
        {
            Assert.Contains($"/millrace/api/v1/{path}", document, StringComparison.Ordinal);
        }
    }
}
