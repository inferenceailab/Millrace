using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Millrace.Dashboard.Ui.Angular;
using Millrace.Dashboard.Ui.Blazor;
using Millrace.Dashboard.Ui.React;
using Xunit;

namespace Millrace.Dashboard.Tests;

/// <summary>
/// Every official UI covers the whole contract (#46).
/// </summary>
/// <remarks>
/// <para>
/// §11.4 committed to several UIs over one REST contract — "designed once in the contract, rendered
/// three times". Nothing enforced the second half, and it had already failed: the React UI shipped
/// covering only the six monitoring endpoints, so the four management ones — cancel, requeue,
/// trigger, signal — existed in the API, in the OpenAPI document, and nowhere an operator could
/// reach them.
/// </para>
/// <para>
/// That is the failure this guards, and it is a failure of <em>addition</em>: an endpoint added to
/// the contract is covered by whichever UI its author happened to be working in. So the check is
/// driven from the route table rather than from a list someone maintains — a new endpoint fails
/// every UI that has not caught up, on the commit that adds it.
/// </para>
/// </remarks>
public sealed class ContractParityTests
{
    /// <summary>
    /// Every route the dashboard mounts under <c>/api/v1</c>, read from the live route table.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ContractRoutesAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddMillrace(m => m.UseInMemoryStorage());
        builder.Services.AddMillraceDashboard();

        await using var app = builder.Build();
        app.MapMillraceDashboard("/millrace");
        await app.StartAsync(TestContext.Current.CancellationToken);

