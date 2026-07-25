using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Weft.Tests.Hosting;

/// <summary>Startup validation must fail fast on configurations that would misbehave silently.</summary>
public class OptionsValidationTests
{
    private static IHost BuildHost(Action<WeftOptions> configure)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddLogging();
        builder.Services.AddWeft(w => w.UseInMemoryStorage().Configure(configure));
        return builder.Build();
    }

    [Fact]
    public async Task Lease_not_exceeding_heartbeat_fails_at_startup()
    {
        using var host = BuildHost(o =>
        {
            o.LeaseDuration = TimeSpan.FromSeconds(30);
            o.HeartbeatInterval = TimeSpan.FromSeconds(60);
        });

        var e = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains("LeaseDuration", e.Message);
    }

    [Fact]
    public async Task Empty_queues_fail_at_startup()
    {
        using var host = BuildHost(o => o.Queues.Clear());

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Fact]
    public async Task Non_positive_counts_fail_at_startup()
    {
        using var host = BuildHost(o => o.MaxParallelism = 0);

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Fact]
    public async Task Inverted_poll_delays_fail_at_startup()
    {
        using var host = BuildHost(o =>
        {
            o.MinPollDelay = TimeSpan.FromSeconds(10);
            o.MaxPollDelay = TimeSpan.FromSeconds(1);
        });

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Fact]
    public async Task Default_options_start_cleanly()
    {
        using var host = BuildHost(_ => { });

        await host.StartAsync();
        await host.StopAsync();
    }
}
