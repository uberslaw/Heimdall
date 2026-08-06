using System.Text.RegularExpressions;

namespace Heimdall.Shared;

/// <summary>
/// Eligibility rules for Discovery / ProcessCatalog: keep real executables only.
/// Excludes .tmp names, Windows TEMP (and typical installer temp) paths, and non-.exe files.
/// Name-only rows (no path) are allowed when the process name is not a junk extension —
/// App Lists / classifications often store bare names like "chrome".
/// </summary>
public static partial class DiscoveryCatalogFilter
{
    /// <summary>True when this process should be stored in the discovery catalog.</summary>
    public static bool IsEligible(string? processName, string? executablePath)
    {
        var name = (processName ?? "").Trim();
        var path = string.IsNullOrWhiteSpace(executablePath) ? "" : executablePath.Trim();

        if (name.Length == 0 && path.Length == 0)
            return false;

        // Names may contain dots (e.g. NVDisplay.Container) — only reject clear junk suffixes.
        if (HasJunkNameSuffix(name))
            return false;

        if (path.Length > 0)
        {
            if (EndsWithTmp(path) || IsTempPath(path))
                return false;

            // Discovery catalog is .exe only when a path is known.
            if (!HasExeExtension(path))
                return false;
        }

        return true;
    }

    /// <summary>True for catalog rows that should be deleted (tmp / TEMP / non-exe path).</summary>
    public static bool IsIneligibleCatalogEntry(string? processName, string? executablePath) =>
        !IsEligible(processName, executablePath);

    public static bool EndsWithTmp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var s = value.Trim();
        return s.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasExeExtension(string path)
    {
        try
        {
            return string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return path.TrimEnd().EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Process names ending in installer/temp or non-exe file suffixes (e.g. foo.tmp).
    /// Does not treat dotted Windows process names (NVDisplay.Container) as extensions.
    /// </summary>
    public static bool HasJunkNameSuffix(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        var n = processName.Trim();
        return n.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
               || n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
               || n.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
               || n.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
               || n.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
               || n.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
               || n.EndsWith(".com", StringComparison.OrdinalIgnoreCase)
               || n.EndsWith(".scr", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Windows TEMP and typical installer extract folders (Innosetup is-XXXXX.tmp, etc.).
    /// </summary>
    public static bool IsTempPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var p = path.Trim().Replace('/', '\\');

        if (InstallerTempFolderPattern().IsMatch(p))
            return true;

        // Segment checks (case-insensitive): \Windows\Temp\, \AppData\Local\Temp\, \TEMP\
        var lower = p.ToLowerInvariant();
        if (lower.Contains(@"\windows\temp\") || lower.EndsWith(@"\windows\temp", StringComparison.Ordinal))
            return true;
        if (lower.Contains(@"\appdata\local\temp\") || lower.EndsWith(@"\appdata\local\temp", StringComparison.Ordinal))
            return true;

        // Generic \TEMP\ path segment (not "Templates")
        if (TempSegmentPattern().IsMatch(p))
            return true;

        return false;
    }

    // Inno Setup / similar: ...\is-R65HS.tmp\...
    [GeneratedRegex(@"\\is-[^\\]+\.tmp(?:\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstallerTempFolderPattern();

    // Path segment named TEMP (exact), e.g. C:\TEMP\foo.exe or ...\TEMP\...
    [GeneratedRegex(@"(?:^|\\)TEMP(?:\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TempSegmentPattern();
}
