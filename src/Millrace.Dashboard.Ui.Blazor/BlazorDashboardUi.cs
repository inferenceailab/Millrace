using Millrace.Dashboard;

namespace Millrace.Dashboard.Ui.Blazor;

/// <summary>
/// Serves the prebuilt Blazor WebAssembly bundle from embedded resources.
/// </summary>
/// <remarks>
/// <para>
/// The same <see cref="EmbeddedBundleUi"/> lookup the React and Angular packages use — a Blazor
/// bundle is still a directory of static files once published, so nothing about serving it differs.
/// What differs is its size: it carries the .NET WebAssembly runtime, which is roughly 7 MB against
/// their 150–200 KB (§11.36).
/// </para>
/// <para>
/// The app itself renders against the C# contract types rather than a mirror of them, which is the
/// reason §11.23 chose native Blazor over a shared web component.
/// </para>
/// </remarks>
internal sealed class BlazorDashboardUi()
    : EmbeddedBundleUi(typeof(BlazorDashboardUi).Assembly, "Blazor");
