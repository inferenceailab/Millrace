using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Millrace.Dashboard.Endpoints;
using Millrace.Storage;
using Millrace.Storage.Monitoring;

namespace Millrace.Dashboard;

/// <summary>
/// Mounts the dashboard API: <c>app.MapMillraceDashboard("/millrace")</c>.
/// </summary>
public static class MillraceDashboardEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Mounts the versioned REST API and its OpenAPI document under <paramref name="prefix"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Endpoints land at <c>{prefix}/api/v1/...</c> and the document at
    /// <c>{prefix}/openapi/millrace-v1.json</c> (§11.11).
    /// </para>
    /// <para>
    /// Two things are checked here rather than on first request, so a misconfiguration surfaces at
    /// deploy time instead of as a dashboard that merely looks broken:
    /// </para>
    /// <list type="bullet">
    ///   <item>the configured storage provider implements <see cref="IMonitoringStorage"/> (§11.14);</item>
    ///   <item>authorization is configured, unless the environment is Development or the insecure
    ///   opt-out is set (§11.13).</item>
    /// </list>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The dashboard services are not registered, the provider has no read model, or authorization
    /// is unconfigured outside Development.
    /// </exception>
    public static IEndpointConventionBuilder MapMillraceDashboard(
        this IEndpointRouteBuilder endpoints, string prefix = "/millrace")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var services = endpoints.ServiceProvider;
        var options = services.GetService<IOptions<MillraceDashboardOptions>>()?.Value
            ?? throw new InvalidOperationException(
                "The Millrace dashboard services are not registered. Call services.AddMillraceDashboard() "
                + "before app.MapMillraceDashboard().");

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(MillraceDashboard).FullName!);

        EnsureReadModel(services);
        var authorization = ResolveAuthorization(services, options, logger);

        prefix = '/' + prefix.Trim('/');

        endpoints.MapOpenApi($"{prefix}/openapi/{{documentName}}.json")
            .AddEndpointFilter(new DashboardAuthorizationFilter(authorization));

        var api = endpoints.MapGroup($"{prefix}/api/{MillraceDashboard.ApiVersion}")
            .WithGroupName(MillraceDashboard.DocumentName)
            // Authorization first: an unauthorized caller must not reach the storage layer at all,
            // so a rejected cursor cannot become a probe for whether the dashboard exists.
            .AddEndpointFilter(new DashboardAuthorizationFilter(authorization))
            .AddEndpointFilter<StorageProblemFilter>();

        MapMetaEndpoints(api);
        api.MapMonitoringEndpoints();
        api.MapManagementEndpoints();

        logger.LogInformation(
            "Millrace dashboard mounted at {Prefix}/api/{Version}; OpenAPI document at {Prefix}/openapi/{Document}.json.",
            prefix, MillraceDashboard.ApiVersion, prefix, MillraceDashboard.DocumentName);

        // A UI package is optional — the API is the product and is fully usable headless (§7).
        if (services.GetService<IMillraceDashboardUi>() is { } ui)
        {
            MapUi(endpoints, prefix, ui, authorization);
            logger.LogInformation("Millrace dashboard {Ui} UI mounted at {Prefix}/ui.", ui.Name, prefix);
        }

        return api;
    }

    /// <summary>
    /// §11.14: a provider without the read model would serve a permanently blank dashboard, so the
    /// failure is raised here, naming the provider, rather than left for an end user to discover.
    /// </summary>
    private static void EnsureReadModel(IServiceProvider services)
    {
        if (services.GetService<IMonitoringStorage>() is not null)
        {
            return;
        }

        var providerName = services.GetService<IJobStorage>()?.GetType().FullName ?? "(no storage provider registered)";
        throw new InvalidOperationException(
            $"The configured Millrace storage provider '{providerName}' does not implement IMonitoringStorage, "
            + "so the dashboard has nothing to read. A supported provider implements it (ARCHITECTURE.md §11.14); "
            + "if you are authoring one, pass the monitoring factory to MillraceBuilder.UseStorage.");
    }

    /// <summary>§11.13: fail closed, and fail at startup rather than per request.</summary>
    private static IMillraceDashboardAuthorization? ResolveAuthorization(
        IServiceProvider services, MillraceDashboardOptions options, ILogger logger)
    {
        var hook = services.GetService<IMillraceDashboardAuthorization>();
        if (hook is not null)
        {
            return hook;
        }

        if (options.AllowAnonymousAccessInsecure)
        {
            logger.LogWarning(
                "The Millrace dashboard is mounted with AllowAnonymousAccessInsecure set: job arguments, "
                + "which frequently contain personal data, are readable by anyone who can reach the mount path.");
            return null;
        }

        var environment = services.GetService<IHostEnvironment>();
        if (environment?.IsDevelopment() == true)
        {
            logger.LogWarning(
                "The Millrace dashboard is mounted without authorization. This is allowed because the "
                + "environment is Development; it will throw at startup in any other environment.");
            return null;
        }

        throw new InvalidOperationException(
            "The Millrace dashboard is mounted without authorization, in environment "
            + $"'{environment?.EnvironmentName ?? "(unknown)"}'. The API exposes serialized job arguments and, "
            + "from 0.4, management actions. Register a hook with "
            + "services.AddMillraceDashboardAuthorization<T>(), or set "
            + "MillraceDashboardOptions.AllowAnonymousAccessInsecure if unauthenticated access is genuinely "
            + "intended (ARCHITECTURE.md §11.13).");
    }

    /// <summary>
    /// Serves the registered UI bundle at <c>{prefix}/ui</c>, falling back to the entry document so
    /// client-side routes and deep links resolve.
    /// </summary>
    private static void MapUi(
        IEndpointRouteBuilder endpoints, string prefix, IMillraceDashboardUi ui,
        IMillraceDashboardAuthorization? authorization)
    {
        var filter = new DashboardAuthorizationFilter(authorization);

        endpoints.MapGet($"{prefix}/ui/{{**path}}", (string? path) =>
            {
                if (!string.IsNullOrEmpty(path) && ui.TryOpenAsset(path, out var asset, out var assetType))
                {
                    return Results.Stream(asset, assetType);
                }

                // No asset: either the mount root or a client-side route. Both get the entry
                // document — a 404 here would break every deep link into the application.
                var entry = ui.OpenEntryDocument(out var entryType);
                return Results.Stream(entry, entryType);
            })
            .ExcludeFromDescription() // Static assets are not part of the REST contract.
            .AddEndpointFilter(filter);

        // The mount root without a trailing slash, so /millrace/ui works as well as /millrace/ui/.
        endpoints.MapGet($"{prefix}/ui", () =>
            {
                var entry = ui.OpenEntryDocument(out var entryType);
                return Results.Stream(entry, entryType);
            })
            .ExcludeFromDescription()
            .AddEndpointFilter(filter);
    }

    private static void MapMetaEndpoints(IEndpointRouteBuilder api)
    {
        api.MapGet("/info", (IMonitoringStorage storage) => TypedResults.Ok(new DashboardInfo(
                MillraceDashboard.ApiVersion,
                storage.GetType().FullName ?? storage.GetType().Name)))
            .WithName("GetDashboardInfo")
            .WithSummary("Contract version and the storage provider backing this dashboard.");
    }

    /// <param name="ApiVersion">The contract version this mount serves.</param>
    /// <param name="StorageProvider">The provider type backing the read model.</param>
    private sealed record DashboardInfo(string ApiVersion, string StorageProvider);

    /// <summary>
    /// Applies the authorization hook to every dashboard endpoint, including the OpenAPI document —
    /// the document describes the surface, so leaving it open would leak the shape of the API.
    /// </summary>
    private sealed class DashboardAuthorizationFilter(IMillraceDashboardAuthorization? authorization) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            if (authorization is null)
            {
                return await next(context);
            }

            var http = context.HttpContext;
            if (await authorization.AuthorizeAsync(http, http.RequestAborted))
            {
                return await next(context);
            }

            // 404 rather than 403: an ops surface should not confirm its own existence.
            return Results.NotFound();
        }
    }
}
