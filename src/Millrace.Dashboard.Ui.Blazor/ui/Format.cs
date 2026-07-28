using System.Globalization;

namespace Millrace.Dashboard.Ui.Blazor.App;

/// <summary>
/// Presentation helpers, matching <c>ui-shared/format.ts</c> exactly.
/// </summary>
/// <remarks>
/// A deliberate second implementation rather than a shared one. The TypeScript UIs share these
/// because they share a language; this is the same rules in C#, and the rules are the contract —
/// three dashboards rendering the same instant differently is a small bug that is very confusing to
/// hit. `FormatTimeTests` in the dashboard test project pins the two against each other.
/// </remarks>
public static class Format
{
    /// <summary>UTC, to the second, always — job times are UTC and a local rendering invites misreading.</summary>
    public static string Time(DateTimeOffset? value) => value is null
        ? "—"
        : value.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>"in 20m" or "3h ago", to one unit.</summary>
    public static string RelativeToNow(DateTimeOffset value, DateTimeOffset now)
    {
        var delta = value - now;
        var abs = delta.Duration();

        var text = abs.TotalMinutes < 1 ? $"{Math.Round(abs.TotalSeconds):N0}s"
            : abs.TotalHours < 1 ? $"{Math.Round(abs.TotalMinutes):N0}m"
            : abs.TotalDays < 1 ? $"{Math.Round(abs.TotalHours):N0}h"
            : $"{Math.Round(abs.TotalDays):N0}d";

        return delta >= TimeSpan.Zero ? $"in {text}" : $"{text} ago";
    }

    /// <summary>Whether a schedule's next fire time has already passed.</summary>
    public static bool IsOverdue(DateTimeOffset nextFireTime, DateTimeOffset now) => nextFireTime < now;

    /// <summary>"overdue by 3h" or "in 20m" — the schedule view's one piece of derived text.</summary>
    public static string DueText(DateTimeOffset nextFireTime, DateTimeOffset now)
    {
        var relative = RelativeToNow(nextFireTime, now);
        return IsOverdue(nextFireTime, now)
            ? $"overdue by {relative.Replace(" ago", string.Empty, StringComparison.Ordinal)}"
            : relative;
    }

    /// <summary>What to show an operator when a request failed.</summary>
    public static string ErrorMessage(Exception error) => error.Message;
}
