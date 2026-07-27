using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace Millrace.Benchmarks.Systems;

/// <summary>
/// WorkflowCore running the same three-step definition against the same PostgreSQL server.
/// </summary>
/// <remarks>
/// Configured as its own README does — <c>AddWorkflow</c> with the PostgreSQL persistence provider —
/// with two knobs moved in its favour: concurrency is raised from its default of 4 to match every
/// other system in the table, and the poll interval comes down to the shared floor. Left at its
/// defaults it would be compared at a quarter of the concurrency, which would say nothing about
/// either engine.
/// <para>
/// It has no generic host: WorkflowCore's host is started directly, so this class owns a
/// <see cref="ServiceProvider"/> rather than an <c>IHost</c>. That difference is in how the two
/// libraries are used and not in what is measured — the timed window starts when workers start.
/// </para>
/// </remarks>
public sealed class WorkflowCoreWorkflowSystem(string adminConnectionString, BenchCounter counter, int workers)
    : IWorkflowSystem
{
    private ServiceProvider? _services;
    private IWorkflowHost? _host;

    public string Name => "WorkflowCore";

    public string Version => typeof(IWorkflowHost).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(IWorkflowHost).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public string Configuration =>
        $"MaxConcurrentWorkflows={workers}, PollInterval=200ms, EF Core persistence, three steps";

    public async Task PrepareAsync(CancellationToken ct)
    {
        var connectionString = await Database.RecreateAsync(adminConnectionString, "bench_workflowcore", ct);

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.ClearProviders().SetMinimumLevel(LogLevel.None));
        services.AddSingleton(counter);
        services.AddTransient<WorkflowCoreBenchStep>();
        services.AddTransient<WorkflowCoreBenchFinalStep>();
        services.AddWorkflow(options =>
        {
            options.UsePostgreSQL(connectionString, canCreateDB: false, canMigrateDB: true);
            options.UsePollInterval(Tuning.PollFloor);
            options.UseMaxConcurrentWorkflows(workers);
        });

        _services = services.BuildServiceProvider();
        _host = _services.GetRequiredService<IWorkflowHost>();
        _host.RegisterWorkflow<WorkflowCoreBenchWorkflow, BenchWorkflowData>();

        // EF Core migrations run here rather than on first start, for the same reason the other two
        // systems create their schema in Prepare: it is setup, and timing it would measure EF.
        _services.GetRequiredService<IPersistenceProvider>().EnsureStoreExists();
        await Task.CompletedTask;
    }

    public async Task StartInstanceAsync(long enqueuedTimestamp, CancellationToken ct) =>
        await _host!.StartWorkflow("bench", new BenchWorkflowData { EnqueuedTimestamp = enqueuedTimestamp });

    public Task StartWorkersAsync(CancellationToken ct)
    {
        _host!.Start();
        return Task.CompletedTask;
    }

    public Task StopWorkersAsync(CancellationToken ct)
    {
        _host!.Stop();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
            _services = null;
        }

        _host = null;
    }
}

/// <summary>The same three steps in a line as the Millrace definition.</summary>
public sealed class WorkflowCoreBenchWorkflow : IWorkflow<BenchWorkflowData>
{
    public string Id => "bench";

    public int Version => 1;

    public void Build(IWorkflowBuilder<BenchWorkflowData> builder) => builder
        .StartWith<WorkflowCoreBenchStep>()
        .Then<WorkflowCoreBenchStep>()
        .Then<WorkflowCoreBenchFinalStep>();
}

public sealed class WorkflowCoreBenchStep : StepBody
{
    public override ExecutionResult Run(IStepExecutionContext context)
    {
        var data = (BenchWorkflowData)context.Workflow.Data;
        data.Step++;
        return ExecutionResult.Next();
    }
}

public sealed class WorkflowCoreBenchFinalStep(BenchCounter counter) : StepBody
{
    public override ExecutionResult Run(IStepExecutionContext context)
    {
        var data = (BenchWorkflowData)context.Workflow.Data;
        data.Step++;
        counter.Record(data.EnqueuedTimestamp);
        return ExecutionResult.Next();
    }
}
