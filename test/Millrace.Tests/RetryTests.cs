using Xunit;

namespace Millrace.Tests;

public class RetryTests
{
    [Fact]
    public void None_never_retries()
    {
        Assert.Null(Retry.None.NextDelay(1));
        Assert.Equal(1, Retry.None.MaxAttempts);
    }

    [Fact]
    public void Fixed_returns_constant_delay_until_exhausted()
    {
        var retry = Retry.Fixed(TimeSpan.FromSeconds(5), maxAttempts: 3);

        Assert.Equal(TimeSpan.FromSeconds(5), retry.NextDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(5), retry.NextDelay(2));
        Assert.Null(retry.NextDelay(3));
        Assert.Null(retry.NextDelay(4)); // beyond exhaustion stays null
    }

    [Fact]
    public void Exponential_doubles_from_base_delay()
    {
        var retry = Retry.Exponential(5, baseDelay: TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(1), retry.NextDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), retry.NextDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(4), retry.NextDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(8), retry.NextDelay(4));
        Assert.Null(retry.NextDelay(5));
    }

    [Fact]
    public void Exponential_caps_at_max_delay()
    {
        var retry = Retry.Exponential(10,
            baseDelay: TimeSpan.FromMinutes(10), maxDelay: TimeSpan.FromMinutes(15));

        Assert.Equal(TimeSpan.FromMinutes(10), retry.NextDelay(1));
        Assert.Equal(TimeSpan.FromMinutes(15), retry.NextDelay(2));
        Assert.Equal(TimeSpan.FromMinutes(15), retry.NextDelay(9));
    }

    [Fact]
    public void Exponential_with_huge_attempt_does_not_overflow()
    {
        var retry = new Retry
        {
            Kind = RetryKind.Exponential,
            MaxAttempts = int.MaxValue,
            BaseDelay = TimeSpan.FromHours(1),
            MaxDelay = TimeSpan.FromDays(365),
        };

        Assert.Equal(TimeSpan.FromDays(365), retry.NextDelay(200));
    }

    [Fact]
    public void Exponential_defaults_are_5s_base_1h_cap()
    {
        var retry = Retry.Exponential(3);

        Assert.Equal(TimeSpan.FromSeconds(5), retry.BaseDelay);
        Assert.Equal(TimeSpan.FromHours(1), retry.MaxDelay);
    }

    [Fact]
    public void Factories_validate_arguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Retry.Fixed(TimeSpan.FromSeconds(1), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Retry.Fixed(TimeSpan.FromSeconds(-1), 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => Retry.Exponential(0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Retry.Exponential(3, baseDelay: TimeSpan.FromSeconds(10), maxDelay: TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void NextDelay_rejects_non_positive_attempt()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Retry.None.NextDelay(0));
    }

    [Fact]
    public void Retry_round_trips_through_json()
    {
        var retry = Retry.Exponential(4, baseDelay: TimeSpan.FromSeconds(2));

        var json = System.Text.Json.JsonSerializer.Serialize(retry);
        var back = System.Text.Json.JsonSerializer.Deserialize<Retry>(json);

        Assert.Equal(retry, back);
    }
}
