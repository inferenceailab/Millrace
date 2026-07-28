using Microsoft.Playwright;
using Millrace.Storage;
using Xunit;

namespace Millrace.Dashboard.BrowserTests;

/// <summary>
/// Each official UI, executed in a real browser against a real dashboard (#126).
/// </summary>
/// <remarks>
/// <para>
/// Every other check in this repository is static. The §11.22 parity check reads compiled bytes for
/// a substring, <c>UiPackagingTests</c> asserts exported types, the wire-format tests read the
/// TypeScript sources. None of them execute the bundle — which is how the Blazor UI reached
/// <c>main</c> in #120 having never been run, rendering nothing on first load for three independent
/// reasons while 58 dashboard tests passed (§11.38).
/// </para>
/// <para>
/// <b>These tests are written against what the three UIs genuinely share</b>, not against any one
/// framework's DOM. That is the hash-route contract (<c>#/jobs</c>, <c>#/recurring</c>, …), the
/// button labels, and the fact that every list view renders a Queue column. Asserting on framework
/// internals would make this three test suites wearing a trench coat, and it would rot the first
/// time any UI was restyled.
/// </para>
/// </remarks>
public sealed class UiRenderingTests
{
    /// <summary>
    /// Blazor WebAssembly boots a .NET runtime before it renders anything (§11.36 — 6.4 MB of it),
    /// so the first paint is seconds rather than milliseconds. Generous enough for a cold CI runner;
    /// still bounded, because "it never rendered" must fail rather than hang.
    /// </summary>
    private const int RenderTimeoutMs = 60_000;

    public static TheoryData<DashboardUi> AllUis =>
        [DashboardUi.React, DashboardUi.Angular, DashboardUi.Blazor];

    /// <summary>
    /// Errors the browser reported: uncaught exceptions and <c>console.error</c>.
    /// </summary>
    /// <remarks>
    /// Collected from the moment the page is created, because the failures worth catching happen
    /// during boot — a bundle that throws while deserializing its first response has already failed
    /// by the time anything is visible.
    /// </remarks>
    private sealed class ConsoleWatcher
    {
        private readonly List<string> _errors = [];

        public ConsoleWatcher(IPage page)
        {
            page.Console += (_, message) =>
            {
                if (message.Type == "error")
                {
                    lock (_errors) { _errors.Add($"console.error: {message.Text}"); }
                }
            };
            page.PageError += (_, error) =>
            {
                lock (_errors) { _errors.Add($"pageerror: {error}"); }
            };
        }

        public IReadOnlyList<string> Errors
        {
            get { lock (_errors) { return [.. _errors]; } }
        }
    }

    private static async Task<(IBrowserContext Context, IPage Page, ConsoleWatcher Console)> NewPageAsync()
    {
        var browser = await BrowserRequirement.RequireBrowserAsync();
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        return (context, page, new ConsoleWatcher(page));
    }

    /// <summary>
    /// The list view renders rows from the contract — the whole point of the suite.
    /// </summary>
    /// <remarks>
    /// Finding the seeded queue name on the page proves four things at once, every one of which was
    /// broken in #120: the bundle loaded, it executed, it called the API, and it deserialized the
    /// response. A 200 on the document proves none of them.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllUis))]
    public async Task The_jobs_view_renders_seeded_rows(DashboardUi ui)
    {
        await using var host = await DashboardBrowserHost.StartAsync(ui, TestContext.Current.CancellationToken);
        var (context, page, console) = await NewPageAsync();
        await using var _ = context;

        await page.GotoAsync($"{host.UiUrl}#/jobs", new PageGotoOptions { Timeout = RenderTimeoutMs });

        await page.GetByText(DashboardBrowserHost.ProbeQueue).First
            .WaitForAsync(new LocatorWaitForOptions { Timeout = RenderTimeoutMs });

        Assert.Empty(console.Errors);
    }

    /// <summary>
    /// The mount root without a trailing slash renders, rather than arriving blank.
    /// </summary>
    /// <remarks>
    /// This is a <em>server</em> defect that only a browser could find, and it was live for all
    /// three UIs. Every UI references its assets relatively, so serving the document at
    /// <c>{prefix}/ui</c> made the browser resolve them against the prefix: the document arrived
    /// with a 200 and the page stayed blank. The test that covered it asserted that 200 and passed
    /// the whole time (§11.38). This one follows the redirect and then insists something rendered.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllUis))]
    public async Task The_mount_root_without_a_trailing_slash_still_renders(DashboardUi ui)
    {
        await using var host = await DashboardBrowserHost.StartAsync(ui, TestContext.Current.CancellationToken);
        var (context, page, console) = await NewPageAsync();
        await using var _ = context;

        // No trailing slash — the URL a consumer types first.
        await page.GotoAsync(
            $"{host.BaseAddress}{DashboardBrowserHost.Prefix}/ui",
            new PageGotoOptions { Timeout = RenderTimeoutMs });

        await page.GetByRole(AriaRole.Link, new() { Name = "Jobs" })
            .WaitForAsync(new LocatorWaitForOptions { Timeout = RenderTimeoutMs });

        Assert.EndsWith("/ui/", page.Url.Split('#')[0], StringComparison.Ordinal);
        Assert.Empty(console.Errors);
    }

    /// <summary>
    /// A management action, driven through the UI and verified in storage.
    /// </summary>
    /// <remarks>
    /// §11.22's parity check exists because the React UI shipped with no management actions at all,
    /// and it can only prove a route literal is present in a bundle. This proves a button is wired
    /// to it: the assertion is on the job's state in storage, not on anything the page says about
    /// itself.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllUis))]
    public async Task Cancelling_a_job_from_the_ui_cancels_it_in_storage(DashboardUi ui)
    {
        await using var host = await DashboardBrowserHost.StartAsync(ui, TestContext.Current.CancellationToken);
        var jobId = await host.JobIdInStateAsync(JobState.Enqueued, TestContext.Current.CancellationToken);

        var (context, page, console) = await NewPageAsync();
        await using var _ = context;

        await page.GotoAsync($"{host.UiUrl}#/jobs/{jobId}", new PageGotoOptions { Timeout = RenderTimeoutMs });

        var cancel = page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true });
        await cancel.WaitForAsync(new LocatorWaitForOptions { Timeout = RenderTimeoutMs });
        await cancel.ClickAsync();

        // Poll storage rather than the page: what matters is that the request reached the endpoint
        // and changed the record, which is a fact about the system rather than about the rendering.
        var cancelled = await WaitForAsync(
            async () =>
            {
                var job = await host.GetJobAsync(jobId, TestContext.Current.CancellationToken);
                return job?.State == JobState.Cancelled;
            },
            TestContext.Current.CancellationToken);

        Assert.True(cancelled, $"{ui}: clicking Cancel did not cancel the job in storage.");
        Assert.Empty(console.Errors);
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(100, ct);
        }

        return false;
    }
}
