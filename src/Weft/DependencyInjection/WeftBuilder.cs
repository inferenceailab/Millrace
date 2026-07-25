using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Weft.Storage;
using Weft.Storage.InMemory;

namespace Weft;

/// <summary>
/// Configuration surface handed to <c>AddWeft</c>. Storage registration is <b>last-wins</b>
/// (via <c>Services.Replace</c>) — the documented contract provider packages build on, so the
/// final composition-root registration (including test-host overrides) deterministically wins.
/// </summary>
public sealed class WeftBuilder(IServiceCollection services)
{
    public IServiceCollection Services { get; } = services;

    public WeftBuilder Configure(Action<WeftOptions> configure)
    {
        Services.Configure(configure);
        return this;
    }

    /// <summary>
    /// Uses the bundled non-durable in-memory provider (development, samples, tests).
    /// </summary>
    public WeftBuilder UseInMemoryStorage()
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
    public WeftBuilder UseStorage(
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
