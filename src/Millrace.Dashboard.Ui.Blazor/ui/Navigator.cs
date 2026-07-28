namespace Millrace.Dashboard.Ui.Blazor.App;

/// <summary>
/// Moves between views. Cascaded from the shell so every internal link navigates the same way.
/// </summary>
/// <remarks>
/// Exists because a plain <c>&lt;a href="#/jobs"&gt;</c> does not work in a Blazor app. Blazor
/// intercepts same-origin anchor clicks and navigates with <c>history.pushState</c>, and for a
/// fragment-only link that raises nothing at all — not <c>hashchange</c>, not
/// <c>LocationChanged</c>. The URL changes and the page does not.
/// <para>
/// So links call this instead of relying on the browser, and <see cref="RouteLink"/> is what every
/// view uses rather than an anchor. The <c>href</c> is still real, so middle-click, copy-link and
/// the status bar all behave — only the left-click path is taken over.
/// </para>
/// </remarks>
public sealed class Navigator(Func<string, Task> go)
{
    /// <summary>Navigates to a route such as <c>/jobs</c> or <c>/jobs/{id}</c>.</summary>
    public Task GoAsync(string route) => go(route);
}
