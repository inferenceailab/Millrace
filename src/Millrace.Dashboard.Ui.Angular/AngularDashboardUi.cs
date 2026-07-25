using Millrace.Dashboard;

namespace Millrace.Dashboard.Ui.Angular;

/// <summary>
/// Serves the prebuilt Angular bundle from embedded resources.
/// </summary>
/// <remarks>
/// The serving itself is <see cref="EmbeddedBundleUi"/>'s, shared with the React package: both ship
/// the same way, and a second copy of the resource lookup would only give the two dashboards
/// different ways to fail.
/// </remarks>
internal sealed class AngularDashboardUi()
    : EmbeddedBundleUi(typeof(AngularDashboardUi).Assembly, "Angular");
