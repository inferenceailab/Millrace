namespace Weft;

/// <summary>Per-job options for <see cref="IJobClient"/> calls (ARCHITECTURE.md §5.2).</summary>
public sealed record EnqueueOptions
{
    /// <summary>Target queue; defaults to <see cref="WeftOptions.DefaultQueue"/>.</summary>
    public string? Queue { get; init; }

    /// <summary>Higher priority is claimed first; FIFO within equal priority. Default 0.</summary>
    public int Priority { get; init; }

    /// <summary>Retry policy; defaults to <see cref="WeftOptions.DefaultRetry"/>.</summary>
    public Retry? Retry { get; init; }

    /// <summary>
    /// Enqueue-time dedup key, unique among active jobs within the ambient tenant scope
    /// (§4.2.6). A duplicate enqueue is a no-op returning the existing job's id. Not supported
    /// on recurring jobs in 0.1.
    /// </summary>
    public string? IdempotencyKey { get; init; }
}
