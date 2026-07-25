using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Millrace.Dashboard;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the dashboard backend: <c>services.AddMillraceDashboard()</c>, then
/// <c>app.MapMillraceDashboard("/millrace")</c>.
/// </summary>
public static class MillraceDashboardServiceCollectionExtensions
{
    /// <summary>
    /// Registers dashboard services and the OpenAPI document describing the contract.
    /// </summary>
    /// <remarks>
    /// The document is registered under <see cref="MillraceDashboard.DocumentName"/> and includes
    /// only endpoints in that group, so a host application's own OpenAPI documents are untouched
    /// and ours never absorbs the host's endpoints.
    /// </remarks>
    public static IServiceCollection AddMillraceDashboard(
        this IServiceCollection services, Action<MillraceDashboardOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<MillraceDashboardOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddOpenApi(MillraceDashboard.DocumentName, options =>
        {
            // Strict group matching. The default would also sweep in every ungrouped endpoint the
            // host app declares, which would publish the host's API inside ours.
            options.ShouldInclude = description => description.GroupName == MillraceDashboard.DocumentName;
        });

        return services;
    }

    /// <summary>
    /// Registers the authorization hook that satisfies the §11.13 startup requirement.
    /// </summary>
    public static IServiceCollection AddMillraceDashboardAuthorization<T>(this IServiceCollection services)
        where T : class, IMillraceDashboardAuthorization
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IMillraceDashboardAuthorization, T>();
        return services;
    }

    /// <summary>Registers an inline authorization hook.</summary>
    public static IServiceCollection AddMillraceDashboardAuthorization(
        this IServiceCollection services, Func<HttpContext, CancellationToken, ValueTask<bool>> authorize)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(authorize);
        services.TryAddSingleton<IMillraceDashboardAuthorization>(new DelegateAuthorization(authorize));
        return services;
    }

    private sealed class DelegateAuthorization(Func<HttpContext, CancellationToken, ValueTask<bool>> authorize)
        : IMillraceDashboardAuthorization
    {
        public ValueTask<bool> AuthorizeAsync(HttpContext context, CancellationToken ct)
            => authorize(context, ct);
    }
}
