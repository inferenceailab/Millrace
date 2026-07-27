using System.Reflection;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Millrace.Benchmarks.Systems;

/// <summary>
/// Hangfire on the same PostgreSQL server, through <c>Hangfire.PostgreSql</c>.
/// </summary>
/// <remarks>
/// Configured as its documentation configures it — <c>AddHangfire</c> plus <c>AddHangfireServer</c>
/// in a generic host — rather than through anything unusual. Two knobs move under the matched
/// tuning and both move in Hangfire's favour: the queue poll interval comes down from 15 seconds to
/// the shared floor, and long polling is switched on so a fetch waits on the database instead of
/// sleeping out the interval. That is the closest equivalent to the LISTEN/NOTIFY wake Millrace
/// uses, and without it a latency comparison would be measuring a default rather than a design.
/// </remarks>
public sealed class HangfireJobSystem(string adminConnectionString, BenchCounter counter, int workers, Tuning tuning)
    : IJobSystem
{
    private IHost? _host;
    private IBackgroundJobClient? _client;

    public string Name => "Hangfire";

    public string Version => typeof(BackgroundJob).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(BackgroundJob).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public string Configuration => tuning.IsMatched
        ? $"WorkerCount={workers}, QueuePollInterval=200ms, EnableLongPolling=true, SchedulePollingInterval=200ms"
        : $"WorkerCount={workers}, QueuePollInterval=15s (default), EnableLongPolling=false (default)";

    public async Task PrepareAsync(CancellationToken ct)
    {
        var connectionString = await Database.RecreateAsync(adminConnectionString, "bench_hangfire", ct);

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);

        builder.Services.AddSingleton(counter);
        builder.Services.AddScoped<BenchJob>();

        var storageOptions = new PostgreSqlStorageOptions
        {
            SchemaName = "hangfire",
            PrepareSchemaIfNecessary = true,
            UseNativeDatabaseTransactions = true,
        };

        if (tuning.IsMatched)
        {
            storageOptions.QueuePollInterval = Tuning.PollFloor;
            storageOptions.EnableLongPolling = true;
        }

        builder.Services.AddHangfire(configuration => configuration
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(postgres => postgres.UseNpgsqlConnection(connectionString), storageOptions));

        builder.Services.AddHangfireServer(server =>
        {
            server.WorkerCount = workers;
            if (tuning.IsMatched)
            {
                server.SchedulePollingInterval = Tuning.PollFloor;
            }
        });

        _host = builder.Build();
        _client = _host.Services.GetRequiredService<IBackgroundJobClient>();

        // Resolving the storage is what runs Hangfire's schema migration, so it happens here rather
        // than inside the measured window. Same treatment as Millrace's InitializeAsync.
        _ = _host.Services.GetRequiredService<JobStorage>();
    }

    public Task EnqueueAsync(long enqueuedTimestamp, CancellationToken ct)
    {
        // Hangfire's client API is synchronous — there is no async enqueue to call. Running it on
        // the thread pool is how a caller would keep it off a request thread, and it is what the
        // producer loop needs to reach any concurrency at all.
        _client!.Enqueue<BenchJob>(job => job.RunAsync(enqueuedTimestamp));
        return Task.CompletedTask;
    }

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
