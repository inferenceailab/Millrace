using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Millrace.Storage.PostgreSql;

namespace Millrace.Benchmarks.Systems;

/// <summary>Millrace's own substrate, configured the way its README tells a consumer to.</summary>
public sealed class MillraceJobSystem(string adminConnectionString, BenchCounter counter, int workers, Tuning tuning)
    : IJobSystem
{
    private IHost? _host;
    private IJobClient? _client;

    public string Name => "Millrace";

    public string Version => typeof(IJobClient).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(IJobClient).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>
    /// The same string under either tuning, which is the point: Millrace's shipped defaults already
    /// sit at the matched floor, so nothing is adjusted for it in the run that adjusts the
    /// comparands. A reader comparing the two rows should be able to see that from the table.
    /// </summary>
    public string Configuration =>
        $"MaxParallelism={workers}, MinPollDelay=200ms, MaxPollDelay=5s, ClaimBatchSize=16, " +
        $"LISTEN/NOTIFY wake — shipped defaults, unchanged under '{tuning.Name}'";

    public async Task PrepareAsync(CancellationToken ct)
    {
        var connectionString = await Database.RecreateAsync(adminConnectionString, "bench_millrace", ct);

        var builder = Host.CreateApplicationBuilder();

        // Logging is off for every system. A console sink on a hot path is a benchmark of the
        // console, and the three systems log at very different volumes by default.
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);

        builder.Services.AddSingleton(counter);
        builder.Services.AddScoped<BenchJob>();
        builder.Services.AddMillrace(millrace =>
        {
            millrace.UsePostgreSqlStorage(connectionString);
            millrace.Configure(options =>
            {
                options.MaxParallelism = workers;

                // Millrace's defaults already sit at the matched floor (§11 — MinPollDelay 200 ms),
                // so there is nothing to change for either tuning. Stated rather than silent: the
                // comparands are the ones being moved, and that asymmetry should be visible.
            });
        });

        _host = builder.Build();
        _client = _host.Services.GetRequiredService<IJobClient>();

        // Schema DDL runs on first use, so force it here — otherwise the first enqueue of the
        // measured run pays for CREATE TABLE and the enqueue-throughput number is a DDL benchmark.
        await _host.Services.GetRequiredService<PostgreSqlStorage>().InitializeAsync(ct);
    }

    public async Task EnqueueAsync(long enqueuedTimestamp, CancellationToken ct) =>
        await _client!.EnqueueAsync<BenchJob>(job => job.RunAsync(enqueuedTimestamp), ct: ct);

    public Task StartWorkersAsync(CancellationToken ct) => _host!.StartAsync(ct);

    public Task StopWorkersAsync(CancellationToken ct) => _host!.StopAsync(ct);

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            _host.Dispose();
            _host = null;
        }

        await Task.CompletedTask;
    }
}
