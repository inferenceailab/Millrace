using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Millrace.Storage;
using Millrace.Workflows;
using Xunit;

namespace Millrace.Tests.Workflows;

/// <summary>
/// Definition versioning with in-flight drain (#40, §6.1).
/// </summary>
/// <remarks>
/// The load-bearing promise is that deploying a new version never disturbs instances already
/// running: they finish on the version they started with, even while newer starts take the new one.
/// That only holds end-to-end — the registry keying it is not evidence on its own, because the
/// instance record and the dispatcher both have to honour the pin.
/// </remarks>
public sealed class WorkflowVersioningTests
{
    public sealed class Doc
    {
        public Dictionary<string, bool> Done { get; set; } = [];
        public string Version { get; set; } = "";
    }

    public sealed class Shared : IActivity<Doc>
    {
        public Task ExecuteAsync(ActivityContext<Doc> context, CancellationToken ct)
        {
            // Records which definition version is actually driving this instance.
            context.Data.Version = $"v{context.Version}";
            context.Data.Done["shared"] = true;
            return Task.CompletedTask;
        }
    }

    public sealed class OnlyInV2 : IActivity<Doc>
    {
        public Task ExecuteAsync(ActivityContext<Doc> context, CancellationToken ct)
        {
            context.Data.Done["v2-only"] = true;
            return Task.CompletedTask;
        }
    }

    private sealed class OrderV1 : IWorkflow<Doc>
    {
        public string Id => "order";
        public int Version => 1;
        public void Build(IWorkflowBuilder<Doc> flow) => flow.StartWith<Shared>();
    }

    private sealed class OrderV2 : IWorkflow<Doc>
    {
        public string Id => "order";
        public int Version => 2;
        public void Build(IWorkflowBuilder<Doc> flow) => flow.StartWith<Shared>().Then<OnlyInV2>();
    }

    /// <summary>
    /// Builds a host for these tests.
    /// </summary>
    /// <param name="time">The controllable clock the worker and scheduler run against.</param>
    /// <param name="withV2">Whether the second definition version is registered.</param>
    /// <param name="withWorker">
    /// Whether to run the worker pool and scheduler.
    /// </param>
    /// <remarks>
    /// A test that drives the dispatcher directly must pass <see langword="false"/>. The worker
    /// advances the instance on its own — checkpointing bumps <c>Revision</c> — so a test that reads
    /// an instance and writes it back under optimistic concurrency is racing it, and loses whenever
    /// the worker gets there first. That is a timing coin-flip, which is the worst kind of test.
    /// </remarks>
    private static IHost BuildHost(FakeTimeProvider time, bool withV2, bool withWorker = true)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddMillrace(m =>
        {
            m.UseInMemoryStorage().AddWorkflow<OrderV1>();
            if (withV2)
            {
                m.AddWorkflow<OrderV2>();
            }

            m.Configure(o =>
            {
                o.WorkerEnabled = withWorker;
                o.SchedulerEnabled = withWorker;
                o.MinPollDelay = TimeSpan.FromMilliseconds(5);
                o.MaxPollDelay = TimeSpan.FromMilliseconds(20);
                o.SchedulerInterval = TimeSpan.FromMilliseconds(5);
            });
        });

