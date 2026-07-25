using Millrace.Dashboard;

namespace Millrace.Dashboard.Ui.React;

/// <summary>
/// Serves the prebuilt React bundle from embedded resources.
/// </summary>
/// <remarks>
/// The bundle is compiled into this assembly at build time (see the csproj), so a consumer installs
/// one NuGet package and never touches Node, npm or a CDN — §7's requirement, and the reason the
/// assets are resources rather than files on disk. The lookup is
/// <see cref="EmbeddedBundleUi"/>'s, shared with the Angular package.
/// </remarks>
internal sealed class ReactDashboardUi()
    : EmbeddedBundleUi(typeof(ReactDashboardUi).Assembly, "React");