        return [.. app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .Where(route => route.Contains("/api/v1/", StringComparison.Ordinal))
            .Select(route => route[(route.IndexOf("/api/v1/", StringComparison.Ordinal) + "/api/v1".Length)..])
            .Distinct()
            .Order(StringComparer.Ordinal)];
    }

    public static TheoryData<string, Type> Uis => new()
    {
        { "React", typeof(ReactDashboardUi) },
        { "Angular", typeof(AngularDashboardUi) },
        { "Blazor", typeof(BlazorDashboardUi) },
    };

    [Theory]
    [MemberData(nameof(Uis))]
    public async Task Every_ui_reaches_every_contract_endpoint(string name, Type uiType)
    {
        var routes = await ContractRoutesAsync();
        Assert.NotEmpty(routes);

        var bundle = ReadBundle(uiType);
        var missing = routes.Where(route => !Reaches(bundle, route)).ToList();

        Assert.True(
            missing.Count == 0,
            $"The {name} bundle never mentions {string.Join(", ", missing)}. Every official UI is a "
            + "client of the whole v1 contract (§11.4) — an endpoint no UI reaches is one an operator "
            + "cannot use. Add it, or remove it from the contract.");
    }

    /// <summary>
    /// Whether a bundle contains every literal fragment of a route.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A UI builds <c>/jobs/{id:guid}/cancel</c> as <c>`/jobs/${id}/cancel`</c>, so the parameter
    /// vanishes but <c>/jobs/</c> and <c>/cancel</c> both survive minification as string literals.
    /// Checking every fragment rather than just the prefix is what gives this teeth: the prefix
    /// alone is <c>/jobs/</c>, which every bundle contains anyway, so a prefix check would have
    /// passed against the very React bundle whose missing management actions prompted this test.
    /// </para>
    /// <para>
    /// Single-character fragments are dropped — the separator between two adjacent parameters in
    /// <c>/signals/{name}/{correlationId}</c> is a bare <c>/</c> and says nothing.
    /// </para>
    /// <para>
    /// It remains one-directional: a bundle that never mentions an endpoint certainly does not call
    /// it, while one that does might still not render it usefully. It catches the failure that
    /// actually happened — an endpoint added and a UI forgotten — and does not pretend to catch more.
    /// </para>
    /// </remarks>
    private static bool Reaches(Bundle bundle, string route) =>
        LiteralFragments(route).All(bundle.Mentions);

    private static IEnumerable<string> LiteralFragments(string route) =>
        Regex.Split(route, @"\{[^}]*\}").Where(fragment => fragment.Length > 1);

    /// <summary>
    /// A UI's embedded bundle, in whichever form its routes actually survive into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a JavaScript UI that is the scripts, as text. For Blazor it cannot be: its routes are in
    /// the compiled <c>_framework/*.wasm</c> assemblies, and its JavaScript is the framework loader,
    /// which mentions no contract endpoint at all. Reading only scripts there would report a bundle
    /// that reaches nothing — and reading the assemblies as text would find nothing either, because
    /// .NET string literals are UTF-16.
    /// </para>
    /// <para>
    /// So a fragment is searched as text in the scripts and as UTF-16 bytes in the assemblies. The
    /// claim is unchanged and still one-directional: a bundle that never mentions an endpoint
    /// certainly does not call it.
    /// </para>
    /// </remarks>
    private sealed class Bundle
    {
        private readonly string _scripts;
        private readonly IReadOnlyList<byte[]> _assemblies;

        public Bundle(Type uiType)
        {
            var assembly = uiType.Assembly;
            var resources = assembly.GetManifestResourceNames();

            _scripts = string.Join('\n', Read(assembly, resources, ".js").Select(
                bytes => System.Text.Encoding.UTF8.GetString(bytes)));

            _assemblies = Read(assembly, resources, ".wasm", ".dll");

            // A UI that embedded neither is not a bundle, and every assertion below would pass
            // vacuously against it.
            Assert.True(
                _scripts.Length > 0 || _assemblies.Count > 0,
                $"{uiType.Name} embeds no scripts and no assemblies.");
        }

        public bool Mentions(string fragment)
        {
            if (_scripts.Contains(fragment, StringComparison.Ordinal))
            {
                return true;
            }

            var utf16 = System.Text.Encoding.Unicode.GetBytes(fragment);
            return _assemblies.Any(bytes => Contains(bytes, utf16));
        }

        private static List<byte[]> Read(
            Assembly assembly, string[] resources, params string[] extensions) =>
            [.. resources
                .Where(r => extensions.Any(e => r.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                .Select(r =>
                {
                    using var stream = assembly.GetManifestResourceStream(r)!;
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    return buffer.ToArray();
                })];

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            for (var i = 0; i + needle.Length <= haystack.Length; i++)
            {
                var match = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private static Bundle ReadBundle(Type uiType) => new(uiType);

    [Fact]
    public async Task The_two_uis_agree_on_which_endpoints_they_reach()
    {
        // Parity is symmetric: neither UI is the reference. Without this, both could pass the check
        // above while covering the contract in different orders of completeness — which is exactly
        // how "the React one has a page for that" starts.
        var routes = await ContractRoutesAsync();
        var react = ReadBundle(typeof(ReactDashboardUi));
        var angular = ReadBundle(typeof(AngularDashboardUi));

        var differing = routes.Where(route => Reaches(react, route) != Reaches(angular, route)).ToList();

        Assert.True(differing.Count == 0, $"Only one UI reaches: {string.Join(", ", differing)}");
    }

    [Fact]
    public void Both_uis_share_one_contract_client_rather_than_declaring_their_own()
    {
        // The structural half. The check above is about behaviour today; this is about how the next
        // endpoint gets added — from one shared module, both UIs get it by rebuilding, and there is
        // no second copy of the types to forget.
        var shared = Path.Combine(RepoRoot(), "src", "ui-shared");
        Assert.True(File.Exists(Path.Combine(shared, "api.ts")));
        Assert.True(File.Exists(Path.Combine(shared, "contract.ts")));

        foreach (var ui in new[] { "Millrace.Dashboard.Ui.React", "Millrace.Dashboard.Ui.Angular" })
        {
            var sources = Directory
                .EnumerateFiles(Path.Combine(RepoRoot(), "src", ui, "ui", "src"), "*.ts*",
                    SearchOption.AllDirectories)
                .Select(File.ReadAllText)
                .ToList();

            Assert.True(
                sources.Any(source => source.Contains("ui-shared/api", StringComparison.Ordinal)),
                $"{ui} does not import the shared contract client.");

            // A `fetch` outside the shared client is a second API surface growing quietly.
            var strays = sources.Count(source => Regex.IsMatch(source, @"\bfetch\s*\("));
            Assert.True(
                strays == 0,
                $"{ui} calls fetch directly in {strays} file(s); requests belong in src/ui-shared/api.ts.");
        }
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}
