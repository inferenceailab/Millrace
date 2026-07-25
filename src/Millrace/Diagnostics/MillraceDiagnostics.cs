using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Millrace.Diagnostics;

/// <summary>
/// The OpenTelemetry surface: one <see cref="ActivitySource"/> and one <see cref="Meter"/>
/// (ARCHITECTURE.md §8).
/// </summary>
/// <remarks>
/// <para>
/// Native rather than adapted: <see cref="ActivitySource"/> and <see cref="Meter"/> are BCL types,
/// so this costs no dependency and G7 holds. A consumer subscribes with whatever exporter they
/// already run — <c>AddSource(MillraceDiagnostics.SourceName)</c>.
/// </para>
/// <para>
/// Names are stable API. Changing one silently breaks a consumer's dashboards and alerts, which
/// fail by showing nothing rather than by erroring.
/// </para>
/// </remarks>
public static class MillraceDiagnostics
{
    /// <summary>Name to pass to <c>AddSource</c> when configuring tracing.</summary>
    public const string SourceName = "Millrace";

    /// <summary>Name to pass to <c>AddMeter</c> when configuring metrics.</summary>
    public const string MeterName = "Millrace";

    internal static readonly ActivitySource Source = new(SourceName);

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Jobs that finished, tagged by outcome — the numerator of any success rate.</summary>
    internal static readonly Counter<long> JobsCompleted = Meter.CreateCounter<long>(
        "millrace.jobs.completed", unit: "{job}", description: "Jobs that reached a terminal state.");

    /// <summary>
    /// Execution wall time, excluding queue wait.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from queue latency below: a slow job and a backed-up queue are
    /// different problems with different fixes, and one histogram cannot distinguish them.
    /// </remarks>
    internal static readonly Histogram<double> JobDuration = Meter.CreateHistogram<double>(
        "millrace.job.duration", unit: "s", description: "Time spent executing a job.");

    /// <summary>Time from a job becoming claimable to a worker starting it.</summary>
    internal static readonly Histogram<double> QueueLatency = Meter.CreateHistogram<double>(
        "millrace.job.queue_latency", unit: "s", description: "Time a job waited before execution began.");

    /// <summary>
    /// Starts the span for one job execution, continuing the trace that enqueued it.
    /// </summary>
    /// <remarks>
    /// <see cref="ActivityKind.Consumer"/> because a worker consumes from a queue — the same shape
    /// as a message-broker consumer, which is what makes the span sit correctly in a trace view
    /// beside HTTP and database spans.
    /// </remarks>
    internal static Activity? StartJobActivity(string typeName, string methodName, string? traceParent)
    {
        var links = Array.Empty<ActivityLink>();
        ActivityContext.TryParse(traceParent, traceState: null, out var parent);

        return Source.StartActivity(
            $"{methodName} {typeName}", ActivityKind.Consumer, parent, links: links);
    }
}
