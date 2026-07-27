using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Millrace.Benchmarks;

/// <summary>What the machine was, published with the numbers because a number without it is a claim.</summary>
public sealed record Machine(
    string Os,
    string Architecture,
    int Cores,
    string Runtime,
    string PostgreSql,
    string Gc)
{
    public static Machine Describe(string postgresVersion) => new(
        RuntimeInformation.OSDescription,
        RuntimeInformation.ProcessArchitecture.ToString(),
        Environment.ProcessorCount,
        RuntimeInformation.FrameworkDescription,
        postgresVersion,
        System.Runtime.GCSettings.IsServerGC ? "server" : "workstation");
}

/// <summary>Renders results as the markdown that goes into <c>docs/benchmarks.md</c>, and as JSON.</summary>
public static class Report
{
    public static string Markdown(Machine machine, IReadOnlyList<Aggregate> results, RunOptions options)
    {
        var text = new StringBuilder();

        text.AppendLine($"Machine: {machine.Os} · {machine.Architecture} · {machine.Cores} logical cores");
        text.AppendLine($"Runtime: {machine.Runtime} · {machine.Gc} GC · PostgreSQL {machine.PostgreSql}");
        text.AppendLine(
            $"Settings: workers={options.Workers}, producers={options.Producers}, jobs={options.Jobs}, " +
            $"instances={options.Instances}, rate={options.RatePerSecond}/s for {options.Seconds}s, " +
            $"median of {options.Repeats}");
        text.AppendLine();

        foreach (var scenario in results.Select(r => r.Scenario).Distinct())
        {
            text.AppendLine($"### {Title(scenario)}");
            text.AppendLine();
            text.AppendLine("| System | Tuning | Throughput | Startup | p50 | p95 | p99 | max | spread |");
            text.AppendLine("|---|---|--:|--:|--:|--:|--:|--:|--:|");

            foreach (var row in results.Where(r => r.Scenario == scenario))
            {
                var unit = scenario == "workflow" ? "inst/s" : "jobs/s";
                text.AppendLine(
                    $"| {row.System} | {row.Tuning} | {row.ThroughputPerSecond:N0} {unit} " +
                    $"| {Ms(row.StartupMs)} | {Ms(row.P50Ms)} | {Ms(row.P95Ms)} | {Ms(row.P99Ms)} " +
                    $"| {Ms(row.MaxMs)} | {row.SpreadPercent:N0}%{Stalls(row)} |");
            }

            text.AppendLine();

            foreach (var row in results.Where(r => r.Scenario == scenario && r.TimedOut > 0))
            {
                text.AppendLine(
                    $"> **{row.System} ({row.Tuning}) stalled on {row.TimedOut} of " +
                    $"{row.TimedOut + row.Repeats.Count} runs** — the backlog had not drained after " +
                    $"the harness timeout. The figure above is the median of the runs that finished.");
                text.AppendLine();
            }
        }

        return text.ToString();
    }

    public static string Json(Machine machine, IReadOnlyList<Aggregate> results, RunOptions options) =>
        JsonSerializer.Serialize(
            new { machine, options = new { options.Jobs, options.Instances, options.Workers, options.Producers, options.Repeats, options.RatePerSecond, options.Seconds }, results },
            new JsonSerializerOptions { WriteIndented = true });

    private static string Ms(double value) => value <= 0 ? "—" : value < 10 ? $"{value:N1} ms" : $"{value:N0} ms";

    private static string Stalls(Aggregate row) => row.TimedOut == 0 ? string.Empty : $" ⚠ {row.TimedOut} stalled";

    private static string Title(string scenario) => scenario switch
    {
        "enqueue" => "Enqueue throughput — client writes, nothing consuming",
        "drain" => "Drain throughput — a standing backlog, workers started",
        "latency" => "Enqueue-to-execute latency — steady arrivals, unsaturated",
        "workflow" => "Workflow throughput — three-step instances, drained from a backlog",
        _ => scenario,
    };
}
