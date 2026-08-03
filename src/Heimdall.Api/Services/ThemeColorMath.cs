using System.Globalization;

namespace Heimdall.Api.Services;

/// <summary>Small hex-colour helpers shared by the server-side custom theme CSS builder and the
/// client-side live preview script on the Theme page — keep the two in numeric lockstep.</summary>
public static class ThemeColorMath
{
    public static (byte R, byte G, byte B) ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return (0, 0, 0);

        var h = hex.Trim().TrimStart('#');
        if (h.Length == 3)
            h = string.Concat(h.Select(c => new string(c, 2)));
        if (h.Length != 6 || !byte.TryParse(h[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r))
            return (0, 0, 0);

        byte.TryParse(h[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g);
        byte.TryParse(h[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b);
        return (r, g, b);
    }

    public static string Rgba(string hex, double alpha)
    {
        var (r, g, b) = ParseHex(hex);
        var a = Math.Clamp(alpha, 0, 1);
        return $"rgba({r}, {g}, {b}, {a.ToString("0.###", CultureInfo.InvariantCulture)})";
    }

    /// <summary>Blends towards white by <paramref name="amount"/> (0..1).</summary>
    public static string Lighten(string hex, double amount)
    {
        var (r, g, b) = ParseHex(hex);
        var t = Math.Clamp(amount, 0, 1);
        return ToHex(Mix(r, 255, t), Mix(g, 255, t), Mix(b, 255, t));
    }

    /// <summary>Blends towards black by <paramref name="amount"/> (0..1).</summary>
    public static string Darken(string hex, double amount)
    {
        var (r, g, b) = ParseHex(hex);
        var t = Math.Clamp(amount, 0, 1);
        return ToHex(Mix(r, 0, t), Mix(g, 0, t), Mix(b, 0, t));
    }

    /// <summary>Picks black or white text for readable contrast against a given background hex.</summary>
    public static string ContrastText(string hex)
    {
        var (r, g, b) = ParseHex(hex);
        var luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
        return luminance > 0.6 ? "#1a1408" : "#f8f5ec";
    }

    private static int Mix(byte from, int to, double t) => (int)Math.Round(from + (to - from) * t);

    private static string ToHex(int r, int g, int b) =>
        $"#{Math.Clamp(r, 0, 255):x2}{Math.Clamp(g, 0, 255):x2}{Math.Clamp(b, 0, 255):x2}";
}
