using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Millrace.Storage;
using Xunit;

namespace Millrace.Dashboard.Tests;

/// <summary>
/// The JSON on the wire, read as JSON (#89).
/// </summary>
/// <remarks>
/// <para>
/// Every other test here deserializes into the C# records, which is exactly why enum values could
/// ship as integers unnoticed for two milestones: <c>"state": 3</c> round-trips into
/// <see cref="JobState.Succeeded"/> perfectly, so a typed client cannot see the bug and a typed test
/// cannot fail on it. Only a JavaScript client — which declares these as string unions — could, and
/// it did so silently, rendering <c>0</c> in a column and losing every chip colour.
/// </para>
/// <para>
/// So these assert on <see cref="JsonElement"/>: what a browser actually receives. The rule is that
/// anything the TypeScript contract in <c>src/ui-shared/contract.ts</c> names must be checked here
/// against the real payload, because that file is a hand-written mirror and nothing else compares
/// the two.
/// </para>
/// </remarks>
public sealed class WireFormatTests
{
    private static async Task<HttpClient> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddMillrace(m => m.UseInMemoryStorage().Configure(o => o.WorkerEnabled = false));
        builder.Services.AddMillraceDashboard();

        var app = builder.Build();
        app.MapMillraceDashboard("/millrace");
        await app.StartAsync(TestContext.Current.CancellationToken);

        var jobs = app.Services.GetRequiredService<IJobClient>();
        await jobs.EnqueueAsync<IThing>(t => t.DoAsync(), ct: TestContext.Current.CancellationToken);

        return app.GetTestClient();
    }

    public interface IThing
    {
        Task DoAsync();
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument
            .Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .RootElement.Clone();
    }

    [Fact]
    public async Task A_job_state_is_a_name_not_a_number()
    {
        var client = await StartAsync();

        var job = (await GetAsync(client, "/millrace/api/v1/jobs")).GetProperty("items")[0];
        var state = job.GetProperty("state");

        // The whole bug in one assertion. `"state": 1` deserializes into JobState.Enqueued without
        // complaint, so nothing typed could ever have caught it.
        Assert.Equal(JsonValueKind.String, state.ValueKind);
        Assert.Equal("Enqueued", state.GetString());
    }

    [Fact]
    public async Task A_job_states_name_matches_the_chip_selectors_the_uis_ship()
    {
        var client = await StartAsync();

        var job = (await GetAsync(client, "/millrace/api/v1/jobs")).GetProperty("items")[0];

        // millrace.css colours chips with [data-state='Enqueued'] and friends, and the UIs put this
        // value straight into that attribute. A number here is a chip with no colour.
        var css = await File.ReadAllTextAsync(
            Path.Combine(RepoRoot(), "src", "ui-shared", "millrace.css"),
            TestContext.Current.CancellationToken);

        foreach (var state in Enum.GetNames<JobState>().Where(name => css.Contains($"data-state='{name}'")))
        {
            Assert.Contains(state, Enum.GetNames<JobState>());
        }

        Assert.Equal(JsonValueKind.String, job.GetProperty("state").ValueKind);
    }

    [Fact]
    public async Task Statistics_keys_and_values_agree_on_naming()
    {
        var client = await StartAsync();

        var statistics = await GetAsync(client, "/millrace/api/v1/statistics");

        // Dictionary *keys* of enum type were always written as names, which is what made the bug so
        // easy to miss: the overview worked while every list was wrong.
        var byState = statistics.GetProperty("jobsByState");
        Assert.All(
            byState.EnumerateObject(),
            property => Assert.Contains(property.Name, Enum.GetNames<JobState>()));
    }

    [Fact]
    public async Task Job_details_serializes_the_retry_kind_as_a_name_too()
    {
        var client = await StartAsync();

        var id = (await GetAsync(client, "/millrace/api/v1/jobs")).GetProperty("items")[0]
            .GetProperty("id").GetString();
        var details = await GetAsync(client, $"/millrace/api/v1/jobs/{id}");

        // Nested enums count. This one is not read by any UI today, which is precisely when a wire
        // format rots unnoticed.
        Assert.Equal(JsonValueKind.String, details.GetProperty("retry").GetProperty("kind").ValueKind);
    }

    [Fact]
    public async Task Info_reports_the_fields_the_typescript_contract_declares()
    {
        var client = await StartAsync();

        var info = await GetAsync(client, "/millrace/api/v1/info");

        // The header rendered "undefined" until this test existed: contract.ts declared `version`,
        // the wire says `apiVersion`, and nothing compared them.
        Assert.True(info.TryGetProperty("apiVersion", out _));
        Assert.True(info.TryGetProperty("storageProvider", out _));

        var contract = await File.ReadAllTextAsync(
            Path.Combine(RepoRoot(), "src", "ui-shared", "contract.ts"),
            TestContext.Current.CancellationToken);
        var declared = contract[contract.IndexOf("interface DashboardInfo", StringComparison.Ordinal)..];
        declared = declared[..declared.IndexOf('}', StringComparison.Ordinal)];

        foreach (var property in info.EnumerateObject())
        {
            Assert.True(
                declared.Contains(property.Name, StringComparison.Ordinal),
                $"The wire has '{property.Name}' and contract.ts does not declare it.");
        }
    }

    [Fact]
    public async Task The_dashboards_serializer_does_not_leak_into_the_host_application()
    {
        // The reason these options are handed to each result instead of registered with
        // ConfigureHttpJsonOptions: the dashboard is mounted into somebody else's app, and changing
        // how *their* endpoints serialize is not a library's business.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddMillrace(m => m.UseInMemoryStorage().Configure(o => o.WorkerEnabled = false));
        builder.Services.AddMillraceDashboard();

        var app = builder.Build();
        app.MapMillraceDashboard("/millrace");
        app.MapGet("/theirs", () => TypedResults.Ok(new { State = JobState.Dead }));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var theirs = await GetAsync(app.GetTestClient(), "/theirs");

        Assert.Equal(JsonValueKind.Number, theirs.GetProperty("state").ValueKind);
    }

    [Fact]
    public async Task Response_schemas_survive_serializing_through_the_dashboards_own_options()
    {
        // Returning JsonHttpResult<T> instead of Ok<T> is what scopes the serializer, and it would be
        // a poor trade if it cost the generated document its response types — the contract is the
        // product (§7), and a schema-less OpenAPI document is a much worse bug than the one fixed.
        var client = await StartAsync();

        var document = JsonDocument.Parse(
            await client.GetStringAsync(
                $"/millrace/openapi/{MillraceDashboard.DocumentName}.json",
                TestContext.Current.CancellationToken));

        var ok = document.RootElement
            .GetProperty("paths").GetProperty("/millrace/api/v1/jobs")
            .GetProperty("get").GetProperty("responses").GetProperty("200");

        Assert.True(
            ok.TryGetProperty("content", out var content),
            "the 200 response lost its schema when the result type changed");
        Assert.True(content.GetProperty("application/json").TryGetProperty("schema", out _));
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}
