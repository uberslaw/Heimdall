using System.Globalization;
using Microsoft.AspNetCore.Html;

namespace Heimdall.Api.Services;

/// <summary>
/// Shared "clickable large duration" control (pairs with wwwroot/js/hd-duration.js and the
/// <c>.hd-duration</c> CSS class). Renders a span that starts by showing the largest sensible
/// whole-number unit — e.g. "27 days" instead of "27.07:53:28" — and lets the user click it to cycle
/// through fixed units (seconds → minutes → hours → days → weeks → calendar months → years) before
/// wrapping back to the auto default.
///
/// Use for any TimeSpan-ish total that can plausibly run into multi-hour/day ranges: active/disconnected
/// time, session durations, app open-time totals, utilization periods, uptime-like fields. Skip fixed,
/// always-small intervals (e.g. a 300-second config refresh interval) — those never benefit from cycling.
/// </summary>
public static class DurationDisplay
{
    public const double SecondsPerMinute = 60;
    public const double SecondsPerHour = 3600;
    public const double SecondsPerDay = 86400;
    public const double SecondsPerWeek = 604800;

    /// <summary>Average Gregorian calendar month (365.2425 / 12 days) — a reasonable "X months" reading without a fixed calendar anchor.</summary>
    public const double SecondsPerMonth = 2629746;

    /// <summary>Average Gregorian year (365.2425 days) — matches SecondsPerMonth * 12.</summary>
    public const double SecondsPerYear = 31556952;

    public static IHtmlContent Render(double totalSeconds)
    {
        var seconds = Math.Max(0, totalSeconds);
        var text = FormatAuto(seconds);
        var secondsAttr = seconds.ToString("0.##", CultureInfo.InvariantCulture);
        return new HtmlString(
            "<span class=\"hd-duration\" data-seconds=\"" + secondsAttr + "\" " +
            "title=\"Click to cycle units (seconds \u2192 minutes \u2192 hours \u2192 days \u2192 weeks \u2192 months \u2192 years)\">" +
            text + "</span>");
    }

    public static IHtmlContent Render(long totalSeconds) => Render((double)totalSeconds);

    public static IHtmlContent Render(TimeSpan span) => Render(span.TotalSeconds);

    private static string FormatAuto(double seconds)
    {
        if (seconds < SecondsPerMinute) return FormatUnit(seconds, 1, "second");
        if (seconds < SecondsPerHour) return FormatUnit(seconds, SecondsPerMinute, "minute");
        if (seconds < SecondsPerDay) return FormatUnit(seconds, SecondsPerHour, "hour");
        if (seconds < SecondsPerWeek) return FormatUnit(seconds, SecondsPerDay, "day");
        if (seconds < SecondsPerMonth) return FormatUnit(seconds, SecondsPerWeek, "week");
        if (seconds < SecondsPerYear) return FormatUnit(seconds, SecondsPerMonth, "month");
        return FormatUnit(seconds, SecondsPerYear, "year");
    }

    private static string FormatUnit(double seconds, double perUnit, string unitLabel)
    {
        var value = (long)Math.Round(seconds / perUnit, MidpointRounding.AwayFromZero);
        var label = value == 1 ? unitLabel : unitLabel + "s";
        return value.ToString("N0", CultureInfo.InvariantCulture) + " " + label;
    }
}
