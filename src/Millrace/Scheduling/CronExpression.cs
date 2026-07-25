using System.Globalization;

namespace Millrace.Scheduling;

/// <summary>
/// A five-field cron expression (<c>minute hour day-of-month month day-of-week</c>) in the
/// vixie-cron dialect: lists (<c>1,15</c>), ranges (<c>8-18</c>), steps (<c>*/5</c>,
/// <c>8-18/2</c>, <c>5/15</c>), <c>JAN</c>–<c>DEC</c> and <c>SUN</c>–<c>SAT</c> names, and
/// <c>0</c> or <c>7</c> for Sunday. When both day fields are restricted (neither is exactly
/// <c>*</c>), the day matches when <em>either</em> field matches — the classic vixie OR rule;
/// the restriction flags only choose OR-versus-AND, the field masks always apply (so
/// <c>*/2</c> in a day field steps as written). Evaluation is UTC-only at minute resolution;
/// time zones are a later phase.
/// </summary>
public sealed class CronExpression
{
    private static readonly string[] MonthNames =
        ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    private static readonly string[] DayNames =
        ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    private readonly string _expression;
    private readonly ulong _minutes;      // bits 0..59
    private readonly ulong _hours;        // bits 0..23
    private readonly ulong _daysOfMonth;  // bits 1..31
    private readonly ulong _months;       // bits 1..12
    private readonly ulong _daysOfWeek;   // bits 0..6, Sunday = 0
    private readonly bool _dayOfMonthRestricted;
    private readonly bool _dayOfWeekRestricted;

    private CronExpression(
        string expression, ulong minutes, ulong hours, ulong daysOfMonth, ulong months,
        ulong daysOfWeek, bool dayOfMonthRestricted, bool dayOfWeekRestricted)
    {
        _expression = expression;
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        _dayOfMonthRestricted = dayOfMonthRestricted;
        _dayOfWeekRestricted = dayOfWeekRestricted;
    }

    public static CronExpression Parse(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        var fields = expression.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            throw new FormatException(
                $"Cron expression '{expression}' must have exactly 5 fields " +
                $"(minute hour day-of-month month day-of-week) but has {fields.Length}.");
        }

        var minutes = ParseField(fields[0], 0, 59, names: null, "minute", expression);
        var hours = ParseField(fields[1], 0, 23, names: null, "hour", expression);
        var daysOfMonth = ParseField(fields[2], 1, 31, names: null, "day-of-month", expression);
        var months = ParseField(fields[3], 1, 12, MonthNames, "month", expression);
        var daysOfWeek = ParseField(fields[4], 0, 7, DayNames, "day-of-week", expression);

        // Both 0 and 7 mean Sunday; fold bit 7 onto bit 0.
        if ((daysOfWeek & (1UL << 7)) != 0)
        {
            daysOfWeek = (daysOfWeek | 1UL) & ~(1UL << 7);
        }

        return new CronExpression(
            expression, minutes, hours, daysOfMonth, months, daysOfWeek,
            dayOfMonthRestricted: fields[2] != "*",
            dayOfWeekRestricted: fields[4] != "*");
    }

    public static bool TryParse(string expression, out CronExpression? parsed)
    {
        try
        {
            parsed = Parse(expression);
            return true;
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            parsed = null;
            return false;
        }
    }

    /// <summary>
    /// The next occurrence strictly after <paramref name="after"/>, in UTC at minute resolution,
    /// or <see langword="null"/> when no occurrence exists within five years (e.g. February 30).
    /// </summary>
    public DateTimeOffset? GetNextOccurrence(DateTimeOffset after)
    {
        var utc = after.UtcDateTime;
        var t = new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc)
            .AddMinutes(1);
        var bound = t.AddYears(5);

        while (t < bound)
        {
            if ((_months & (1UL << t.Month)) == 0)
            {
                t = new DateTime(t.Year, t.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
                continue;
            }

            if (!DayMatches(t))
            {
                t = t.Date.AddDays(1);
                continue;
            }

            if ((_hours & (1UL << t.Hour)) == 0)
            {
                t = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);
                continue;
            }

            if ((_minutes & (1UL << t.Minute)) == 0)
            {
                t = t.AddMinutes(1);
                continue;
            }

            return new DateTimeOffset(t);
        }

        return null;
    }

    public override string ToString() => _expression;

    private bool DayMatches(DateTime t)
    {
        var dayOfMonth = (_daysOfMonth & (1UL << t.Day)) != 0;
        var dayOfWeek = (_daysOfWeek & (1UL << (int)t.DayOfWeek)) != 0;

        // The restriction flags choose the vixie OR rule versus plain intersection; the masks
        // themselves ALWAYS apply (a bare "*" field simply has a full mask).
        return _dayOfMonthRestricted && _dayOfWeekRestricted
            ? dayOfMonth || dayOfWeek
            : dayOfMonth && dayOfWeek;
    }

    private static ulong ParseField(
        string field, int min, int max, string[]? names, string fieldName, string expression)
    {
        ulong mask = 0;
        foreach (var part in field.Split(','))
        {
            mask |= ParsePart(part, min, max, names, fieldName, expression);
        }

        return mask;
    }

    private static ulong ParsePart(
        string part, int min, int max, string[]? names, string fieldName, string expression)
    {
        var range = part;
        var step = 1;

        var slash = part.IndexOf('/');
        if (slash >= 0)
        {
            range = part[..slash];
            var stepText = part[(slash + 1)..];
            if (!int.TryParse(stepText, NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1)
            {
                throw Error($"step '{stepText}' must be a positive integer");
            }
        }

        int start, end;
        if (range == "*")
        {
            start = min;
            end = max;
        }
        else
        {
            var dash = range.IndexOf('-');
            if (dash >= 0)
            {
                start = ParseValue(range[..dash]);
                end = ParseValue(range[(dash + 1)..]);
            }
            else
            {
                start = ParseValue(range);
                // vixie: "N/step" means N through the field maximum.
                end = slash >= 0 ? max : start;
            }
        }

        if (start < min || start > max || end < min || end > max)
        {
            throw Error($"value out of range {min}-{max}");
        }

        if (start > end)
        {
            throw Error($"range {start}-{end} is inverted (wrapping ranges are not supported)");
        }

        ulong mask = 0;
        for (var v = start; v <= end; v += step)
        {
            mask |= 1UL << v;
        }

        return mask;

        int ParseValue(string text)
        {
            if (text.Length == 0)
            {
                throw Error("empty value");
            }

            if (names is not null)
            {
                for (var i = 0; i < names.Length; i++)
                {
                    if (text.Equals(names[i], StringComparison.OrdinalIgnoreCase))
                    {
                        // Month names are 1-based (JAN=1); day names are 0-based (SUN=0).
                        return names == MonthNames ? i + 1 : i;
                    }
                }
            }

            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                throw Error($"'{text}' is not a valid value");
            }

            return value;
        }

        FormatException Error(string reason) => new(
            $"Cron expression '{expression}': invalid {fieldName} field part '{part}' — {reason}.");
    }
}
