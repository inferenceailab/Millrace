using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Millrace.Storage;
using Millrace.Storage.InMemory;

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
    /// Uses the bundled non-durable in-memory provider (development, samples, tests).
    /// </summary>
    public MillraceBuilder UseInMemoryStorage()
    {
        Services.Replace(ServiceDescriptor.Singleton(
            sp => new InMemoryStorage(sp.GetRequiredService<TimeProvider>())));
        return UseStorage(
            sp => sp.GetRequiredService<InMemoryStorage>(),
            sp => sp.GetRequiredService<InMemoryStorage>());
    }

    /// <summary>
    /// Registers storage implementations (last-wins). Provider packages call this from their
    /// own <c>UseXxxStorage</c> extensions.
    /// </summary>
    public MillraceBuilder UseStorage(
        Func<IServiceProvider, IJobStorage> jobStorage,
        Func<IServiceProvider, IWorkflowStorage>? workflowStorage = null)
    {
        ArgumentNullException.ThrowIfNull(jobStorage);
        Services.Replace(ServiceDescriptor.Singleton(jobStorage));
        if (workflowStorage is not null)
        {
            Services.Replace(ServiceDescriptor.Singleton(workflowStorage));
        }

        return this;
    }
}
