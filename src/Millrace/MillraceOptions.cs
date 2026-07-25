using System.Text.Json;

namespace Millrace;

/// <summary>Node-level configuration for workers, scheduling, and serialization.</summary>
public sealed class MillraceOptions
{
    public const string DefaultQueue = "default";

    /// <summary>Queues this node's workers claim from (unordered — no queue precedence).</summary>
    public IList<string> Queues { get; } = [DefaultQueue];

    /// <summary>Maximum concurrently executing jobs on this node.</summary>
    public int MaxParallelism { get; set; } = Math.Max(4, Environment.ProcessorCount * 2);

    /// <summary>
    /// Claim lease length. Must exceed <see cref="HeartbeatInterval"/> by enough margin for
    /// renewal latency plus inter-node clock skew (defaults tolerate ~4 minutes of skew);
    /// validated at startup.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Cadence of the opportunistic scheduler pass (due activation + recurring fires).</summary>
    public TimeSpan SchedulerInterval { get; set; } = TimeSpan.FromSeconds(1);

    public int ClaimBatchSize { get; set; } = 16;

    public int ActivationBatchSize { get; set; } = 100;

    /// <summary>Adaptive polling floor (used right after work was found).</summary>
    public TimeSpan MinPollDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Adaptive polling ceiling when idle; also the wakeup fallback with a notifier.</summary>
    public TimeSpan MaxPollDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Phase-1 shutdown drain: in-flight jobs get this long to finish, leases renewing.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Phase-2 shutdown grace after job tokens fire, before jobs are released.</summary>
    public TimeSpan ShutdownGrace { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Poison-pill threshold: a claim returning <c>Attempt - Failures</c> above this means the
    /// job keeps getting interrupted without ever recording a failure — presumed to crash
    /// workers — and is dead-lettered without executing.
    /// </summary>
    public int InterruptionLimit { get; set; } = 10;

    /// <summary>Runs the worker pool on this node.</summary>
    public bool WorkerEnabled { get; set; } = true;

    /// <summary>Runs the opportunistic scheduler on this node (independent of the workers).</summary>
    public bool SchedulerEnabled { get; set; } = true;

    /// <summary>Applied when <see cref="EnqueueOptions.Retry"/> is not set.</summary>
    public Retry DefaultRetry { get; set; } = Retry.Exponential(5);

    /// <summary>Used for job arguments and workflow data documents.</summary>
    public JsonSerializerOptions SerializerOptions { get; set; } = new(JsonSerializerDefaults.General);
}
