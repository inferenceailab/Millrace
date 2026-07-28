using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Millrace.Dashboard.Tests;

/// <summary>
/// Serving the embedded React bundle (#32).
/// </summary>
/// <remarks>
/// The point of these is that the bundle is genuinely <em>in the assembly</em> and reachable. A
/// packaging mistake — an empty glob, a mangled logical name — produces a package that builds,
/// installs, and then serves nothing, which no compile-time check would catch.
/// </remarks>
public sealed class ReactUiTests
{
    private static async Task<HttpClient> StartAsync(string prefix = "/millrace", bool withUi = true)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddMillrace(m => m.UseInMemoryStorage());
        builder.Services.AddMillraceDashboard();
        if (withUi)
        {
            builder.Services.AddMillraceReactUi();
        }

        var app = builder.Build();
        app.MapMillraceDashboard(prefix);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app.GetTestClient();
    }

    /// <summary>
    /// The mount root redirects rather than serving the document at two URLs.
    /// </summary>
    /// <remarks>
    /// This asserted a 200 and a body until the Blazor UI was first opened in a browser, where it
    /// rendered nothing: the bundle references its assets relatively, and a browser resolves those
    /// against the directory of the current URL. Without the trailing slash that directory is the
    /// mount prefix, so every asset 404s while the document itself arrives perfectly — which is what
    /// the old assertion was measuring.
    /// </remarks>
    [Fact]
    public async Task The_ui_root_redirects_to_the_trailing_slash_so_relative_assets_resolve()
    {
        var client = await StartAsync();

        var response = await client.GetAsync("/millrace/ui", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/millrace/ui/", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task The_entry_document_is_served_with_a_trailing_slash()
    {
        var client = await StartAsync();

        var response = await client.GetAsync("/millrace/ui/", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<div id=\"root\">", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Assets_are_served_from_the_embedded_bundle_with_a_usable_content_type()
    {
        var client = await StartAsync();

        var script = await client.GetAsync("/millrace/ui/assets/app.js", TestContext.Current.CancellationToken);
        var styles = await client.GetAsync("/millrace/ui/assets/index.css", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
        Assert.True(script.Content.Headers.ContentLength > 1000, "the bundle should not be empty");

        Assert.Equal(HttpStatusCode.OK, styles.StatusCode);
        Assert.Equal("text/css", styles.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task An_asset_in_a_subdirectory_resolves_despite_the_build_machines_path_separator()
    {
        // Embedded names carry whichever separator MSBuild produced — backslash on Windows. A
        // package built on Windows would 404 every nested asset without normalisation, and the
        // failure would only appear at runtime.
        var client = await StartAsync();

        var response = await client.GetAsync("/millrace/ui/assets/app.js", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_client_side_route_falls_back_to_the_entry_document()
    {
        var client = await StartAsync();

        // Deep links must load the application rather than 404.
        var response = await client.GetAsync("/millrace/ui/jobs/some-id", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<div id=\"root\">", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_traversal_attempt_never_escapes_the_bundle()
    {
        var client = await StartAsync();

        var response = await client.GetAsync(
            "/millrace/ui/../../appsettings.json", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Whatever the routing does with the dots, the response is never a file from outside the
        // embedded bundle.
        Assert.DoesNotContain("ConnectionStrings", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_ui_is_mounted_under_the_configured_prefix()
    {
        // The bundle derives the API base from its own path, so it has to live at {prefix}/ui.
        var client = await StartAsync("/ops/millrace");

        var mounted = await client.GetAsync("/ops/millrace/ui/", TestContext.Current.CancellationToken);
        var mountRoot = await client.GetAsync("/ops/millrace/ui", TestContext.Current.CancellationToken);
        var elsewhere = await client.GetAsync("/millrace/ui", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, mounted.StatusCode);
        // The redirect follows the prefix too, rather than being hard-coded to /millrace.
        Assert.Equal(HttpStatusCode.Found, mountRoot.StatusCode);
        Assert.Equal("/ops/millrace/ui/", mountRoot.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.NotFound, elsewhere.StatusCode);
    }

    [Fact]
    public async Task Without_a_ui_package_the_api_still_mounts_and_no_ui_is_served()
    {
        // §7: the API is the product and is fully usable headless.
        var client = await StartAsync(withUi: false);

        var api = await client.GetAsync("/millrace/api/v1/info", TestContext.Current.CancellationToken);
        var ui = await client.GetAsync("/millrace/ui", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, api.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ui.StatusCode);
    }

    [Fact]
    public async Task The_ui_is_guarded_by_the_same_authorization_hook()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddMillrace(m => m.UseInMemoryStorage());
        builder.Services.AddMillraceDashboard();
        builder.Services.AddMillraceReactUi();
        builder.Services.AddMillraceDashboardAuthorization((_, _) => ValueTask.FromResult(false));

        await using var app = builder.Build();
        app.MapMillraceDashboard("/millrace");
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        var response = await client.GetAsync("/millrace/ui", TestContext.Current.CancellationToken);

        // An unauthorized caller gets nothing — not even the shell that would reveal the mount.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
