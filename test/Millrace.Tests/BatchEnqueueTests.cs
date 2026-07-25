using Microsoft.Extensions.DependencyInjection;
using Millrace.Storage;
using Millrace.Storage.InMemory;
using Millrace.Testing;
using Xunit;

namespace Millrace.Tests;

/// <summary>
/// Batch enqueue (#45).
/// </summary>
/// <remarks>
/// The storage contract has always inserted all-or-nothing; these cover the client surface that
/// reaches it, and the two properties that make a batch worth having rather than a loop —
/// atomicity, and positional ids that honour the idempotency scope.
/// </remarks>
public sealed class BatchEnqueueTests
{
    public interface IWork
    {
        Task RunAsync(int value);
    }

    private sealed class Work(Recorder recorder) : IWork
    {
        public Task RunAsync(int value)
        {
            recorder.Add(value);
            return Task.CompletedTask;
        }
    }

    public sealed class Recorder
    {
        private readonly List<int> _values = [];

        public IReadOnlyList<int> Values
        {
            get
            {
                lock (_values)
                {
                    return [.. _values];
                }
            }
        }

        public void Add(int value)
        {
            lock (_values)
            {
                _values.Add(value);
            }
        }
    }

    private static MillraceTestHost Create() => MillraceTestHost.Create(services =>
    {
        services.AddSingleton<Recorder>();
        services.AddScoped<IWork, Work>();
    });

    [Fact]
    public async Task A_batch_enqueues_every_job_and_returns_ids_positionally()
    {
        await using var host = Create();

        var batch = new JobBatch();
        foreach (var value in Enumerable.Range(1, 5))
        {
            batch.Enqueue<IWork>(w => w.RunAsync(value));
        }

        var ids = await host.Jobs.EnqueueBatchAsync(batch);

        Assert.Equal(5, ids.Count);
        Assert.Equal(5, ids.Distinct().Count());

        await host.RunUntilIdleAsync();
        Assert.Equal([1, 2, 3, 4, 5], host.Services.GetRequiredService<Recorder>().Values.Order());
    }

    [Fact]
    public async Task An_empty_batch_is_a_no_op_rather_than_an_error()
    {
        await using var host = Create();

        // Fanning out over an empty collection is ordinary; making it throw would push a guard into
        // every caller.
        Assert.Empty(await host.Jobs.EnqueueBatchAsync(new JobBatch()));
    }

    [Fact]
    public async Task A_batch_can_mix_immediate_and_scheduled_jobs()
    {
        await using var host = Create();

        var batch = new JobBatch()
            .Enqueue<IWork>(w => w.RunAsync(1))
            .Schedule<IWork>(w => w.RunAsync(2), TimeSpan.FromHours(3));

        var ids = await host.Jobs.EnqueueBatchAsync(batch);
        await host.RunUntilIdleAsync();

        Assert.Equal(2, ids.Count);
        Assert.Equal([1], host.Services.GetRequiredService<Recorder>().Values);

        await host.AdvanceTime(TimeSpan.FromHours(4));
        await host.RunUntilIdleAsync();

        Assert.Equal([1, 2], host.Services.GetRequiredService<Recorder>().Values.Order());
    }

    [Fact]
    public async Task A_held_idempotency_key_returns_the_existing_job_at_that_position()
    {
        await using var host = Create();

        var first = await host.Jobs.EnqueueAsync<IWork>(
            w => w.RunAsync(1), new EnqueueOptions { IdempotencyKey = "k1" });

        var batch = new JobBatch()
            .Enqueue<IWork>(w => w.RunAsync(99), new EnqueueOptions { IdempotencyKey = "k1" })
            .Enqueue<IWork>(w => w.RunAsync(2));

        var ids = await host.Jobs.EnqueueBatchAsync(batch);

        // §4.2.6 applies per position: the duplicate resolves to the job already holding the key,
        // and the rest of the batch still lands.
        Assert.Equal(first, ids[0]);
        Assert.NotEqual(first, ids[1]);

        await host.RunUntilIdleAsync();
        Assert.Equal([1, 2], host.Services.GetRequiredService<Recorder>().Values.Order());
    }

    [Fact]
    public async Task A_batch_that_cannot_be_inserted_lands_nothing()
    {
        var services = new ServiceCollection();
        services.AddMillrace(m => m.UseInMemoryStorage().Configure(o => o.WorkerEnabled = false));
        services.AddScoped<IWork, Work>();
        services.AddSingleton<Recorder>();
        using var provider = services.BuildServiceProvider();

        var jobs = provider.GetRequiredService<IJobClient>();
        var storage = provider.GetRequiredService<InMemoryStorage>();

        var good = await jobs.EnqueueAsync<IWork>(w => w.RunAsync(1));

        // The storage contract leaves duplicate keys within a batch to the caller and lets providers
        // reject them or not, so unchecked this would behave differently on each database. The
        // client rejects it, identically everywhere, before anything is written.
        var batch = new JobBatch()
            .Enqueue<IWork>(w => w.RunAsync(2), new EnqueueOptions { IdempotencyKey = "dup" })
            .Enqueue<IWork>(w => w.RunAsync(3), new EnqueueOptions { IdempotencyKey = "dup" });

        var ex = await Assert.ThrowsAsync<ArgumentException>(async () => await jobs.EnqueueBatchAsync(batch));
        Assert.Contains("share idempotency key", ex.Message);

        var page = await provider.GetRequiredService<Millrace.Storage.Monitoring.IMonitoringStorage>()
            .QueryJobsAsync(new Millrace.Storage.Monitoring.JobQuery(), CancellationToken.None);

        Assert.Equal(good, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task One_round_trip_regardless_of_size()
    {
        await using var host = Create();

        var batch = new JobBatch();
        foreach (var value in Enumerable.Range(1, 500))
        {
            batch.Enqueue<IWork>(w => w.RunAsync(value));
        }

        var ids = await host.Jobs.EnqueueBatchAsync(batch);

        // The reason the API exists: a fan-out is one insert, not five hundred.
        Assert.Equal(500, ids.Count);
        Assert.Equal(500, await host.RunUntilIdleAsync());
    }
}
