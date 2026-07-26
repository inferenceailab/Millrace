using System.Text.Json;

namespace Millrace;

/// <summary>Node-level configuration for workers, scheduling, and serialization.</summary>
public sealed class MillraceOptions
{
    /// <summary>The queue name used when none is given.</summary>
    /// <remarks>
    /// A constant rather than a setting, because both ends fall back to it independently: an
    /// enqueue that names no queue and a node that configures none have to arrive at the same
    /// string, or the job is written somewhere nothing claims from.
    /// </remarks>
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

    /// <summary>How often a worker renews the leases on jobs it is running.</summary>
    /// <remarks>
    /// <see cref="LeaseDuration"/> must exceed this and startup validation refuses a configuration
    /// where it does not — the gap between the two is the entire tolerance for renewal latency and
    /// inter-node clock skew. Shortening this is what allows a correspondingly shorter lease, which
    /// is the value that governs how long a crashed node's jobs stay unclaimable; the cost is more
    /// renewal traffic against storage.
    /// </remarks>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Cadence of the opportunistic scheduler pass (due activation + recurring fires).</summary>
    public TimeSpan SchedulerInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling on how many jobs a worker claims in one round trip.</summary>
    /// <remarks>
    /// The effective size is the smaller of this and the node's free capacity, so a worker never
    /// claims work it has no slot to run. Raising it amortises the claim query over more jobs while
    /// the pool is mostly idle, and changes nothing once it is saturated.
    /// </remarks>
    public int ClaimBatchSize { get; set; } = 16;

    /// <summary>
    /// Ceiling on the work one scheduler pass does — both due jobs activated and recurring
    /// definitions considered for firing.
    /// </summary>
    /// <remarks>
    /// A pass makes one call of each kind and does not loop, so a backlog larger than this drains
    /// over consecutive passes at <see cref="SchedulerInterval"/>. It bounds the size of a single
    /// transaction rather than throughput: a thousand jobs falling due in the same second are all
    /// still activated, just not all at once.
    /// </remarks>
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

    /// <summary>
    /// Queue that workflow activity jobs are enqueued to.
    /// </summary>
    /// <remarks>
    /// Explicit rather than derived from <see cref="Queues"/>: a node configured to claim only, say,
    /// <c>reports</c> would otherwise enqueue activities somewhere nothing claims from, and the
    /// instance would hang with no error anywhere. Every node in a deployment must agree on this
    /// value, and at least one must claim from it.
    /// </remarks>
    public string WorkflowQueue { get; set; } = DefaultQueue;

    /// <summary>Used for job arguments and workflow data documents.</summary>
    public JsonSerializerOptions SerializerOptions { get; set; } = new(JsonSerializerDefaults.General);
}
