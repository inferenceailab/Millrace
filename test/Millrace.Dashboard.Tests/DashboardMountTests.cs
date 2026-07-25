using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Millrace.Dashboard;
using Xunit;

namespace Millrace.Dashboard.Tests;

/// <summary>
/// Covers what <c>MapMillraceDashboard</c> promises at startup (ARCHITECTURE.md §11.13, §11.14)
/// and the shape of the mounted surface (§11.11).
/// </summary>
/// <remarks>
/// The startup facts matter most. Both guards exist so a misconfiguration surfaces at deploy time
/// rather than as a dashboard that merely looks broken, and neither is observable from a passing
/// request — only from the failure that should have happened.
/// </remarks>
public sealed class DashboardMountTests
{
    private static WebApplication BuildApp(
        string? environment = null,
        bool withStorage = true,
        bool withAuthorization = true,
        bool allowAnonymous = false)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment ?? Environments.Production,
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        if (withStorage)
        {
            builder.Services.AddMillrace(m => m.UseInMemoryStorage());
        }

        builder.Services.AddMillraceDashboard(o => o.AllowAnonymousAccessInsecure = allowAnonymous);

        if (withAuthorization)
        {
            builder.Services.AddMillraceDashboardAuthorization((_, _) => ValueTask.FromResult(true));
        }

        return builder.Build();
    }

    private static async Task<HttpClient> StartAsync(WebApplication app, string prefix = "/millrace")
    {
        app.MapMillraceDashboard(prefix);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app.GetTestClient();
    }

    // ---------------------------------------------------------------- §11.13 authorization

    [Fact]
    public void Mounting_without_authorization_outside_development_throws_at_startup()
    {
        using var app = BuildApp(Environments.Production, withAuthorization: false);

        var ex = Assert.Throws<InvalidOperationException>(() => app.MapMillraceDashboard("/millrace"));

        // The message has to say what to do — a startup failure nobody can action is no better
        // than the silent 404 this replaced.
        Assert.Contains("AddMillraceDashboardAuthorization", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Production", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Mounting_without_authorization_throws_in_staging_too()
    {
        // Only Development is exempt: "not production" is not the test.
        using var app = BuildApp("Staging", withAuthorization: false);

        var ex = Assert.Throws<InvalidOperationException>(() => app.MapMillraceDashboard("/millrace"));
        Assert.Contains("Staging", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Development_allows_anonymous_access()
    {
        await using var app = BuildApp(Environments.Development, withAuthorization: false);
        var client = await StartAsync(app);

        var response = await client.GetAsync("/millrace/api/v1/info", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_insecure_opt_out_permits_anonymous_access_outside_development()
    {
        await using var app = BuildApp(Environments.Production, withAuthorization: false, allowAnonymous: true);
        var client = await StartAsync(app);

        var response = await client.GetAsync("/millrace/api/v1/info", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_denied_request_gets_404_not_403()
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

        await using var app = builder.Build();
        var client = await StartAsync(app);

        var response = await client.GetAsync("/millrace/api/v1/info", TestContext.Current.CancellationToken);

        // An ops surface should not confirm its own existence to an unauthorized caller.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Authorization_also_guards_the_openapi_document()
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

        await using var app = builder.Build();
        var client = await StartAsync(app);

        // The document describes the whole surface; leaving it open would leak the API's shape.
        var response = await client.GetAsync($"/millrace/openapi/{MillraceDashboard.DocumentName}.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------- §11.14 read model

    [Fact]
    public void Mounting_without_a_read_model_throws_naming_the_provider()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        // A provider that implements the job contract but not the read model — exactly the case
        // §11.14 refuses to let reach an end user as a blank dashboard.
        builder.Services.AddMillrace(m => m.UseStorage(_ => new JobsOnlyStorage()));
        builder.Services.AddMillraceDashboard();
        builder.Services.AddMillraceDashboardAuthorization((_, _) => ValueTask.FromResult(true));

        using var app = builder.Build();

        var ex = Assert.Throws<InvalidOperationException>(() => app.MapMillraceDashboard("/millrace"));

        Assert.Contains(nameof(JobsOnlyStorage), ex.Message, StringComparison.Ordinal);
        Assert.Contains("IMonitoringStorage", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Mounting_without_calling_AddMillraceDashboard_says_so()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddMillrace(m => m.UseInMemoryStorage());

        using var app = builder.Build();

        var ex = Assert.Throws<InvalidOperationException>(() => app.MapMillraceDashboard("/millrace"));
        Assert.Contains("AddMillraceDashboard", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- §11.11 surface shape

    [Fact]
    public async Task The_api_is_mounted_under_a_version_segment()
    {
        await using var app = BuildApp();
        var client = await StartAsync(app);

        // The version is a routing fact, not a negotiated header.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/millrace/api/v1/info", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/millrace/api/info", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task The_mount_prefix_is_configurable()
    {
        await using var app = BuildApp();
        var client = await StartAsync(app, "/ops/jobs");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ops/jobs/api/v1/info", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/millrace/api/v1/info", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task The_openapi_document_is_served_and_describes_the_versioned_path()
    {
        await using var app = BuildApp();
        var client = await StartAsync(app);

        var document = await client.GetStringAsync($"/millrace/openapi/{MillraceDashboard.DocumentName}.json", TestContext.Current.CancellationToken);

        Assert.Contains("/millrace/api/v1/info", document, StringComparison.Ordinal);
        Assert.Contains("openapi", document, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_document_excludes_endpoints_the_host_application_declares()
    {
        await using var app = BuildApp();
        // An ungrouped host endpoint must not be swept into our contract document.
        app.MapGet("/host-only", () => "host");
        var client = await StartAsync(app);

        var document = await client.GetStringAsync($"/millrace/openapi/{MillraceDashboard.DocumentName}.json", TestContext.Current.CancellationToken);

        Assert.DoesNotContain("host-only", document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Info_reports_the_contract_version_and_backing_provider()
    {
        await using var app = BuildApp();
        var client = await StartAsync(app);

        var body = await client.GetStringAsync("/millrace/api/v1/info", TestContext.Current.CancellationToken);

        Assert.Contains("\"apiVersion\":\"v1\"", body, StringComparison.Ordinal);
        Assert.Contains("InMemoryStorage", body, StringComparison.Ordinal);
    }
}
