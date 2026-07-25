using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Millrace.Storage;
using Millrace.Storage.InMemory;
using Millrace.Storage.Monitoring;
using Millrace.Workflows;

namespace Millrace;

/// <summary>
/// Configuration surface handed to <c>AddMillrace</c>. Storage registration is <b>last-wins</b>
/// (via <c>Services.Replace</c>) — the documented contract provider packages build on, so the
/// final composition-root registration (including test-host overrides) deterministically wins.
/// </summary>
public sealed class MillraceBuilder(IServiceCollection services)
{
    public IServiceCollection Services { get; } = services;

    public MillraceBuilder Configure(Action<MillraceOptions> configure)
    {
        Services.Configure(configure);
        return this;
    }

    /// <summary>
    /// Registers a workflow definition (ARCHITECTURE.md §6.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The definition is compiled once at registration, so a malformed graph — no steps, a blank id,
    /// a version below 1 — fails at startup rather than when an instance first runs it.
    /// </para>
    /// <para>
    /// Register every version that still has instances in flight. Removing a version strands the
    /// instances pinned to it, because an instance always finishes on the version it started with.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <typeparamref name="TWorkflow"/> does not implement <see cref="IWorkflow{TData}"/>.
    /// </exception>
    public MillraceBuilder AddWorkflow<TWorkflow>()
        where TWorkflow : class, new()
    {
        var contract = typeof(TWorkflow).GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IWorkflow<>))
            ?? throw new ArgumentException(
                $"{typeof(TWorkflow).Name} does not implement IWorkflow<TData>.", nameof(TWorkflow));

        var dataType = contract.GetGenericArguments()[0];
        var compile = typeof(WorkflowDefinition)
            .GetMethod(nameof(WorkflowDefinition.Compile))!
            .MakeGenericMethod(dataType);

        WorkflowDefinition definition;
        try
        {
            definition = (WorkflowDefinition)compile.Invoke(null, [new TWorkflow()])!;
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Surface the validation message, not the reflection wrapper.
            throw ex.InnerException;
        }

        Services.AddSingleton(definition);
        Services.TryAddSingleton(sp => new WorkflowRegistry(sp.GetServices<WorkflowDefinition>()));
        Services.TryAddSingleton<IWorkflowClient, WorkflowClient>();
        // Scoped: the dispatcher writes into the per-execution JobSideEffects, so it must not
        // outlive one job.
        Services.TryAddScoped<IWorkflowDispatcher, WorkflowDispatcher>();
        return this;
    }

    /// <summary>
    /// Uses the bundled non-durable in-memory provider (development, samples, tests).
    /// </summary>
    public MillraceBuilder UseInMemoryStorage()
    {
        Services.Replace(ServiceDescriptor.Singleton(
            sp => new InMemoryStorage(sp.GetRequiredService<TimeProvider>())));
        return UseStorage(
            sp => sp.GetRequiredService<InMemoryStorage>(),
            sp => sp.GetRequiredService<InMemoryStorage>(),
            sp => sp.GetRequiredService<InMemoryStorage>());
    }

    /// <summary>
    /// Registers storage implementations (last-wins). Provider packages call this from their
    /// own <c>UseXxxStorage</c> extensions.
    /// </summary>
    /// <param name="jobStorage">The Layer 0 job contract. Required.</param>
    /// <param name="workflowStorage">The workflow instance and bookmark contract.</param>
    /// <param name="monitoringStorage">
    /// The dashboard read model. Optional <em>here</em> so a provider under construction still
    /// composes, but a supported provider implements it (§11.14) — omitting it makes
    /// <c>MapMillraceDashboard</c> fail at startup rather than serving a blank dashboard.
    /// </param>
    public MillraceBuilder UseStorage(
        Func<IServiceProvider, IJobStorage> jobStorage,
        Func<IServiceProvider, IWorkflowStorage>? workflowStorage = null,
        Func<IServiceProvider, IMonitoringStorage>? monitoringStorage = null)
    {
        ArgumentNullException.ThrowIfNull(jobStorage);
        Services.Replace(ServiceDescriptor.Singleton(jobStorage));
        if (workflowStorage is not null)
        {
            Services.Replace(ServiceDescriptor.Singleton(workflowStorage));
        }

        if (monitoringStorage is not null)
        {
            Services.Replace(ServiceDescriptor.Singleton(monitoringStorage));
        }

        return this;
    }
}
