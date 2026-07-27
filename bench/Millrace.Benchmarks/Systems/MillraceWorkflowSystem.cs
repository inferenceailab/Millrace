using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Millrace.Storage.PostgreSql;
using Millrace.Workflows;

namespace Millrace.Benchmarks.Systems;

/// <summary>
/// Millrace's workflow engine running the shared three-step definition.
/// </summary>
/// <remarks>
/// Each activity is a Layer 1 job, so this number includes the substrate underneath it. That is the
/// design (§6.2) rather than an artefact of the harness, and it is why the workflow figure is well
/// below the raw job figure: three checkpointed steps cost more than one job.
/// </remarks>
public sealed class MillraceWorkflowSystem(string adminConnectionString, BenchCounter counter, int workers)
    : IWorkflowSystem
{
    private IHost? _host;
    private IWorkflowClient? _client;

    public string Name => "Millrace";

    public string Version => typeof(IWorkflowClient).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "unknown";

    public string Configuration => $"MaxParallelism={workers}, three activities, checkpoint per step";

    public async Task PrepareAsync(CancellationToken ct)
    {
        var connectionString = await Database.RecreateAsync(adminConnectionString, "bench_millrace_wf", ct);

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);

        builder.Services.AddSingleton(counter);
        builder.Services.AddMillrace(millrace =>
        {
            millrace.UsePostgreSqlStorage(connectionString);
            millrace.AddWorkflow<MillraceBenchWorkflow>();
            millrace.Configure(options => options.MaxParallelism = workers);
        });

        _host = builder.Build();
        _client = _host.Services.GetRequiredService<IWorkflowClient>();
        await _host.Services.GetRequiredService<PostgreSqlStorage>().InitializeAsync(ct);
    }

    public async Task StartInstanceAsync(long enqueuedTimestamp, CancellationToken ct) =>
        await _client!.StartAsync("bench", new BenchWorkflowData { EnqueuedTimestamp = enqueuedTimestamp }, ct);

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

/// <summary>Three steps in a line — the shape both engines run.</summary>
public sealed class MillraceBenchWorkflow : IWorkflow<BenchWorkflowData>
{
    public string Id => "bench";

    public int Version => 1;

    public void Build(IWorkflowBuilder<BenchWorkflowData> flow) => flow
        .StartWith<MillraceBenchStep>()
        .Then<MillraceBenchStep>()
        .Then<MillraceBenchFinalStep>();
}

/// <summary>
/// Mutates the document so the step costs a real checkpoint write rather than an empty transition.
/// </summary>
public sealed class MillraceBenchStep : IActivity<BenchWorkflowData>
{
    public Task ExecuteAsync(ActivityContext<BenchWorkflowData> context, CancellationToken ct)
    {
        context.Data.Step++;
        return Task.CompletedTask;
    }
}

/// <summary>Records the completion, which is what ends the run.</summary>
public sealed class MillraceBenchFinalStep(BenchCounter counter) : IActivity<BenchWorkflowData>
{
    public Task ExecuteAsync(ActivityContext<BenchWorkflowData> context, CancellationToken ct)
    {
        context.Data.Step++;
        counter.Record(context.Data.EnqueuedTimestamp);
        return Task.CompletedTask;
    }
}
