using System.Text.RegularExpressions;

namespace Heimdall.Shared;

/// <summary>
/// Derives a stable “program” identity from an executable path under Program Files.
/// Default: first folder under Program Files / Program Files (x86).
/// Mega-vendors (Autodesk, Adobe, …): next product folder, with year/version stripped when sensible.
/// Does not collapse all of Program Files into one program.
/// </summary>
public static partial class ProgramInstallRoot
{
    /// <summary>
    /// Vendor / container folders where the first segment is too coarse —
    /// use the next product directory as the program grain.
    /// </summary>
    private static readonly HashSet<string> MegaVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        "Autodesk",
        "Adobe",
        "Microsoft",
        // Microsoft Office stays first-folder (…\Microsoft Office\root\Office16\… would otherwise become "root").
        "Dell",
        "Alienware",
        "HP",
        "Cisco",
        "Common Files",
        "WindowsApps",
        "AMD",
        "Intel",
        "NVIDIA Corporation",
        "Epic Games",
        "Google",
        "Mozilla",
        "JetBrains",
        "Amazon",
        "Oracle",
        "IBM",
        "SAP",
        "VMware",
        "TechSmith",
        "Razer",
        "Logitech",
        "Chaos",
        "Bentley",
        "Trimble",
        "ESRI",
        "Esri",
        "McAfee",
        "Norton",
        "Symantec",
        "Trend Micro",
        "Sophos",
        "Crowdstrike",
        "CrowdStrike",
        "dotnet",
        "IIS",
        "Windows Kits",
        "MSBuild",
        "Reference Assemblies",
        "Microsoft Visual Studio",
        "Microsoft SQL Server",
    };

    /// <summary>Second-level folder names that are install layout, not a product identity.</summary>
    private static readonly HashSet<string> NonProductFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "root", "bin", "bin64", "x64", "x86", "amd64", "arm64", "i386", "Wow6432Node",
        "Common", "CommonFiles", "Shared", "Support", "Setup", "Installer", "Install",
        "Application", "Applications", "App", "Apps", "Program", "Programs",
        "Current", "Latest", "Client", "Server", "Service", "Services",
    };

    public sealed record Result(
        /// <summary>Stable lowercase key for grouping (e.g. <c>pf:autodesk/revit</c>).</summary>
        string Key,
        /// <summary>Human label (e.g. <c>Autodesk / Revit</c>).</summary>
        string DisplayName,
        /// <summary>Install root path used for the program (no trailing exe).</summary>
        string RootPath,
        /// <summary>True when mega-vendor second-level grain was used.</summary>
        bool UsedProductFolder);

    /// <summary>
    /// Extracts program identity from a full executable path.
    /// Returns null when the path is not under Program Files / Program Files (x86),
    /// or when there is no app folder (bare file directly under Program Files).
    /// </summary>
    public static Result? TryExtract(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return null;

        var path = executablePath.Trim().Replace('/', '\\');
        if (!TrySplitProgramFiles(path, out var pfLabel, out var relative))
            return null;

        var segments = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            return null;

        // Last segment is usually the .exe — drop it when present.
        var dirs = segments[^1].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? segments[..^1]
            : segments;
        if (dirs.Length == 0)
            return null;

        var first = dirs[0];
        if (string.IsNullOrWhiteSpace(first))
            return null;

        string display;
        string rootPath;
        bool usedProduct;
        string keyTail;

        if (MegaVendors.Contains(first) && dirs.Length >= 2 && !NonProductFolders.Contains(dirs[1]))
        {
            var productRaw = dirs[1];
            var product = StabilizeProductName(first, productRaw);
            if (string.IsNullOrWhiteSpace(product) || NonProductFolders.Contains(product))
            {
                // Fall back to first-folder program identity.
                usedProduct = false;
                var stableFirst = StabilizeProductName(null, first);
                display = string.IsNullOrWhiteSpace(stableFirst) ? first : stableFirst;
                rootPath = $"{pfLabel}\\{first}";
                keyTail = NormalizeKeyPart(display);
            }
            else
            {
                usedProduct = true;
                display = $"{first} / {product}";
                rootPath = $"{pfLabel}\\{first}\\{productRaw}";
                keyTail = $"{NormalizeKeyPart(first)}/{NormalizeKeyPart(product)}";
            }
        }
        else
        {
            usedProduct = false;
            var stableFirst = StabilizeProductName(null, first);
            display = string.IsNullOrWhiteSpace(stableFirst) ? first : stableFirst;
            rootPath = $"{pfLabel}\\{first}";
            keyTail = NormalizeKeyPart(display);
        }

        // Distinguish x86 vs 64 roots so the same vendor name on both sides stays separate when needed,
        // but share key when product names match across bitness by omitting bitness from the key —
        // keep bitness in RootPath only. Key is bitness-agnostic for cross-arch program identity.
        var key = $"pf:{keyTail}";
        return new Result(key, display, rootPath, usedProduct);
    }

    /// <summary>Convenience: program key or null.</summary>
    public static string? TryGetKey(string? executablePath) => TryExtract(executablePath)?.Key;

    /// <summary>Convenience: display name or null.</summary>
    public static string? TryGetDisplayName(string? executablePath) => TryExtract(executablePath)?.DisplayName;

    /// <summary>
    /// True when two executable paths resolve to the same program identity.
    /// Unknown / non-Program-Files paths never match (even if equal process names).
    /// </summary>
    public static bool SameProgram(string? pathA, string? pathB)
    {
        var a = TryGetKey(pathA);
        var b = TryGetKey(pathB);
        return a is not null && b is not null
               && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TrySplitProgramFiles(string path, out string pfLabel, out string relative)
    {
        pfLabel = "";
        relative = "";

        // Longer prefix first so "(x86)" wins over "Program Files\".
        ReadOnlySpan<(string Prefix, string Label)> prefixes =
        [
            (@"C:\Program Files (x86)\", @"C:\Program Files (x86)"),
            (@"C:\Program Files\", @"C:\Program Files"),
        ];

        foreach (var (prefix, label) in prefixes)
        {
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            pfLabel = label;
            relative = path[prefix.Length..];
            return relative.Length > 0;
        }

        return false;
    }

    /// <summary>
    /// Strip trailing years / dotted versions from product folder names when that yields a stable family
    /// (e.g. "Revit 2025" → "Revit", "AutoCAD 2024" → "AutoCAD").
    /// WindowsApps package folders: take name before the first <c>_&lt;digit&gt;</c> version token.
    /// </summary>
    private static string StabilizeProductName(string? megaVendor, string productRaw)
    {
        var p = productRaw.Trim();
        if (p.Length == 0)
            return p;

        if (string.Equals(megaVendor, "WindowsApps", StringComparison.OrdinalIgnoreCase))
        {
            // PackageName_1.2.3.0_x64__publisher → PackageName
            var m = WindowsAppsPackageNamePattern().Match(p);
            if (m.Success)
                return m.Groups[1].Value;
        }

        // Trailing calendar year: "Revit 2025", "3ds Max 2022"
        p = TrailingYearPattern().Replace(p, "").Trim();

        // Trailing dotted version folder residue: "Something 1.2.3"
        p = TrailingDottedVersionPattern().Replace(p, "").Trim();

        return string.IsNullOrWhiteSpace(p) ? productRaw.Trim() : p;
    }

    private static string NormalizeKeyPart(string value)
    {
        var s = value.Trim().ToLowerInvariant();
        // Collapse whitespace / punctuation to single hyphens for stable keys.
        s = NonKeyCharPattern().Replace(s, "-");
        s = MultiHyphenPattern().Replace(s, "-").Trim('-');
        return s;
    }

    [GeneratedRegex(@"^(.*?)_[0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsAppsPackageNamePattern();

    [GeneratedRegex(@"\s+((?:19|20)\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingYearPattern();

    [GeneratedRegex(@"\s+\d+(?:\.\d+){1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingDottedVersionPattern();

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonKeyCharPattern();

    [GeneratedRegex(@"-+", RegexOptions.CultureInvariant)]
    private static partial Regex MultiHyphenPattern();
}
