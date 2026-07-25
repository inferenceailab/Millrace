using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Weft.Storage;
using Weft.Storage.InMemory;
using Weft.Tenancy;
using Xunit;

namespace Weft.Tests;

public interface IClientProbe
{
    Task DoAsync(int id);
}

public class JobClientTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static (JobClient Client, InMemoryStorage Storage, FakeTimeProvider Time, AmbientTenantContextAccessor Tenants) Build()
    {
        var time = new FakeTimeProvider(Epoch);
        var storage = new InMemoryStorage(time);
        var tenants = new AmbientTenantContextAccessor();
        var client = new JobClient(storage, tenants, time, Options.Create(new WeftOptions()));
        return (client, storage, time, tenants);
    }

    [Fact]
    public async Task Enqueue_builds_an_enqueued_record_with_defaults()
    {
        var (client, storage, time, _) = Build();

        var id = await client.EnqueueAsync<IClientProbe>(p => p.DoAsync(1));

        var job = (await storage.GetJobAsync(id, CancellationToken.None))!;
        Assert.Equal(JobState.Enqueued, job.State);
        Assert.Equal(WeftOptions.DefaultQueue, job.Queue);
        Assert.Equal(0, job.Priority);
        Assert.Equal(Retry.Exponential(5), job.Retry);
        Assert.Equal(time.GetUtcNow(), job.CreatedAt);
        Assert.Null(job.TenantId);
        Assert.Equal(0, job.Attempt);
    }

    [Fact]
    public async Task Enqueue_applies_options_and_ambient_tenant()
    {
        var (client, storage, _, tenants) = Build();

        JobId id;
        using (tenants.BeginScope("tenant-3"))
        {
            id = await client.EnqueueAsync<IClientProbe>(p => p.DoAsync(1), new EnqueueOptions
            {
                Queue = "payments",
                Priority = 9,
                Retry = Retry.None,
                IdempotencyKey = "cap:1",
            });
        }

        var job = (await storage.GetJobAsync(id, CancellationToken.None))!;
        Assert.Equal("payments", job.Queue);
        Assert.Equal(9, job.Priority);
        Assert.Equal(Retry.None, job.Retry);
        Assert.Equal("cap:1", job.IdempotencyKey);
        Assert.Equal("tenant-3", job.TenantId);
    }

    [Fact]
    public async Task Schedule_by_delay_sets_scheduled_state_and_due_time()
    {
        var (client, storage, time, _) = Build();

        var id = await client.ScheduleAsync<IClientProbe>(p => p.DoAsync(1), TimeSpan.FromHours(2));

        var job = (await storage.GetJobAsync(id, CancellationToken.None))!;
        Assert.Equal(JobState.Scheduled, job.State);
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromHours(2), job.DueAt);
    }

    [Fact]
    public async Task Continue_with_parks_an_awaiting_child()
    {
        var (client, storage, _, _) = Build();
        var parentId = await client.EnqueueAsync<IClientProbe>(p => p.DoAsync(1));

        var childId = await client.ContinueWithAsync<IClientProbe>(parentId, p => p.DoAsync(2));

        var child = (await storage.GetJobAsync(childId, CancellationToken.None))!;
        Assert.Equal(JobState.Awaiting, child.State);
        Assert.Equal(parentId, child.ParentId);
    }

    [Fact]
    public async Task Upsert_recurring_computes_next_fire_and_flows_options()
    {
        var (client, storage, time, _) = Build();

        await client.UpsertRecurringAsync<IClientProbe>(
            "cleanup", "0 3 * * *", p => p.DoAsync(1),
            new EnqueueOptions { Queue = "maintenance", Priority = 2 });

        var record = (await storage.GetRecurringAsync("cleanup", CancellationToken.None))!;
        Assert.Equal("0 3 * * *", record.Cron);
        Assert.Equal("maintenance", record.Queue);
        Assert.Equal(2, record.Priority);
        Assert.Equal(Epoch + TimeSpan.FromHours(3), record.NextFireTime);
        Assert.Equal(time.GetUtcNow(), record.CreatedAt);
    }

    [Fact]
    public async Task Upsert_recurring_rejects_idempotency_keys()
    {
        var (client, _, _, _) = Build();

        var e = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await client.UpsertRecurringAsync<IClientProbe>(
                "r", "* * * * *", p => p.DoAsync(1),
                new EnqueueOptions { IdempotencyKey = "nope" }));

        Assert.Contains("recurring", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Remove_recurring_delegates_to_storage()
    {
        var (client, storage, _, _) = Build();
        await client.UpsertRecurringAsync<IClientProbe>("r", "* * * * *", p => p.DoAsync(1));

        await client.RemoveRecurringAsync("r");

        Assert.Null(await storage.GetRecurringAsync("r", CancellationToken.None));
    }

    [Fact]
    public async Task Schedule_rejects_negative_delay()
    {
        var (client, _, _, _) = Build();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await client.ScheduleAsync<IClientProbe>(p => p.DoAsync(1), TimeSpan.FromSeconds(-1)));
    }
}