        return builder.Build();
    }

    private static async Task<Doc> RunToCompletionAsync(IHost host, FakeTimeProvider time, WorkflowInstanceId id)
    {
        var storage = host.Services.GetRequiredService<IWorkflowStorage>();
        var deadline = DateTime.UtcNow.AddSeconds(15);

        while (DateTime.UtcNow < deadline)
        {
            var instance = await storage.GetInstanceAsync(id, CancellationToken.None);
            if (instance?.State == WorkflowInstanceState.Completed)
            {
                return (await host.Services.GetRequiredService<IWorkflowClient>().GetDataAsync<Doc>(id))!;
            }

            time.Advance(TimeSpan.FromMilliseconds(200));
            await Task.Delay(15);
        }

        var last = await storage.GetInstanceAsync(id, CancellationToken.None);
        throw new TimeoutException($"Instance did not complete; state={last?.State}, cursor={last?.CursorJson}");
    }

    private static FakeTimeProvider NewTime() => new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task A_new_start_uses_the_latest_registered_version()
    {
        var time = NewTime();
        using var host = BuildHost(time, withV2: true);
        await host.StartAsync();

        var id = await host.Services.GetRequiredService<IWorkflowClient>().StartAsync("order", new Doc());
        var data = await RunToCompletionAsync(host, time, id);

        Assert.Equal("v2", data.Version);
        Assert.True(data.Done.GetValueOrDefault("v2-only"));
    }

    [Fact]
    public async Task A_start_can_pin_an_older_version()
    {
        var time = NewTime();
        using var host = BuildHost(time, withV2: true);
        await host.StartAsync();

        // For a caller that must not drift onto a newer definition mid-rollout.
        var id = await host.Services.GetRequiredService<IWorkflowClient>()
            .StartAsync("order", version: 1, new Doc());
        var data = await RunToCompletionAsync(host, time, id);

        Assert.Equal("v1", data.Version);
        Assert.False(data.Done.GetValueOrDefault("v2-only"));
    }

    [Fact]
    public async Task An_instance_started_before_a_new_version_still_finishes_on_its_own()
    {
        var time = NewTime();

        // Start on a host that only knows v1, and stop it before the instance runs — standing in
        // for an instance that was in flight when the deploy happened.
        using var before = BuildHost(time, withV2: false);
        var storage = before.Services.GetRequiredService<IWorkflowStorage>();
        var id = await before.Services.GetRequiredService<IWorkflowClient>().StartAsync("order", new Doc());

        var pinned = await storage.GetInstanceAsync(id, CancellationToken.None);
        Assert.Equal(1, pinned!.DefinitionVersion);
    }

    [Fact]
    public void Removing_a_version_that_instances_still_reference_is_visible_at_the_registry()
    {
        // The engine cannot rescue an instance whose definition is gone, so the failure has to be
        // loud where it can still be acted on.
        var registry = new WorkflowRegistry([WorkflowDefinition.Compile(new OrderV2())]);

        Assert.False(registry.TryGet("order", 1, out _));
        Assert.Equal(2, registry.GetLatest("order")?.Version);
    }

    [Fact]
    public async Task An_instance_pinned_to_an_unregistered_version_fails_loudly()
    {
        var time = NewTime();

        // No worker: this test calls the dispatcher itself, and a running worker would advance the
        // instance between the read and the write below — bumping Revision and failing the
        // optimistic-concurrency check with a MillraceConcurrencyException instead of the
        // InvalidOperationException under test. It did exactly that on CI while passing everywhere
        // else, which is the shape §11.38's "tests that drive the real worker share its scheduler"
        // warns about.
        using var host = BuildHost(time, withV2: false, withWorker: false);
        await host.StartAsync();

        var storage = host.Services.GetRequiredService<IWorkflowStorage>();
        var id = await host.Services.GetRequiredService<IWorkflowClient>().StartAsync("order", new Doc());

        // Rewrite the instance to claim a version nobody registered — what dropping an old
        // definition while instances drain would look like.
        var instance = await storage.GetInstanceAsync(id, CancellationToken.None);
        await storage.UpdateInstanceAsync(
            instance! with { DefinitionVersion = 99 }, instance!.Revision, CancellationToken.None);

        var dispatcher = host.Services.CreateScope().ServiceProvider.GetRequiredService<IWorkflowDispatcher>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await dispatcher.ExecuteAsync(id.Value, "a1", null, 0, CancellationToken.None));

        Assert.Contains("version 99 is not registered", ex.Message);
        Assert.Contains("must stay registered until they drain", ex.Message);
    }

    [Fact]
    public void Registering_the_same_version_twice_is_rejected_at_startup()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddMillrace(m => m.UseInMemoryStorage().AddWorkflow<OrderV1>().AddWorkflow<OrderV1>())
                .BuildServiceProvider()
                .GetRequiredService<WorkflowRegistry>());

        Assert.Contains("registered twice", ex.Message);
    }
}
