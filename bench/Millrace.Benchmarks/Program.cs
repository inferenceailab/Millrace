using Millrace.Benchmarks;
using Millrace.Benchmarks.Systems;

// The results are published, so they format the same wherever they are produced. Without this the
// separator is the machine's locale and a table generated here would not match one generated
// elsewhere — a difference in the numbers that is not a difference in the measurements.
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

var options = RunOptions.Parse(args);

if (options.Help)
{
    Console.WriteLine(RunOptions.Usage);
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

var ct = cancellation.Token;

try
{
    await Database.WaitForServerAsync(options.AdminConnectionString, ct);
}
catch (Exception e)
{
    Console.Error.WriteLine($"No PostgreSQL at {options.AdminConnectionString}: {e.Message}");
    Console.Error.WriteLine("Start it with: cd bench && docker compose up -d");
    return 1;
}

var machine = Machine.Describe(await Database.ServerVersionAsync(options.AdminConnectionString, ct));
var counter = new BenchCounter();
var results = new List<Aggregate>();

Console.WriteLine($"Millrace benchmarks — {machine.Cores} cores, {machine.Gc} GC, PostgreSQL {machine.PostgreSql}");
Console.WriteLine();

// Warmup, discarded. It pays the JIT, the connection pool and PostgreSQL's page cache for the
// schema once, so the first measured run is not the only one that pays them.
//
// It runs at full size, which is slower and is the point. Every measured run begins by dropping the
// database the previous run filled, so a small warmup leaves the first measured run dropping
// something tiny while every later one drops a full database — and the first run is then faster for
// a reason that has nothing to do with the system. That was worth 211 inst/s against a steady 65
// for WorkflowCore, three times the number it can actually sustain. A warmup has to leave the world
// in the state a measured run leaves it in, or it is just a fast first result.
foreach (var name in options.SystemNames.Where(n => n is "millrace" or "hangfire"))
{
    Console.WriteLine($"warmup   {name}");
    await using var system = JobSystem(name, Tuning.Matched);
    await Scenarios.DrainThroughputAsync(system, options, Tuning.Matched, counter, ct);
    await system.StopWorkersAsync(ct);
}

// The workflow engines get the same courtesy, for the same reason.
if (options.ScenarioNames.Contains("workflow"))
{
    foreach (var name in options.SystemNames.Where(n => n is "millrace" or "workflowcore"))
    {
        Console.WriteLine($"warmup   {name} (workflow)");
        await using var system = WorkflowSystem(name);
        await Scenarios.WorkflowThroughputAsync(system, options, counter, ct);
        await system.StopWorkersAsync(ct);
    }
}

foreach (var scenario in options.ScenarioNames)
{
    foreach (var tuning in Tunings(scenario))
    {
        foreach (var name in Systems(scenario))
        {
            var repeats = new List<Measurement>();
            var timedOut = 0;

            for (var repeat = 0; repeat < options.Repeats; repeat++)
            {
                Console.Write($"{scenario,-9}{name,-14}{tuning.Name,-9}run {repeat + 1}/{options.Repeats} ... ");

                Measurement? measurement = null;
                try
                {
                    measurement = await RunOnceAsync(scenario, name, tuning);
                }
                catch (TimeoutException)
                {
                    // One run that never drains should cost one run, not the suite. Before this, a
                    // single stalled Hangfire drain took a 45-minute suite with it at repeat 5 of 9.
                    timedOut++;
                    Console.WriteLine($"TIMED OUT after {options.Timeout.TotalMinutes:N0} min");
                }

                if (measurement is not null)
                {
                    repeats.Add(measurement);
                    Console.WriteLine(scenario == "latency"
                        ? $"p50 {measurement.P50Ms:N0} ms, p99 {measurement.P99Ms:N0} ms"
                        : $"{measurement.ThroughputPerSecond:N0}/s");
                }

                // Let the previous host's shutdown finish before the next run drops its database.
                // Recreating storage underneath a worker pool that has not fully stopped is the most
                // plausible route to the stall above: PostgreSQL's DROP ... WITH (FORCE) terminates
                // whatever is still connected, and a fetch loop that loses its connection mid-claim
                // is exactly the shape of thing that then waits out an invisibility timeout.
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }

            if (repeats.Count == 0)
            {
                Console.WriteLine($"  ! {scenario}/{name}/{tuning.Name}: every run timed out, no result");
                continue;
            }

            results.Add(Aggregate.From(repeats, timedOut));
        }
    }
}

Console.WriteLine();
Console.WriteLine(Report.Markdown(machine, results, options));

if (options.JsonPath is not null)
{
    await File.WriteAllTextAsync(options.JsonPath, Report.Json(machine, results, options), ct);
    Console.WriteLine($"Raw repeats written to {options.JsonPath}");
}

return 0;

async Task<Measurement> RunOnceAsync(string scenario, string name, Tuning tuning)
{
    if (scenario == "workflow")
    {
        await using var system = WorkflowSystem(name);
        try
        {
            return await Scenarios.WorkflowThroughputAsync(system, options, counter, ct);
        }
        finally
        {
            await system.StopWorkersAsync(ct);
        }
    }

    await using var jobs = JobSystem(name, tuning);
    try
    {
        return scenario switch
        {
            "enqueue" => await Scenarios.EnqueueThroughputAsync(jobs, options, tuning, ct),
            "drain" => await Scenarios.DrainThroughputAsync(jobs, options, tuning, counter, ct),
            "latency" => await Scenarios.LatencyAsync(jobs, options, tuning, counter, ct),
            _ => throw new ArgumentException($"Unknown scenario '{scenario}'."),
        };
    }
    finally
    {
        if (scenario != "enqueue")
        {
            await jobs.StopWorkersAsync(ct);
        }
    }
}

IJobSystem JobSystem(string name, Tuning tuning) => name switch
{
    "millrace" => new MillraceJobSystem(options.AdminConnectionString, counter, options.Workers, tuning),
    "hangfire" => new HangfireJobSystem(options.AdminConnectionString, counter, options.Workers, tuning),
    _ => throw new ArgumentException($"'{name}' is not a job system."),
};

IWorkflowSystem WorkflowSystem(string name) => name switch
{
    "millrace" => new MillraceWorkflowSystem(options.AdminConnectionString, counter, options.Workers),
    "workflowcore" => new WorkflowCoreWorkflowSystem(options.AdminConnectionString, counter, options.Workers),
    _ => throw new ArgumentException($"'{name}' is not a workflow engine."),
};

// WorkflowCore is not a job substrate and Hangfire is not a workflow engine, so the matrix is not a
// full cross product. Saying so here keeps the loop above honest about what it skips.
IEnumerable<string> Systems(string scenario) => scenario == "workflow"
    ? options.SystemNames.Where(n => n is "millrace" or "workflowcore")
    : options.SystemNames.Where(n => n is "millrace" or "hangfire");

// Polling only affects a system that is waiting for work. Enqueue never waits, and the workflow
// scenario runs matched only — there is no second WorkflowCore configuration worth publishing.
IEnumerable<Tuning> Tunings(string scenario) => scenario is "enqueue" or "workflow"
    ? [Tuning.Matched]
    : options.Tunings;
