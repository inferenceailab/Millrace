using Microsoft.Extensions.DependencyInjection.Extensions;
using Millrace.Dashboard;
using Millrace.Dashboard.Ui.React;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the official React UI:
/// <c>services.AddMillraceDashboard().AddMillraceReactUi()</c>, then the existing
/// <c>app.MapMillraceDashboard("/millrace")</c> serves it at <c>/millrace/ui</c>.
/// </summary>
public static class MillraceReactUiServiceCollectionExtensions
{
    /// <summary>
    /// Adds the embedded React bundle as the dashboard's UI.
    /// </summary>
    /// <remarks>
    /// A consumer references exactly one UI package (§7). <c>TryAdd</c> means referencing two does
    /// not silently pick a winner by registration order — the first registered stands, and the mount
    /// log line names which UI is being served.
    /// </remarks>
    public static IServiceCollection AddMillraceReactUi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IMillraceDashboardUi, ReactDashboardUi>();
        return services;
    }
}
