namespace Weft;

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

    public RetryKind Kind { get; init; }

    /// <summary>Total attempts allowed, including the first. Minimum 1.</summary>
    public int MaxAttempts { get; init; } = 1;

    public TimeSpan BaseDelay { get; init; }

    public TimeSpan MaxDelay { get; init; }

    /// <summary>The first failure is final; the job goes straight to <c>Dead</c>.</summary>
    public static Retry None { get; } = new() { Kind = RetryKind.None, MaxAttempts = 1 };

    public static Retry Fixed(TimeSpan delay, int maxAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delay.Ticks, nameof(delay));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        return new Retry { Kind = RetryKind.Fixed, MaxAttempts = maxAttempts, BaseDelay = delay, MaxDelay = delay };
    }

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
