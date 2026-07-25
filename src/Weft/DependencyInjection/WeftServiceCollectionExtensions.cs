using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Weft;
using Weft.Invocations;
using Weft.Storage;
using Weft.Storage.InMemory;
using Weft.Tenancy;
using Weft.Workers;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers Weft (ARCHITECTURE.md §5.2): <c>services.AddWeft(w =&gt; w.UseInMemoryStorage())</c>.</summary>
public static class WeftServiceCollectionExtensions
{
    /// <summary>
    /// Registers the client, executor, and hosted services. Core registrations are idempotent
    /// (TryAdd) so repeated calls are safe; hosted services are deduplicated by implementation
    /// type and never suppressed by pre-existing consumer registrations.
    /// </summary>
    public static IServiceCollection AddWeft(this IServiceCollection services, Action<WeftBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<WeftOptions>()
            .Validate(
                o => o.LeaseDuration > o.HeartbeatInterval,
                "WeftOptions.LeaseDuration must exceed HeartbeatInterval — the lease/heartbeat " +
                "margin is what tolerates renewal latency and inter-node clock skew.")
            .Validate(o => o.Queues.Count > 0, "WeftOptions.Queues must not be empty.")
            .Validate(
                o => o.MaxParallelism >= 1 && o.ClaimBatchSize >= 1
                    && o.ActivationBatchSize >= 1 && o.InterruptionLimit >= 1,
                "WeftOptions counts (MaxParallelism, ClaimBatchSize, ActivationBatchSize, " +
                "InterruptionLimit) must be at least 1.")
            .Validate(
                o => o.HeartbeatInterval > TimeSpan.Zero && o.SchedulerInterval > TimeSpan.Zero
                    && o.MinPollDelay > TimeSpan.Zero && o.MaxPollDelay >= o.MinPollDelay,
                "WeftOptions intervals must be positive and MaxPollDelay must be at least MinPollDelay.")
            .Validate(
                o => o.ShutdownTimeout >= TimeSpan.Zero && o.ShutdownGrace >= TimeSpan.Zero,
                "WeftOptions shutdown windows must not be negative.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ITenantContextAccessor, AmbientTenantContextAccessor>();
        services.TryAddSingleton<JobExecutor>();
        services.TryAddSingleton<IJobClient, JobClient>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, WeftWorkerService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, WeftSchedulerService>());

        configure(new WeftBuilder(services));
        return services;
    }
}
