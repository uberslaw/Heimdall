using System.Text.RegularExpressions;

namespace Heimdall.Shared;

/// <summary>
/// Normalizes executable paths for catalog identity so volatile Windows install locations
/// (DriverStore hashes, WindowsApps package versions) do not create endless duplicate entries.
/// Raw paths are still stored on each catalog entry; this is used only for deduplication and merge heuristics.
/// </summary>
public static partial class CatalogPathNormalizer
{
    /// <summary>
    /// Returns a lowercase, slash-normalized path with volatile segments collapsed.
    /// Empty input returns empty string (unknown path).
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        var p = path.Trim().Replace('/', '\\');

        // DriverStore: C:\Windows\System32\DriverStore\FileRepository\<hash>\… → {hash}
        p = DriverStoreHashPattern().Replace(p, @"\DriverStore\FileRepository\{hash}\");

        // WindowsApps: …\PackageName_1.2.3.0_x64__8wekyb3d8bbwe\… → PackageName{version}_x64__publisher
        p = WindowsAppsVersionPattern().Replace(p, m =>
            $@"\WindowsApps\{m.Groups[1].Value}{{version}}{m.Groups[2].Value}");

        // Versioned install folders: …\App\4.2.1.0\file.exe → …\App\{version}\file.exe
        p = VersionFolderPattern().Replace(p, @"\{version}\");

        return p.ToLowerInvariant();
    }

    /// <summary>True when normalization changes the path (volatile segment detected).</summary>
    public static bool IsVolatilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var raw = path.Trim().Replace('/', '\\').ToLowerInvariant();
        var normalized = Normalize(path);
        return !string.Equals(raw, normalized, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\\DriverStore\\FileRepository\\[^\\]+\\", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DriverStoreHashPattern();

    // Package folder: Name_Version_Arch__Publisher (version starts with a digit)
    [GeneratedRegex(@"\\WindowsApps\\([^\\]+?)_[0-9][^\\]*?(_(?:x64|x86|arm|arm64|neutral)__[^\\]+\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsAppsVersionPattern();

    // Standalone version directory segments (e.g. \18.2.1\ or \4.0.30319\)
    [GeneratedRegex(@"\\(\d+(?:\.\d+){1,3}(?:\.\d+)?)\\", RegexOptions.CultureInvariant)]
    private static partial Regex VersionFolderPattern();
}
