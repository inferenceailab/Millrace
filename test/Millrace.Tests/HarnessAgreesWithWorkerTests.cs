using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Millrace.Storage;
using Millrace.Testing;
using Millrace.Workflows;
using Xunit;

namespace Millrace.Tests;

/// <summary>
/// The harness and the worker decide job outcomes the same way (#83).
/// </summary>
/// <remarks>
/// <para>
/// Both run jobs, and they used to reach that decision through separate code. Nothing checked they
/// agreed, and they had already drifted: the harness never applied the poison-pill rule, never
/// truncated stored errors, and let a throwing failure observer abort the dead-letter it was
/// observing. A consumer whose test passed against the drifted harness was being told something
/// untrue about production, which is worse than having no test.
/// </para>
/// <para>
/// The fix is structural — one <see cref="JobOutcomes"/> both call — so these tests guard the
/// structure rather than re-asserting each rule twice. The last one fails if a new decision point
/// appears in the worker without going through it.
/// </para>
/// </remarks>
public sealed class HarnessAgreesWithWorkerTests
{
    public interface IWork
    {
        Task RunAsync();
    }

    private sealed class Boom : IWork
    {
        public Task RunAsync() => throw new InvalidOperationException("boom");
    }

    private sealed class CompensatingObserver : IJobFailureObserver
    {
        public IReadOnlyList<JobRecord> OnDeadLettered(JobRecord job) =>
        [
            new()
            {
                Id = JobId.New(TimeProvider.System),
                Queue = job.Queue,
                State = JobState.Enqueued,
                Invocation = job.Invocation,
                Retry = Retry.None,
                CreatedAt = job.CreatedAt,
            },
        ];
    }

    private sealed class ThrowingObserver : IJobFailureObserver
    {
        public IReadOnlyList<JobRecord> OnDeadLettered(JobRecord job)
            => throw new InvalidOperationException("observer is broken");
    }

    [Fact]
    public async Task The_harness_applies_the_poison_pill_rule()
    {
        await using var host = MillraceTestHost.Create(services => services.AddScoped<IWork, Boom>());

        var id = await host.Jobs.EnqueueAsync<IWork>(w => w.RunAsync());

        // Claimed repeatedly with no failure ever recorded — the signature of executions that
        // vanished without a verdict, i.e. workers that crashed. Produced here the way it is
        // produced in production, by letting leases expire rather than by editing the record.
        var storage = host.Services.GetRequiredService<Millrace.Storage.InMemory.InMemoryStorage>();
        var options = host.Services.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<MillraceOptions>>().Value;

        for (var i = 0; i <= options.InterruptionLimit; i++)
        {
            var claimed = await storage.ClaimAsync(
                new ClaimRequest("crashing-worker", [.. options.Queues], MaxCount: 1, options.LeaseDuration),
                CancellationToken.None);
            Assert.Single(claimed);
            host.Time.Advance(options.LeaseDuration + TimeSpan.FromSeconds(1));
        }

        // Production dead-letters this without running it. Before #83 the harness ran it anyway, so
        // a job that takes down real workers looked healthy in tests.
        await Assert.ThrowsAsync<MillraceJobFailedException>(() => host.RunUntilIdleAsync().AsTask());

        var dead = await host.GetJobAsync(id);
        Assert.Equal(JobState.Dead, dead!.State);
        Assert.Contains("Poison-pill", dead.LastError);
    }

    [Fact]
    public async Task A_throwing_failure_observer_does_not_stop_the_dead_letter()
    {
        await using var host = MillraceTestHost.Create(services =>
        {
            services.AddScoped<IWork, Boom>();
            services.AddSingleton<IJobFailureObserver, ThrowingObserver>();
            services.AddSingleton<IJobFailureObserver, CompensatingObserver>();
        });

        var id = await host.Jobs.EnqueueAsync<IWork>(
            w => w.RunAsync(), new EnqueueOptions { Retry = Retry.None });

        await Assert.ThrowsAsync<MillraceJobFailedException>(
            () => host.RunUntilIdleAsync().AsTask());

        // The transition is the important part: a lost notification is recoverable, a job stuck in
        // Processing is not. The surviving observer's work is still committed with it.
        Assert.Equal(JobState.Dead, (await host.GetJobAsync(id))!.State);
    }

    [Fact]
    public async Task A_stored_error_is_capped_the_same_way_in_both()
    {
        await using var host = MillraceTestHost.Create(services => services.AddScoped<IWork, Boom>());

        var id = await host.Jobs.EnqueueAsync<IWork>(
            w => w.RunAsync(), new EnqueueOptions { Retry = Retry.None });

        await host.RunUntilIdleAsync(throwOnFailure: false);

        // One pathological stack trace must not bloat a row unboundedly — in tests as in production.
        Assert.True((await host.GetJobAsync(id))!.LastError!.Length <= 8192);
    }

    [Theory]
    // The worker is allowed two — cancel and shutdown-release — because both are genuinely
    // worker-only: the harness has no lease to lose and no shutdown to survive. It is allowed none.
    [InlineData("src/Millrace/Workers/MillraceWorkerService.cs", 2)]
    [InlineData("src/Millrace.Testing/MillraceTestHost.cs", 0)]
    public void Neither_builds_outcome_transitions_of_its_own(string path, int allowed)
    {
        // The structural guard, and the only test here that catches the *next* divergence rather
        // than the three already found: a rule written directly in one runner is a rule the other
        // does not have. If this fails, the decision belongs in JobOutcomes.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), path));
        var found = source.Split("new JobTransition").Length - 1;

        Assert.True(
            found == allowed,
            $"{path} builds {found} JobTransition(s) directly, expected {allowed}. Outcome rules "
            + "belong in JobOutcomes so the worker and the test harness cannot disagree (#83).");
    }

    private static string RepoRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}
