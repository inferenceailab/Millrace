namespace Millrace.Benchmarks;

/// <summary>One measured run of one scenario against one system.</summary>
public sealed record Measurement(
    string Scenario,
    string System,
    string Tuning,
    int Count,
    double ThroughputPerSecond,
    double StartupMs,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs,
    double WallMs);

/// <summary>The repeats of one scenario/system pair, reduced to what gets published.</summary>
/// <remarks>
/// The median rather than the mean, and every repeat kept in the JSON alongside it. A background
/// process on the machine inflates one repeat and leaves the others alone, which moves a mean and
/// does not move a median; publishing the spread is what lets a reader see that happen rather than
/// take the summary on trust.
/// </remarks>
public sealed record Aggregate(
    string Scenario,
    string System,
    string Tuning,
    int Count,
    double ThroughputPerSecond,
    double StartupMs,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs,
    double SpreadPercent,
    int TimedOut,
    IReadOnlyList<Measurement> Repeats)
{
    /// <summary>
    /// Reduces the repeats that finished, and carries how many did not.
    /// </summary>
    /// <remarks>
    /// A run that never drains is a result about the system, not an error in the harness, so it is
    /// counted and published rather than silently retried. Dropping it from the median and saying
    /// nothing would turn "stalled once in nine attempts" into a clean number.
    /// </remarks>
    public static Aggregate From(IReadOnlyList<Measurement> repeats, int timedOut = 0)
    {
        ArgumentOutOfRangeException.ThrowIfZero(repeats.Count);

        var throughputs = repeats.Select(r => r.ThroughputPerSecond).ToArray();
        var median = Stats.Median(throughputs);
        var spread = median <= 0 ? 0 : (throughputs.Max() - throughputs.Min()) / median * 100;

        return new Aggregate(
            repeats[0].Scenario,
            repeats[0].System,
            repeats[0].Tuning,
            repeats[0].Count,
            median,
            Stats.Median([.. repeats.Select(r => r.StartupMs)]),
            Stats.Median([.. repeats.Select(r => r.P50Ms)]),
            Stats.Median([.. repeats.Select(r => r.P95Ms)]),
            Stats.Median([.. repeats.Select(r => r.P99Ms)]),
            Stats.Median([.. repeats.Select(r => r.MaxMs)]),
            spread,
            timedOut,
            repeats);
    }
}

public static class Stats
{
    /// <summary>Nearest-rank percentile of <paramref name="values"/>, which it sorts a copy of.</summary>
    public static double Percentile(double[] values, double percentile)
    {
        if (values.Length == 0)
        {
            return 0;
        }

        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        var rank = (int)Math.Ceiling(percentile / 100 * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    public static double Median(double[] values)
    {
        if (values.Length == 0)
        {
            return 0;
        }

        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
