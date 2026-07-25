using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Millrace;
using Millrace.Invocations;
using Millrace.Storage;
using Millrace.Storage.InMemory;
using Millrace.Tenancy;
using Millrace.Workers;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers Millrace (ARCHITECTURE.md §5.2): <c>services.AddMillrace(w =&gt; w.UseInMemoryStorage())</c>.</summary>
public static class MillraceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the client, executor, and hosted services. Core registrations are idempotent
    /// (TryAdd) so repeated calls are safe; hosted services are deduplicated by implementation
    /// type and never suppressed by pre-existing consumer registrations.
    /// </summary>
    public static IServiceCollection AddMillrace(this IServiceCollection services, Action<MillraceBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<MillraceOptions>()
            .Validate(
                o => o.LeaseDuration > o.HeartbeatInterval,
                "MillraceOptions.LeaseDuration must exceed HeartbeatInterval — the lease/heartbeat " +
                "margin is what tolerates renewal latency and inter-node clock skew.")
            .Validate(o => o.Queues.Count > 0, "MillraceOptions.Queues must not be empty.")
            .Validate(
                o => o.MaxParallelism >= 1 && o.ClaimBatchSize >= 1
                    && o.ActivationBatchSize >= 1 && o.InterruptionLimit >= 1,
                "MillraceOptions counts (MaxParallelism, ClaimBatchSize, ActivationBatchSize, " +
                "InterruptionLimit) must be at least 1.")
            .Validate(
                o => o.HeartbeatInterval > TimeSpan.Zero && o.SchedulerInterval > TimeSpan.Zero
                    && o.MinPollDelay > TimeSpan.Zero && o.MaxPollDelay >= o.MinPollDelay,
                "MillraceOptions intervals must be positive and MaxPollDelay must be at least MinPollDelay.")
            .Validate(
                o => o.ShutdownTimeout >= TimeSpan.Zero && o.ShutdownGrace >= TimeSpan.Zero,
                "MillraceOptions shutdown windows must not be negative.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ITenantContextAccessor, AmbientTenantContextAccessor>();
        services.TryAddSingleton<JobExecutor>();
        // Scoped to one job execution: the workflow engine writes the checkpoint and follow-on jobs
        // here, and the worker folds them into that job's own terminal transition.
        services.TryAddScoped<JobSideEffects>();
        services.TryAddSingleton<IJobClient, JobClient>();
        // Registered even with no workflows: the dashboard's signal endpoint resolves the client
        // regardless, and an application that registers none should get "nothing was waiting"
        // rather than a missing-service failure. The registry is simply empty.
        services.TryAddSingleton(sp => new Millrace.Workflows.WorkflowRegistry(sp.GetServices<Millrace.Workflows.WorkflowDefinition>()));
        services.TryAddSingleton<Millrace.Workflows.IWorkflowClient, Millrace.Workflows.WorkflowClient>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MillraceWorkerService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MillraceSchedulerService>());

        configure(new MillraceBuilder(services));
        return services;
    }
}
