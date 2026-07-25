using Millrace.Scheduling;
using Xunit;

namespace Millrace.Tests.Scheduling;

public class CronExpressionTests
{
    private static DateTimeOffset Utc(string iso) =>
        DateTimeOffset.Parse(iso, null, System.Globalization.DateTimeStyles.AssumeUniversal);

    [Theory]
    // every minute
    [InlineData("* * * * *", "2026-07-24T10:15:30Z", "2026-07-24T10:16:00Z")]
    [InlineData("* * * * *", "2026-07-24T10:15:00Z", "2026-07-24T10:16:00Z")] // strictly after
    // daily at 03:00
    [InlineData("0 3 * * *", "2026-07-24T02:59:00Z", "2026-07-24T03:00:00Z")]
    [InlineData("0 3 * * *", "2026-07-24T03:00:00Z", "2026-07-25T03:00:00Z")]
    // step minutes
    [InlineData("*/15 * * * *", "2026-07-24T10:16:00Z", "2026-07-24T10:30:00Z")]
    [InlineData("*/15 * * * *", "2026-07-24T10:45:00Z", "2026-07-24T11:00:00Z")]
    // "N/step" = N through max
    [InlineData("5/15 * * * *", "2026-07-24T10:00:00Z", "2026-07-24T10:05:00Z")]
    [InlineData("5/15 * * * *", "2026-07-24T10:05:00Z", "2026-07-24T10:20:00Z")]
    [InlineData("5/15 * * * *", "2026-07-24T10:50:00Z", "2026-07-24T11:05:00Z")]
    // range with step
    [InlineData("0 8-18/2 * * *", "2026-07-24T08:30:00Z", "2026-07-24T10:00:00Z")]
    [InlineData("0 8-18/2 * * *", "2026-07-24T18:00:00Z", "2026-07-25T08:00:00Z")]
    // lists
    [InlineData("30 4 1,15 * *", "2026-07-02T00:00:00Z", "2026-07-15T04:30:00Z")]
    [InlineData("30 4 1,15 * *", "2026-07-15T04:30:00Z", "2026-08-01T04:30:00Z")]
    // weekday range
    [InlineData("0 22 * * 1-5", "2026-07-24T22:00:00Z", "2026-07-27T22:00:00Z")] // Fri -> Mon
    // names
    [InlineData("0 0 * JAN SUN", "2026-07-24T00:00:00Z", "2027-01-03T00:00:00Z")]
    [InlineData("0 0 * * MON", "2026-07-24T00:00:00Z", "2026-07-27T00:00:00Z")]
    // 7 == Sunday
    [InlineData("0 0 * * 7", "2026-07-24T00:00:00Z", "2026-07-26T00:00:00Z")]
    [InlineData("0 0 * * 0", "2026-07-24T00:00:00Z", "2026-07-26T00:00:00Z")]
    // month rollover to first of month
    [InlineData("0 0 1 * *", "2026-07-24T00:00:00Z", "2026-08-01T00:00:00Z")]
    // year rollover
    [InlineData("0 0 1 1 *", "2026-07-24T00:00:00Z", "2027-01-01T00:00:00Z")]
    // leap day
    [InlineData("0 0 29 2 *", "2026-07-24T00:00:00Z", "2028-02-29T00:00:00Z")]
    // vixie OR rule: both day fields restricted -> either matches (Fri 2026-08-07 before 13th)
    [InlineData("0 0 13 * FRI", "2026-08-01T00:00:00Z", "2026-08-07T00:00:00Z")]
    [InlineData("0 0 13 * FRI", "2026-08-07T00:00:00Z", "2026-08-13T00:00:00Z")]
    // a step on a star day field applies its mask (every 2nd day, not every day)
    [InlineData("0 0 */2 * *", "2026-08-01T00:00:00Z", "2026-08-03T00:00:00Z")]
    [InlineData("0 0 */2 * *", "2026-08-02T00:00:00Z", "2026-08-03T00:00:00Z")]
    // */N day fields count as restricted for the OR rule (dom 13 OR dow {SUN,TUE,THU,SAT})
    [InlineData("0 0 13 * */2", "2026-08-01T00:00:00Z", "2026-08-02T00:00:00Z")]
    public void GetNextOccurrence_returns_expected(string expression, string after, string expected)
    {
        var cron = CronExpression.Parse(expression);

        var next = cron.GetNextOccurrence(Utc(after));

        Assert.Equal(Utc(expected), next);
    }

    [Fact]
    public void GetNextOccurrence_converts_offset_input_to_utc()
    {
        var cron = CronExpression.Parse("0 12 * * *");
        var after = new DateTimeOffset(2026, 7, 24, 13, 0, 0, TimeSpan.FromHours(2)); // 11:00Z

        Assert.Equal(Utc("2026-07-24T12:00:00Z"), cron.GetNextOccurrence(after));
    }

    [Fact]
    public void GetNextOccurrence_returns_null_when_date_never_exists()
    {
        var cron = CronExpression.Parse("0 0 30 2 *"); // February 30th

        Assert.Null(cron.GetNextOccurrence(Utc("2026-07-24T00:00:00Z")));
    }

    [Theory]
    [InlineData("* * * *")]           // 4 fields
    [InlineData("* * * * * *")]       // 6 fields
    [InlineData("60 * * * *")]        // minute out of range
    [InlineData("* 24 * * *")]        // hour out of range
    [InlineData("* * 0 * *")]         // day-of-month out of range
    [InlineData("* * 32 * *")]
    [InlineData("* * * 0 *")]         // month out of range
    [InlineData("* * * 13 *")]
    [InlineData("* * * * 8")]         // day-of-week out of range
    [InlineData("5-1 * * * *")]       // inverted range
    [InlineData("*/0 * * * *")]       // zero step
    [InlineData("*/-5 * * * *")]      // negative step
    [InlineData("+5 * * * *")]        // signed value
    [InlineData("1,, * * * *")]       // empty list entry
    [InlineData("/5 * * * *")]        // step without base
    [InlineData("* * * FOO *")]       // bad name
    [InlineData("* * * * MOO")]
    [InlineData("1-2-3 * * * *")]     // malformed range
    public void Parse_rejects_invalid_expressions(string expression)
    {
        Assert.Throws<FormatException>(() => CronExpression.Parse(expression));

        Assert.False(CronExpression.TryParse(expression, out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void Parse_rejects_null_and_whitespace()
    {
        Assert.ThrowsAny<ArgumentException>(() => CronExpression.Parse(null!));
        Assert.ThrowsAny<ArgumentException>(() => CronExpression.Parse("   "));
    }

    [Fact]
    public void ToString_round_trips_the_original_text()
    {
        Assert.Equal("0 3 * * MON", CronExpression.Parse("0 3 * * MON").ToString());
    }

    [Fact]
    public void Names_are_case_insensitive()
    {
        var upper = CronExpression.Parse("0 0 * * mon");

        Assert.Equal(Utc("2026-07-27T00:00:00Z"), upper.GetNextOccurrence(Utc("2026-07-24T00:00:00Z")));
    }
}
