using Microsoft.Extensions.DependencyInjection.Extensions;
using Millrace.Dashboard;
using Millrace.Dashboard.Ui.Blazor;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the official Blazor UI:
/// <c>services.AddMillraceDashboard().AddMillraceBlazorUi()</c>, then the existing
/// <c>app.MapMillraceDashboard("/millrace")</c> serves it at <c>/millrace/ui</c>.
/// </summary>
public static class MillraceBlazorUiServiceCollectionExtensions
{
    /// <summary>
    /// Adds the embedded Blazor WebAssembly bundle as the dashboard's UI.
    /// </summary>
    /// <remarks>
    /// A consumer references exactly one UI package (§7). <c>TryAdd</c> means referencing two does
    /// not silently pick a winner by registration order — the first registered stands, and the mount
    /// log line names which UI is being served.
    /// <para>
    /// This package is substantially larger than the React and Angular ones because a Blazor app
    /// ships a .NET runtime (§11.36). Nothing about that is recoverable at install time, so it is
    /// worth knowing before choosing it rather than after.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddMillraceBlazorUi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IMillraceDashboardUi, BlazorDashboardUi>();
        return services;
    }
}
