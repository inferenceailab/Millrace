using System.Diagnostics;
using Millrace.Benchmarks.Systems;

namespace Millrace.Benchmarks;

/// <summary>
/// What gets measured. Each scenario is one question, asked identically of every system.
/// </summary>
public static class Scenarios
{
    /// <summary>
    /// How fast the client can write jobs, with nothing consuming them.
    /// </summary>
    /// <remarks>
    /// Workers are never started, so this isolates the enqueue path: expression capture, argument
    /// serialization, and the insert. It is the number that matters to whatever is handling a
    /// request while it enqueues, because that cost is paid on the caller's thread.
    /// </remarks>
    public static async Task<Measurement> EnqueueThroughputAsync(
        IJobSystem system, RunOptions options, Tuning tuning, CancellationToken ct)
    {
        await system.PrepareAsync(ct);

        var perProducer = options.Jobs / options.Producers;
        var started = Stopwatch.GetTimestamp();

        await Parallel.ForAsync(0, options.Producers, ct, async (_, token) =>
        {
            for (var i = 0; i < perProducer; i++)
            {
                await system.EnqueueAsync(Stopwatch.GetTimestamp(), token);
            }
        });

        var elapsed = Stopwatch.GetElapsedTime(started);
        var enqueued = perProducer * options.Producers;

        return new Measurement(
            "enqueue", system.Name, tuning.Name, enqueued,
            enqueued / elapsed.TotalSeconds, 0, 0, 0, 0, 0, elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// How fast a saturated queue drains.
    /// </summary>
    /// <remarks>
    /// The backlog is seeded with workers stopped, so every system starts from the same standing
    /// queue and none of them is measured while racing its own producer. Throughput is the backlog
    /// divided by the time from starting the pool to the last completion — spin-up included, and
    /// also reported on its own as startup so it can be seen rather than inferred.
    /// </remarks>
    public static async Task<Measurement> DrainThroughputAsync(
        IJobSystem system, RunOptions options, Tuning tuning, BenchCounter counter, CancellationToken ct)
    {
        await system.PrepareAsync(ct);

        var perProducer = options.Jobs / options.Producers;
        await Parallel.ForAsync(0, options.Producers, ct, async (_, token) =>
        {
            for (var i = 0; i < perProducer; i++)
            {
                await system.EnqueueAsync(Stopwatch.GetTimestamp(), token);
            }
        });

        var seeded = perProducer * options.Producers;
        counter.Arm(seeded);

        var started = Stopwatch.GetTimestamp();
        await system.StartWorkersAsync(ct);
        await counter.Reached.WaitAsync(options.Timeout, ct);

        var window = counter.WindowFrom(started);

        // No percentiles here. Every job in a seeded backlog was enqueued before any of them ran, so
        // its "latency" is just its position in the queue divided by the drain rate — the same
        // number as the throughput column, restated in a way that reads like responsiveness and is
        // not. The latency scenario is where that question is actually asked.
        return new Measurement(
            "drain", system.Name, tuning.Name, seeded,
            window > TimeSpan.Zero ? seeded / window.TotalSeconds : 0,
            counter.TimeToFirstCompletion(started).TotalMilliseconds,
            0, 0, 0, 0,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>
    /// Enqueue-to-execute latency under an arrival rate no system is saturated by.
    /// </summary>
    /// <remarks>
    /// This is the scenario polling architecture shows up in, and the one that most needs the
    /// matched tuning to mean anything: at a system's default poll interval this measures the
    /// interval, not the system.
    /// <para>
    /// Arrivals are emitted in 25 ms batches rather than one at a time because Windows timer
    /// granularity is ~15 ms — pacing every individual job would produce a sawtooth that is an
    /// artefact of <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> rather than
    /// of any system here. Every system gets the identical arrival pattern.
    /// </para>
    /// </remarks>
    public static async Task<Measurement> LatencyAsync(
        IJobSystem system, RunOptions options, Tuning tuning, BenchCounter counter, CancellationToken ct)
    {
        await system.PrepareAsync(ct);

        var total = options.RatePerSecond * options.Seconds;
        counter.Arm(total);
        await system.StartWorkersAsync(ct);

        // Let the pool reach steady state before the first arrival, so the first batch does not
        // record a worker's cold start as its latency.
        await Task.Delay(TimeSpan.FromSeconds(2), ct);

        var started = Stopwatch.GetTimestamp();
        const int ticksPerSecond = 40;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / ticksPerSecond));

        var emitted = 0;
        var pending = new List<Task>(total);

        while (emitted < total && await timer.WaitForNextTickAsync(ct))
        {
            // Emit against elapsed time rather than a fixed count per tick. A per-tick count is an
            // integer division of the rate, so any rate that is not a multiple of the tick frequency
            // silently becomes a lower one — and the run then reports a latency for an arrival rate
            // nobody asked for. Deriving the target from the clock also absorbs a late tick instead
            // of letting the schedule drift for the rest of the run.
            var due = (int)(Stopwatch.GetElapsedTime(started).TotalSeconds * options.RatePerSecond);
            while (emitted < Math.Min(due, total))
            {
                // Dispatched rather than awaited in line. Awaiting here makes the arrival rate a
                // function of how long each system's enqueue call takes, and Hangfire's client is
                // synchronous — a first attempt paced this loop directly and delivered 118/s to
                // Hangfire against the 200/s Millrace received, which compares two systems under
                // two different workloads and reads as a latency result.
                //
                // The timestamp is taken inside the callback, so what is measured is still
                // call-start to execution: thread-pool scheduling delay before the call is not
                // charged to either system, and the enqueue itself still is.
                pending.Add(Task.Run(() => system.EnqueueAsync(Stopwatch.GetTimestamp(), ct), ct));
                emitted++;
            }
        }

        // Surfaces an enqueue that threw. Without this the run would simply wait out its timeout on
        // completions that were never going to arrive.
        await Task.WhenAll(pending);

        await counter.Reached.WaitAsync(options.Timeout, ct);

        var elapsed = Stopwatch.GetElapsedTime(started);
        var latencies = counter.Latencies();

        return new Measurement(
            "latency", system.Name, tuning.Name, latencies.Length,
            latencies.Length / elapsed.TotalSeconds,
            0,
            Stats.Percentile(latencies, 50),
            Stats.Percentile(latencies, 95),
            Stats.Percentile(latencies, 99),
            latencies.Length == 0 ? 0 : latencies.Max(),
            elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// How fast a backlog of three-step workflow instances completes.
    /// </summary>
    /// <remarks>
    /// Seeded with workers stopped and drained the same way jobs are, so the two throughput numbers
    /// are read the same way. One instance is three checkpointed steps, so an instance is not
    /// comparable to a job — only to another engine's instance.
    /// </remarks>
    public static async Task<Measurement> WorkflowThroughputAsync(
        IWorkflowSystem system, RunOptions options, BenchCounter counter, CancellationToken ct)
    {
        await system.PrepareAsync(ct);

        var perProducer = options.Instances / options.Producers;
        await Parallel.ForAsync(0, options.Producers, ct, async (_, token) =>
        {
            for (var i = 0; i < perProducer; i++)
            {
                await system.StartInstanceAsync(Stopwatch.GetTimestamp(), token);
            }
        });

        var seeded = perProducer * options.Producers;
        counter.Arm(seeded);

        var started = Stopwatch.GetTimestamp();
        await system.StartWorkersAsync(ct);
        await counter.Reached.WaitAsync(options.Timeout, ct);

        var window = counter.WindowFrom(started);

        // Same reasoning as the drain scenario: seeded backlog, so percentiles restate throughput.
        return new Measurement(
            "workflow", system.Name, "matched", seeded,
            window > TimeSpan.Zero ? seeded / window.TotalSeconds : 0,
            counter.TimeToFirstCompletion(started).TotalMilliseconds,
            0, 0, 0, 0,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
}
