using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Millrace.Invocations;
using Millrace.Storage;
using Millrace.Storage.InMemory;
using Millrace.Workers;
using Millrace.Workflows;

namespace Millrace.Testing;

/// <summary>
/// An in-memory Millrace host that runs work <b>deterministically</b>, for testing applications
/// built on it (ARCHITECTURE.md §8).
/// </summary>
/// <remarks>
/// <para>
/// The worker pool and scheduler are switched off. Instead, <see cref="RunUntilIdleAsync"/> drains
/// the queue on the calling thread: activate what is due, claim it, run it, apply the transition,
/// repeat until nothing is left. So a test reads
/// </para>
/// <code>
/// await host.Jobs.EnqueueAsync&lt;IEmailSender&gt;(s =&gt; s.SendAsync(orderId));
/// await host.RunUntilIdleAsync();
/// Assert.True(sent);
/// </code>
/// <para>
/// rather than polling with sleeps and hoping. Time is a <see cref="FakeTimeProvider"/>, so delays,
/// retry backoff and signal timeouts are reached with <see cref="AdvanceTime"/> instead of waiting —
/// a seven-day timeout is one call, not seven days.
/// </para>
/// <para>
/// Storage is the bundled in-memory provider, which is explicitly not durable. This is for testing
/// <em>your</em> jobs and workflows; the storage contract itself is covered by the conformance kit.
/// </para>
/// </remarks>
public sealed class MillraceTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly InMemoryStorage _storage;
    private readonly JobExecutor _executor;
    private readonly MillraceOptions _options;

    private const string WorkerId = "millrace-test-host";

    private MillraceTestHost(ServiceProvider provider, FakeTimeProvider time)
    {
        _provider = provider;
        Time = time;
        _storage = provider.GetRequiredService<InMemoryStorage>();
        _executor = provider.GetRequiredService<JobExecutor>();
        _options = provider.GetRequiredService<IOptions<MillraceOptions>>().Value;
        Jobs = provider.GetRequiredService<IJobClient>();
        Workflows = provider.GetRequiredService<IWorkflowClient>();
    }

    /// <summary>The enqueue API, as the application under test sees it.</summary>
    public IJobClient Jobs { get; }

    /// <summary>The workflow API, as the application under test sees it.</summary>
    public IWorkflowClient Workflows { get; }

    /// <summary>The controllable clock. Prefer <see cref="AdvanceTime"/> for moving it.</summary>
    public FakeTimeProvider Time { get; }

    /// <summary>Services registered on the host, for resolving your own types.</summary>
    public IServiceProvider Services => _provider;

    /// <summary>
    /// Builds a host, registering the application's own services and any workflows.
    /// </summary>
    /// <param name="configure">Registers the services jobs and activities depend on.</param>
    /// <param name="millrace">Optional extra Millrace configuration, e.g. <c>AddWorkflow&lt;T&gt;()</c>.</param>
    /// <param name="startingAt">Initial clock value; defaults to 2026-01-01Z.</param>
    public static MillraceTestHost Create(
        Action<IServiceCollection>? configure = null,
        Action<MillraceBuilder>? millrace = null,
        DateTimeOffset? startingAt = null)
    {
        var time = new FakeTimeProvider(startingAt ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddLogging();

        services.AddMillrace(builder =>
        {
            builder.UseInMemoryStorage();
            builder.Configure(o =>
            {
                // The harness is the worker. Leaving the real ones running would race every
                // assertion and put the sleeps back.
                o.WorkerEnabled = false;
                o.SchedulerEnabled = false;
            });

            millrace?.Invoke(builder);
        });

        configure?.Invoke(services);
        return new MillraceTestHost(services.BuildServiceProvider(), time);
    }

    /// <summary>
    /// Moves the clock forward and activates anything that became due.
    /// </summary>
    /// <remarks>
    /// Does not execute: call <see cref="RunUntilIdleAsync"/> after, so a test can advance and
    /// assert that something is <em>ready</em> without running it.
    /// </remarks>
    public async ValueTask AdvanceTime(TimeSpan by)
    {
        Time.Advance(by);
        await ActivateDueAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Runs every claimable job to completion, including work that running them enqueues.
    /// </summary>
    /// <param name="throwOnFailure">
    /// When true (the default) a job that exhausts its retries rethrows here, so a broken job fails
    /// the test loudly instead of leaving an unexplained assertion failure later. Pass false when
    /// the failure is the thing under test.
    /// </param>
    /// <returns>How many job executions ran — retries counted separately.</returns>
    /// <exception cref="MillraceJobFailedException">A job exhausted its retries and <paramref name="throwOnFailure"/> is true.</exception>
    public async ValueTask<int> RunUntilIdleAsync(bool throwOnFailure = true, CancellationToken ct = default)
    {
        var executed = 0;

        // Bounded so a job that endlessly re-enqueues itself fails the test rather than hanging it.
        for (var pass = 0; pass < 10_000; pass++)
        {
            await ActivateDueAsync(ct).ConfigureAwait(false);

            var claimed = await _storage.ClaimAsync(
                new ClaimRequest(
                    WorkerId,
                    [.. _options.Queues, _options.WorkflowQueue],
                    MaxCount: 16,
                    _options.LeaseDuration),
                ct).ConfigureAwait(false);

            if (claimed.Count == 0)
            {
                return executed;
            }

            foreach (var job in claimed)
            {
                executed++;
                await RunOneAsync(job, throwOnFailure, ct).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            "RunUntilIdleAsync did not settle after 10,000 passes. A job is almost certainly "
            + "enqueueing itself in a loop.");
    }

    private async ValueTask RunOneAsync(JobRecord job, bool throwOnFailure, CancellationToken ct)
    {
        // Every transition below comes from JobOutcomes, which is the same code the real worker
        // runs. The harness used to decide these itself, and nothing checked the two agreed — so a
        // consumer's test could pass on rules that had quietly stopped matching production.
        if (JobOutcomes.IsPoisoned(job, _options.InterruptionLimit))
        {
            await _storage.ApplyAsync(
                JobOutcomes.Poisoned(job, WorkerId, Time.GetUtcNow(), FailureEffects(job)), ct)
                .ConfigureAwait(false);

            if (throwOnFailure)
            {
                throw new MillraceJobFailedException(job, new InvalidOperationException(JobOutcomes.PoisonReason(job)));
            }

            return;
        }

        try
        {
            var effects = await _executor.ExecuteAsync(job, ct).ConfigureAwait(false);
            await ApplyAsync(JobOutcomes.Succeeded(job, WorkerId, Time.GetUtcNow(), effects), effects, ct)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is not MillraceJobFailedException)
        {
            // Dead-letter observers still fire, so a saga still compensates in a test.
            var transition = JobOutcomes.Failed(job, WorkerId, Time.GetUtcNow(), e, FailureEffects(job));
            await _storage.ApplyAsync(transition, ct).ConfigureAwait(false);

            if (transition.TargetState == JobState.Dead && throwOnFailure)
            {
                throw new MillraceJobFailedException(job, e);
            }
        }
    }

    /// <summary>Applies a transition, rebasing once if a concurrent checkpoint won.</summary>
    private async ValueTask ApplyAsync(JobTransition transition, JobSideEffects effects, CancellationToken ct)
    {
        try
        {
            await _storage.ApplyAsync(transition, ct).ConfigureAwait(false);
        }
        catch (MillraceStorageException) when (effects.Remerge is not null)
        {
            // The harness runs jobs one at a time, so this is rare — but a workflow fan-out can
            // still produce it, and silently dropping the transition would strand the instance.
            if (await effects.Remerge(ct).ConfigureAwait(false))
            {
                await _storage.ApplyAsync(
                    transition with { Enqueue = effects.Enqueue, Checkpoint = effects.Checkpoint },
                    ct).ConfigureAwait(false);
            }
        }
    }

    private IReadOnlyList<JobRecord> FailureEffects(JobRecord job)
        => JobOutcomes.FailureEffects(_provider.GetServices<IJobFailureObserver>(), job, logger: null);

    private ValueTask ActivateDueAsync(CancellationToken ct = default)
        => new(_storage.ActivateDueJobsAsync(Time.GetUtcNow(), _options.ActivationBatchSize, ct).AsTask());

    // ---------------------------------------------------------------- assertions

    /// <summary>The stored job, or null if there is no such job.</summary>
    public ValueTask<JobRecord?> GetJobAsync(JobId id, CancellationToken ct = default)
        => _storage.GetJobAsync(id, ct);

    /// <summary>The stored workflow instance, or null.</summary>
    public ValueTask<WorkflowInstanceRecord?> GetInstanceAsync(WorkflowInstanceId id, CancellationToken ct = default)
        => _storage.GetInstanceAsync(id, ct);

    /// <summary>The workflow instance's current state.</summary>
    public async ValueTask<WorkflowInstanceState?> GetInstanceStateAsync(
        WorkflowInstanceId id, CancellationToken ct = default)
        => (await GetInstanceAsync(id, ct).ConfigureAwait(false))?.State;

    /// <summary>The workflow instance's data document.</summary>
    public ValueTask<TData?> GetDataAsync<TData>(WorkflowInstanceId id, CancellationToken ct = default)
        => Workflows.GetDataAsync<TData>(id, ct);

    /// <summary>Tears down the host and everything it built.</summary>
    /// <remarks>
    /// Disposes the underlying service provider, which stops the worker and scheduler. Storage is
    /// in-memory and goes with it, so each host is an isolated world and tests need no cleanup
    /// between them.
    /// </remarks>
    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}

/// <summary>Thrown when a job exhausts its retries inside <see cref="MillraceTestHost.RunUntilIdleAsync"/>.</summary>
/// <remarks>
/// Carries the job so a failing test names what died and why, rather than leaving an unexplained
/// assertion failure several lines later.
/// </remarks>
public sealed class MillraceJobFailedException(JobRecord job, Exception cause)
    : Exception($"Job {job.Id} ({job.Invocation.TypeName}.{job.Invocation.MethodName}) failed: {cause.Message}", cause)
{
    /// <summary>The job that exhausted its retries.</summary>
    /// <remarks>
    /// The whole record, not just its id — a test that catches this can assert on the state,
    /// attempt counts or last error without going back to storage for a job that has already died.
    /// </remarks>
    public JobRecord Job { get; } = job;
}
