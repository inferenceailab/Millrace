using Millrace.Benchmarks.Systems;

namespace Millrace.Benchmarks;

/// <summary>Everything the harness takes from the command line, with the published defaults.</summary>
public sealed record RunOptions
{
    /// <summary>Points at the server <c>bench/docker-compose.yml</c> starts.</summary>
    public string AdminConnectionString { get; init; } =
        "Host=localhost;Port=5434;Database=postgres;Username=millrace;Password=millrace";

    /// <summary>Jobs per run of the job scenarios.</summary>
    public int Jobs { get; init; } = 10_000;

    /// <summary>Workflow instances per run of the workflow scenario — three steps each.</summary>
    public int Instances { get; init; } = 2_000;

    /// <summary>Worker concurrency, applied identically to every system.</summary>
    public int Workers { get; init; } = 20;

    /// <summary>Threads enqueueing at once.</summary>
    public int Producers { get; init; } = 8;

    /// <summary>Measured runs per cell. The published number is the median of these.</summary>
    public int Repeats { get; init; } = 3;

    /// <summary>Arrival rate for the latency scenario.</summary>
    public int RatePerSecond { get; init; } = 200;

    /// <summary>How long the latency scenario sustains that rate.</summary>
    public int Seconds { get; init; } = 15;

    /// <summary>
    /// How long a single run may take before the harness gives up on it.
    /// </summary>
    /// <remarks>
    /// A run that hangs is a result — it means the configuration cannot drain the backlog — and it
    /// should fail loudly rather than leave the suite waiting overnight.
    /// </remarks>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    public IReadOnlyList<string> ScenarioNames { get; init; } = ["enqueue", "drain", "latency", "workflow"];

    public IReadOnlyList<string> SystemNames { get; init; } = ["millrace", "hangfire", "workflowcore"];

    public IReadOnlyList<Tuning> Tunings { get; init; } = [Tuning.Matched, Tuning.Default];

    public string? JsonPath { get; init; }

    public bool Help { get; init; }

    public static RunOptions Parse(string[] args)
    {
        var options = new RunOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--all":
                    break;
                case "--scenario" when value is not null:
                    options = options with { ScenarioNames = Split(value) };
                    i++;
                    break;
                case "--system" when value is not null:
                    options = options with { SystemNames = Split(value) };
                    i++;
                    break;
                case "--tuning" when value is not null:
                    options = options with
                    {
                        Tunings = [.. Split(value).Select(name =>
                            name == "default" ? Tuning.Default : Tuning.Matched)],
                    };
                    i++;
                    break;
                case "--jobs" when value is not null:
                    options = options with { Jobs = int.Parse(value) };
                    i++;
                    break;
                case "--instances" when value is not null:
                    options = options with { Instances = int.Parse(value) };
                    i++;
                    break;
                case "--workers" when value is not null:
                    options = options with { Workers = int.Parse(value) };
                    i++;
                    break;
                case "--producers" when value is not null:
                    options = options with { Producers = int.Parse(value) };
                    i++;
                    break;
                case "--repeats" when value is not null:
                    options = options with { Repeats = int.Parse(value) };
                    i++;
                    break;
                case "--rate" when value is not null:
                    options = options with { RatePerSecond = int.Parse(value) };
                    i++;
                    break;
                case "--seconds" when value is not null:
                    options = options with { Seconds = int.Parse(value) };
                    i++;
                    break;
                case "--postgres" when value is not null:
                    options = options with { AdminConnectionString = value };
                    i++;
                    break;
                case "--json" when value is not null:
                    options = options with { JsonPath = value };
                    i++;
                    break;
                case "--help" or "-h":
                    options = options with { Help = true };
                    break;
                default:
                    throw new ArgumentException($"Unrecognised argument '{args[i]}'. Try --help.");
            }
        }

        return options;
    }

    public static string Usage => """
        Millrace benchmarks — #49, method in docs/benchmarks.md

          dotnet run -c Release --project bench/Millrace.Benchmarks -- [options]

        Start the database first:  cd bench && docker compose up -d

          --all                 Everything, at the published defaults (the same as no arguments).
          --scenario <list>     enqueue, drain, latency, workflow          (default: all four)
          --system <list>       millrace, hangfire, workflowcore           (default: all three)
          --tuning <list>       matched, default                           (default: both)
          --jobs <n>            Jobs per job-scenario run                  (default: 10000)
          --instances <n>       Workflow instances per run                 (default: 2000)
          --workers <n>         Worker concurrency, all systems            (default: 20)
          --producers <n>       Concurrent enqueueing threads              (default: 8)
          --repeats <n>         Measured runs per cell; median published   (default: 3)
          --rate <n>            Arrivals per second, latency scenario      (default: 200)
          --seconds <n>         Duration of the latency scenario           (default: 15)
          --postgres <cs>       Connection string to the bench server
          --json <path>         Write every repeat, not just the medians

        Lists are comma-separated: --scenario drain,latency --system millrace,hangfire
        """;

    private static string[] Split(string value) =>
        [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.ToLowerInvariant())];
}
