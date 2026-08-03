using System.Globalization;
using Microsoft.AspNetCore.Html;

namespace Heimdall.Api.Services;

/// <summary>Formats average concurrent process counts for app usage tables.</summary>
public static class ConcurrentDisplay
{
    public static IHtmlContent Render(double avgConcurrent) =>
        new HtmlString(Format(avgConcurrent));

    public static string Format(double avgConcurrent)
    {
        if (avgConcurrent <= 0)
            return "—";

        return avgConcurrent.ToString("0.#", CultureInfo.InvariantCulture);
    }
}
