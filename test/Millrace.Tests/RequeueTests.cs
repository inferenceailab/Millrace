using Microsoft.Extensions.DependencyInjection;
using Millrace.Storage;
using Millrace.Storage.InMemory;
using Xunit;

namespace Millrace.Tests;

/// <summary>
/// Requeue as a new job with provenance (#73, §11.19).
/// </summary>
/// <remarks>
/// The point of the design is what it does <em>not</em> have to decide. Three of the four questions
/// #73 raised — retry budget, idempotency-key collision, orphaned continuations — are answered by
/// existing semantics once requeue mints a new job, and these tests pin those answers so a later
/// change cannot quietly alter them.
/// </remarks>
public sealed class RequeueTests
{
    public interface IWork
    {
        Task RunAsync(int value);
    }

    private static (IJobClient Client, InMemoryStorage Storage, ServiceProvider Provider) Build()
    {
        var services = new ServiceCollection();
        services.AddMillrace(m => m.UseInMemoryStorage());
        var provider = services.BuildServiceProvider();
        return (
            provider.GetRequiredService<IJobClient>(),
            provider.GetRequiredService<InMemoryStorage>(),
            provider);
    }

    /// <summary>
    /// Seeds a job in <paramref name="state"/> the way the contract allows.
    /// </summary>
    /// <remarks>
    /// Terminal states are not insertable — <c>EnqueueAsync</c> only accepts Scheduled, Enqueued and
    /// Awaiting — so a job reaches Dead or Failed by being claimed and transitioned, exactly as it
    /// would in life.
    /// </remarks>
    private static async Task<JobRecord> FinishedJobAsync(
        InMemoryStorage storage, JobState state = JobState.Dead, string? idempotencyKey = null)
    {
        var insertable = state is JobState.Enqueued or JobState.Scheduled or JobState.Awaiting
            ? state
            : JobState.Enqueued;

        var job = new JobRecord
        {
            Id = JobId.New(),
            Queue = "reports",
            State = insertable,
            Priority = 5,
            Invocation = new JobInvocation
            {
                TypeName = "Millrace.Tests.RequeueTests+IWork, Millrace.Tests",
                MethodName = "RunAsync",
                ParameterTypes = ["System.Int32, System.Private.CoreLib"],
                ArgumentsJson = ["7"],
            },
            Retry = Retry.Exponential(3),
            IdempotencyKey = idempotencyKey,
            TenantId = "acme",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await storage.EnqueueAsync([job], CancellationToken.None);

        // Processing is reached by claiming, not by a transition — ApplyAsync only targets terminal,
        // Failed or Enqueued.
        if (state == JobState.Processing)
        {
            await ClaimAsync(storage, job.Queue);
            return (await storage.GetJobAsync(job.Id, CancellationToken.None))!;
        }

        if (insertable == state)
        {
            return (await storage.GetJobAsync(job.Id, CancellationToken.None))!;
        }

        var claimed = await ClaimAsync(storage, job.Queue);
        await storage.ApplyAsync(
            new JobTransition
            {
                JobId = claimed.Id,
                ExpectedWorkerId = claimed.WorkerId!,
                ExpectedAttempt = claimed.Attempt,
                TargetState = state,
                Failures = 3,
                Error = "boom",
                DueAt = state == JobState.Failed ? DateTimeOffset.UtcNow.AddMinutes(5) : null,
                FinishedAt = state.IsTerminal() ? DateTimeOffset.UtcNow : null,
            },
            CancellationToken.None);

        return (await storage.GetJobAsync(job.Id, CancellationToken.None))!;
    }

    private static async Task<JobRecord> ClaimAsync(InMemoryStorage storage, string queue)
    {
        var claimed = await storage.ClaimAsync(
            new ClaimRequest("worker-1", [queue], MaxCount: 1, TimeSpan.FromMinutes(5)), CancellationToken.None);
        return claimed[0];
    }

    [Fact]
    public async Task Requeue_creates_a_new_job_that_links_back()
    {
        var (client, storage, provider) = Build();
        using var _ = provider;
        var original = await FinishedJobAsync(storage);

        var newId = await client.RequeueAsync(original.Id);

        Assert.NotNull(newId);
        Assert.NotEqual(original.Id, newId);

        var copy = await storage.GetJobAsync(newId.Value, CancellationToken.None);
        Assert.Equal(original.Id, copy!.RequeuedFrom);
        Assert.Equal(JobState.Enqueued, copy.State);
        Assert.Equal("reports", copy.Queue);
        Assert.Equal(5, copy.Priority);
        Assert.Equal("acme", copy.TenantId);
        Assert.Equal(original.Invocation.ArgumentsJson, copy.Invocation.ArgumentsJson);
    }

    [Fact]
    public async Task The_original_is_left_untouched()
    {
        var (client, storage, provider) = Build();
        using var _ = provider;
        var original = await FinishedJobAsync(storage);

        await client.RequeueAsync(original.Id);

        // Terminal records are immutable everywhere else in the contract; requeue does not carve
        // out an exception for itself.
        var stored = await storage.GetJobAsync(original.Id, CancellationToken.None);
        Assert.Equal(JobState.Dead, stored!.State);
        Assert.Equal(3, stored.Failures);
    }

    [Fact]
    public async Task The_retry_budget_starts_fresh_because_the_job_is_new()
    {
        var (client, storage, provider) = Build();
        using var _ = provider;
        var original = await FinishedJobAsync(storage);

        var newId = await client.RequeueAsync(original.Id);
        var copy = await storage.GetJobAsync(newId!.Value, CancellationToken.None);

        // Not a decision so much as a consequence: a new job has never failed.
        Assert.Equal(0, copy!.Failures);
        Assert.Equal(0, copy.Attempt);
    }

    [Fact]
    public async Task An_idempotency_key_still_held_makes_requeue_a_no_op()
    {
        var (client, storage, provider) = Build();
        using var _ = provider;
        var original = await FinishedJobAsync(storage, idempotencyKey: "capture:1");

        // Something else already re-ran the equivalent work and holds the key.
        var holder = await FinishedJobAsync(storage, state: JobState.Enqueued, idempotencyKey: "capture:1");

        var newId = await client.RequeueAsync(original.Id);

        // §4.2.6 already answers this: enqueueing a duplicate active key returns the existing job.
        // "An equivalent run is already in flight" is the right answer to "run it again".
        Assert.Equal(holder.Id, newId);
    }

    [Fact]
    public async Task Continuations_of_the_original_are_not_revived()
    {
        var (client, storage, provider) = Build();
        using var _ = provider;
        var original = await FinishedJobAsync(storage);

        var newId = await client.RequeueAsync(original.Id);
        var copy = await storage.GetJobAsync(newId!.Value, CancellationToken.None);

        // They were cancelled when the original died, and nothing about a new job brings them back.
        // Reattaching the copy as a continuation would make it wait on an already-terminal parent.
        Assert.Null(copy!.ParentId);
    }

    [Fact]
    public async Task A_failed_job_awaiting_retry_can_be_requeued()
    {
        var (client, storage, provider) = Build();
        using var _ = provider;
        var original = await FinishedJobAsync(storage, state: JobState.Failed);

        Assert.NotNull(await client.RequeueAsync(original.Id));
    }

    [Theory]
    [InlineData(JobState.Enqueued)]
    [InlineData(JobState.Scheduled)]
    [InlineData(JobState.Processing)]
    public async Task Requeueing_a_job_that_has_not_finished_is_refused(JobState state)
    {
        var (client, storage, provider) = Build();
        using var _ = provider;
        var original = await FinishedJobAsync(storage, state);

        // Otherwise the same work runs twice concurrently, which is the opposite of what an
        // operator reaching for "requeue" wants.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.RequeueAsync(original.Id));

        Assert.Contains("has not finished", ex.Message);
    }

    [Fact]
    public async Task Requeueing_an_unknown_job_returns_null()
    {
        var (client, _, provider) = Build();
        using var __ = provider;

        Assert.Null(await client.RequeueAsync(JobId.New()));
    }
}
