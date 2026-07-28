using Microsoft.Playwright;
using Xunit;

namespace Millrace.Dashboard.BrowserTests;

/// <summary>
/// One Chromium for the whole test run, and the policy deciding whether its absence skips the run
/// or fails it.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately the same shape as <c>PostgresTestDatabase</c>: an explicit
/// <c>MILLRACE_REQUIRE_BROWSER</c> wins, and otherwise strictness defaults to whether <c>CI</c> is
/// set. So <em>every</em> CI job is strict unless it opts out, and the opt-out is visible in the
/// workflow file. The inverse default — opt in per job — leaves the next job someone adds free to
/// skip the whole suite and still report success, which is the failure this suite exists to stop
/// happening to UIs.
/// </para>
/// <para>
/// Skipping is right on a developer machine that has never run <c>playwright install</c>. Reporting
/// success in CI for a run that started no browser is not.
/// </para>
/// </remarks>
internal static class BrowserRequirement
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static IPlaywright? _playwright;
    private static IBrowser? _browser;
    private static bool _unavailable;
    private static Exception? _lastFailure;

    /// <summary>Whether an unavailable browser must fail the run rather than skip it.</summary>
    public static bool IsRequired { get; } = ResolveRequired(
        Environment.GetEnvironmentVariable("MILLRACE_REQUIRE_BROWSER"),
        Environment.GetEnvironmentVariable("CI"));

    /// <summary>Why the last launch attempt failed, for the skip and failure messages.</summary>
    public static Exception? LastFailure => _lastFailure;

    /// <summary>
    /// The strictness policy, kept pure so it can be tested without a browser installed.
    /// </summary>
    /// <param name="explicitFlag">Value of <c>MILLRACE_REQUIRE_BROWSER</c>, if any.</param>
    /// <param name="ciFlag">Value of <c>CI</c>, if any.</param>
    internal static bool ResolveRequired(string? explicitFlag, string? ciFlag)
        => string.IsNullOrWhiteSpace(explicitFlag) ? IsTruthy(ciFlag) : IsTruthy(explicitFlag);

    private static bool IsTruthy(string? value)
    {
        if (value is null)
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed is "1" or "yes" || (bool.TryParse(trimmed, out var parsed) && parsed);
    }

    /// <summary>
    /// The shared browser, or null when none could be launched.
    /// </summary>
    /// <remarks>
    /// One browser process for the whole run, with a fresh context per test — contexts are cheap and
    /// isolated, launches are neither.
    /// </remarks>
    public static async Task<IBrowser?> GetBrowserAsync()
    {
        if (_browser is not null)
        {
            return _browser;
        }

        await Gate.WaitAsync();
        try
        {
            if (_browser is not null)
            {
                return _browser;
            }

            if (_unavailable)
            {
                return null;
            }

            try
            {
                _playwright = await Playwright.CreateAsync();
                _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true,
                });
                return _browser;
            }
            catch (Exception ex)
            {
                _lastFailure = ex;
                _unavailable = true;
                return null;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Returns the browser, or skips — or fails, when this run was expected to have one.
    /// </summary>
    public static async Task<IBrowser> RequireBrowserAsync()
    {
        var browser = await GetBrowserAsync();
        if (browser is not null)
        {
            return browser;
        }

        // Skipping here would report success for a run that rendered nothing, which is precisely the
        // failure §11.38 is about. A run that was supposed to have a browser fails instead.
        if (IsRequired)
        {
            Assert.Fail(
                "No browser could be launched and MILLRACE_REQUIRE_BROWSER (or CI) demands one. "
                + "Run 'pwsh test/Millrace.Dashboard.BrowserTests/bin/<config>/net10.0/playwright.ps1 install chromium' "
                + "to install it. If a job genuinely cannot run a browser, set MILLRACE_REQUIRE_BROWSER=false on "
                + $"that job so the skip is deliberate. Last failure: {LastFailure?.Message ?? "none recorded"}");
        }

        Assert.Skip(
            "No browser available, so the UI rendering tests cannot run. Install one with "
            + "'pwsh test/Millrace.Dashboard.BrowserTests/bin/<config>/net10.0/playwright.ps1 install chromium'. "
            + "Set MILLRACE_REQUIRE_BROWSER=true to make this a failure instead. "
            + $"Last failure: {LastFailure?.Message ?? "none recorded"}");

        throw new InvalidOperationException("unreachable");
    }
}
