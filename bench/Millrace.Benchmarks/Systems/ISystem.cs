namespace Millrace.Benchmarks.Systems;

/// <summary>
/// How aggressively the systems are allowed to poll.
/// </summary>
/// <remarks>
/// Two tunings exist because either one alone misleads. <see cref="Matched"/> puts every system on
/// the same polling floor, which is the only way a latency number says something about design
/// rather than about a default someone picked in 2016. <see cref="Default"/> leaves each system as
/// it ships, which is what an evaluator actually gets on day one. Publishing only the first flatters
/// nobody in particular but hides a real difference in what you have to know to be fast; publishing
/// only the second is how a benchmark wins an argument it did not have.
/// <para>
/// Worker concurrency is equalised in both. It is the one knob every deployment sets deliberately,
/// and leaving it at three different defaults would make every other number a comparison of core
/// counts.
/// </para>
/// </remarks>
public sealed record Tuning(string Name)
{
    public static readonly Tuning Matched = new("matched");

    public static readonly Tuning Default = new("default");

    /// <summary>The floor every system polls at under <see cref="Matched"/>.</summary>
    /// <remarks>
    /// 200 ms because that is Millrace's own <c>MinPollDelay</c> default and the value it was
    /// designed around — moving Millrace instead would mean tuning the system under test to beat
    /// the comparands, which is the thing this whole file exists to avoid.
    /// </remarks>
    public static readonly TimeSpan PollFloor = TimeSpan.FromMilliseconds(200);

    public bool IsMatched => Name == Matched.Name;
}

/// <summary>A job substrate that can be seeded, drained, and measured.</summary>
public interface IJobSystem : IAsyncDisposable
{
    string Name { get; }

    /// <summary>The package version behind the numbers, so a result can be traced to what produced it.</summary>
    string Version { get; }

    /// <summary>The knobs actually applied, as published in the results table.</summary>
    string Configuration { get; }

    /// <summary>Recreates storage, builds the host, and creates the schema — none of it measured.</summary>
    Task PrepareAsync(CancellationToken ct);

    /// <summary>Enqueues one job. Called on many threads at once.</summary>
    Task EnqueueAsync(long enqueuedTimestamp, CancellationToken ct);

    /// <summary>Starts processing. Nothing executes before this.</summary>
    Task StartWorkersAsync(CancellationToken ct);

    Task StopWorkersAsync(CancellationToken ct);
}

/// <summary>A workflow engine that can run the same linear definition.</summary>
public interface IWorkflowSystem : IAsyncDisposable
{
    string Name { get; }

    string Version { get; }

    string Configuration { get; }

    Task PrepareAsync(CancellationToken ct);

    /// <summary>Starts one instance of the shared three-step definition.</summary>
    Task StartInstanceAsync(long enqueuedTimestamp, CancellationToken ct);

    Task StartWorkersAsync(CancellationToken ct);

    Task StopWorkersAsync(CancellationToken ct);
}
