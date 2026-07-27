using System.Diagnostics;

namespace Millrace.Benchmarks;

/// <summary>
/// Counts completions and records enqueue-to-execute latency. One instance is shared by whichever
/// system is under measurement, and it is the only thing that decides when a run is over.
/// </summary>
/// <remarks>
/// Counting in-process rather than by polling each system's own statistics is deliberate. Every
/// system here reports progress differently — Hangfire aggregates counters on a timer, WorkflowCore
/// has no equivalent at all — so a poll-based finish line would measure three different things and
/// attribute the difference to throughput. A job body that increments a counter is the same
/// instruction in all three.
/// </remarks>
public sealed class BenchCounter
{
    private readonly Lock _gate = new();
    private double[] _latencies = [];
    private long _first;
    private long _last;
    private int _completed;
    private int _target;
    private TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completions observed since the last <see cref="Arm"/>.</summary>
    public int Completed => Volatile.Read(ref _completed);

    /// <summary>Completes once <paramref name="target"/> completions have been observed.</summary>
    public Task Reached
    {
        get
        {
            lock (_gate)
            {
                return _reached.Task;
            }
        }
    }

    /// <summary>Resets for a run of <paramref name="target"/> jobs. Call with nothing executing.</summary>
    public void Arm(int target)
    {
        lock (_gate)
        {
            _latencies = new double[target];
            _completed = 0;
            _first = 0;
            _last = 0;
            _target = target;
            _reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Records one completion of work enqueued at <paramref name="enqueuedTimestamp"/>.</summary>
    /// <remarks>
    /// <see cref="Stopwatch.GetTimestamp"/> ticks rather than wall-clock time: the producer and the
    /// worker are in one process here, so the two ends share an origin and the subtraction is
    /// meaningful without any clock synchronisation.
    /// </remarks>
    public void Record(long enqueuedTimestamp)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(enqueuedTimestamp, now).TotalMilliseconds;
        var index = Interlocked.Increment(ref _completed) - 1;

        if (index == 0)
        {
            Volatile.Write(ref _first, now);
        }

        Volatile.Write(ref _last, now);

        if (index < _latencies.Length)
        {
            _latencies[index] = elapsed;
        }

        if (index + 1 >= _target)
        {
            lock (_gate)
            {
                _reached.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Latencies observed, in milliseconds. Ordered by completion, not by size — the percentile
    /// helpers sort their own copy.
    /// </summary>
    public double[] Latencies()
    {
        lock (_gate)
        {
            return [.. _latencies.Take(Math.Min(Volatile.Read(ref _completed), _latencies.Length))];
        }
    }

    /// <summary>
    /// Wall time from <paramref name="startedAt"/> — the instant workers were started — to the last
    /// completion. This is what throughput is computed over.
    /// </summary>
    /// <remarks>
    /// Measuring first-completion to last-completion instead would be wrong for anything whose work
    /// is not all counted. A three-step workflow only records its final step, so the first record
    /// does not arrive until the pipeline is two-thirds full: the window would cover the tail of the
    /// run and report a throughput the engine never sustained. Including the ramp costs the drain
    /// scenario a few milliseconds out of several seconds, and it makes both numbers mean the same
    /// thing — work completed, divided by the time it took from a standing start.
    /// </remarks>
    public TimeSpan WindowFrom(long startedAt)
    {
        var last = Volatile.Read(ref _last);
        return last == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(startedAt, last);
    }

    /// <summary>Wall time from <paramref name="startedAt"/> to the first completion.</summary>
    public TimeSpan TimeToFirstCompletion(long startedAt)
    {
        var first = Volatile.Read(ref _first);
        return first == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(startedAt, first);
    }
}

/// <summary>
/// The unit of work. Every system enqueues exactly this method with exactly this argument, so what
/// is being compared is the machinery around the job rather than the job.
/// </summary>
/// <remarks>
/// It does no work on purpose. A benchmark whose job body sleeps or computes measures the sleep:
/// with a body of any weight, every system converges on <c>parallelism / body duration</c> and the
/// substrate — the thing under test — stops being visible. What this measures is therefore the
/// ceiling, not a prediction of any real workload, and the doc says so.
/// </remarks>
public sealed class BenchJob(BenchCounter counter)
{
    public Task RunAsync(long enqueuedTimestamp)
    {
        counter.Record(enqueuedTimestamp);
        return Task.CompletedTask;
    }
}

/// <summary>The workflow data document, shared by the Millrace and WorkflowCore definitions.</summary>
/// <remarks>
/// Mutable properties with a parameterless constructor because both engines serialize it and
/// WorkflowCore's persistence needs to rehydrate it. Keeping one type for both is what makes the
/// two definitions comparable: same document, same size on the wire, same number of writes.
/// </remarks>
public sealed class BenchWorkflowData
{
    public long EnqueuedTimestamp { get; set; }

    public int Step { get; set; }
}
