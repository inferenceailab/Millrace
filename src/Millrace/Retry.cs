namespace Millrace;

/// <summary>How retry delays are computed.</summary>
public enum RetryKind
{
    /// <summary>No retries: the first failure is final.</summary>
    None = 0,

    /// <summary>The same <see cref="Retry.BaseDelay"/> before every retry.</summary>
    Fixed = 1,

    /// <summary>Delays double from <see cref="Retry.BaseDelay"/>, capped at <see cref="Retry.MaxDelay"/>.</summary>
    Exponential = 2,
}

/// <summary>
/// Retry policy for a job. Serialized into the <c>JobRecord</c> as plain data — the engine, not
/// the storage provider, evaluates it. <see cref="MaxAttempts"/> counts <em>total</em> attempts
/// including the first, so <c>Retry.Exponential(5)</c> means at most five executions.
/// </summary>
public sealed record Retry
{
    private static readonly TimeSpan DefaultBaseDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromHours(1);

    /// <summary>Which formula computes the wait between attempts.</summary>
    /// <remarks>
    /// <see cref="RetryKind.None"/> overrides <see cref="MaxAttempts"/> rather than working with
    /// it: the first failure is final whatever the count says.
    /// </remarks>
    public RetryKind Kind { get; init; }

    /// <summary>Total attempts allowed, including the first. Minimum 1.</summary>
    public int MaxAttempts { get; init; } = 1;

    /// <summary>
    /// The wait for <see cref="RetryKind.Fixed"/>, or the first one for
    /// <see cref="RetryKind.Exponential"/>.
    /// </summary>
    /// <remarks>
    /// Zero is legal and means retry immediately — occasionally right for a contended resource, and
    /// a very fast way to spend the whole attempt budget when it is not.
    /// </remarks>
    public TimeSpan BaseDelay { get; init; }

    /// <summary>Ceiling on the computed wait.</summary>
    /// <remarks>
    /// Does the work for <see cref="RetryKind.Exponential"/>, where doubling would otherwise run
    /// away. <see cref="Fixed"/> sets it equal to its delay, so this never holds a value that
    /// silently does not apply.
    /// </remarks>
    public TimeSpan MaxDelay { get; init; }

    /// <summary>The first failure is final; the job goes straight to <c>Dead</c>.</summary>
    public static Retry None { get; } = new() { Kind = RetryKind.None, MaxAttempts = 1 };

    /// <summary>A policy that waits the same <paramref name="delay"/> before every retry.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="delay"/> is negative, or <paramref name="maxAttempts"/> is below 1. One is
    /// the floor because it counts the first execution — a policy allowing zero attempts describes
    /// a job that never runs.
    /// </exception>
    public static Retry Fixed(TimeSpan delay, int maxAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delay.Ticks, nameof(delay));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        return new Retry { Kind = RetryKind.Fixed, MaxAttempts = maxAttempts, BaseDelay = delay, MaxDelay = delay };
    }

    /// <summary>A policy whose wait doubles after each failure, up to a ceiling.</summary>
    /// <remarks>
    /// Defaults to a 5 second base and a 1 hour ceiling — quick enough to ride out a blip, slow
    /// enough that a sustained outage is not hammered. The doubling is clamped internally, so a
    /// large <paramref name="maxAttempts"/> cannot overflow the computed delay.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxAttempts"/> is below 1, <paramref name="baseDelay"/> is negative, or
    /// <paramref name="maxDelay"/> is below the base — a ceiling under the floor has no reading.
    /// </exception>
    public static Retry Exponential(int maxAttempts, TimeSpan? baseDelay = null, TimeSpan? maxDelay = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        var @base = baseDelay ?? DefaultBaseDelay;
        var max = maxDelay ?? DefaultMaxDelay;
        ArgumentOutOfRangeException.ThrowIfNegative(@base.Ticks, nameof(baseDelay));
        ArgumentOutOfRangeException.ThrowIfLessThan(max.Ticks, @base.Ticks, nameof(maxDelay));
        return new Retry { Kind = RetryKind.Exponential, MaxAttempts = maxAttempts, BaseDelay = @base, MaxDelay = max };
    }

    /// <summary>
    /// Delay before the next attempt, given that attempt number <paramref name="attempt"/>
    /// (1-based) just failed. Returns <see langword="null"/> when attempts are exhausted and the
    /// job must be dead-lettered.
    /// </summary>
    public TimeSpan? NextDelay(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        if (attempt >= MaxAttempts || Kind == RetryKind.None)
        {
            return null;
        }

        if (Kind == RetryKind.Fixed)
        {
            return BaseDelay;
        }

        // Exponential: BaseDelay * 2^(attempt-1), capped at MaxDelay without overflowing.
        var exponent = Math.Min(attempt - 1, 62);
        if (BaseDelay.Ticks == 0)
        {
            return TimeSpan.Zero;
        }

        if (BaseDelay.Ticks > MaxDelay.Ticks >> exponent)
        {
            return MaxDelay;
        }

        return TimeSpan.FromTicks(BaseDelay.Ticks << exponent);
    }
}
