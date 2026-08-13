namespace Heimdall.Shared;

/// <summary>
/// Consistent Temp / deposit folder names for client packs.
/// Pattern: <c>Heimdall-Client-v{version}</c> (push) or
/// <c>Heimdall-Client-v{version}-{yyyyMMdd-HHmmss}</c> (API deposit).
/// Build output stays <c>dist\Heimdall-Client</c>.
/// </summary>
public static class ClientPackFolderNames
{
    public const string FolderPrefix = "Heimdall-Client";

    /// <summary>Example pattern shown in Help / UI for API deposits.</summary>
    public const string DepositPatternExample = @"C:\Temp\Heimdall-Client-v{version}-{yyyyMMdd-HHmmss}";

    /// <summary>Example pattern shown in Help / UI for Launch Control SMB push.</summary>
    public const string PushPatternExample = @"C:\Temp\Heimdall-Client-v{version}";

    /// <summary>Sanitize productVersion for a Windows directory segment.</summary>
    public static string SanitizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return "unknown";

        var chars = version.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'
                || char.IsControl(c))
                chars[i] = '-';
        }

        var s = new string(chars).Trim().TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(s) ? "unknown" : s;
    }

    /// <summary>Launch Control / fixed drop: <c>Heimdall-Client-v{version}</c>.</summary>
    public static string BuildPushFolderName(string? version) =>
        $"{FolderPrefix}-v{SanitizeVersion(version)}";

    /// <summary>Agent DepositClientPack: <c>Heimdall-Client-v{version}-{yyyyMMdd-HHmmss}</c>.</summary>
    public static string BuildDepositFolderName(string? version, DateTime? localStamp = null)
    {
        var stamp = (localStamp ?? DateTime.Now).ToString("yyyyMMdd-HHmmss");
        return $"{FolderPrefix}-v{SanitizeVersion(version)}-{stamp}";
    }

    /// <summary>
    /// True for legacy <c>Heimdall-Client</c> and versioned
    /// <c>Heimdall-Client-*</c> deposit/push folders (not unrelated Temp names).
    /// </summary>
    public static bool IsTempClientPackFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        if (string.Equals(name, FolderPrefix, StringComparison.OrdinalIgnoreCase))
            return true;
        return name.StartsWith(FolderPrefix + "-", StringComparison.OrdinalIgnoreCase);
    }
}
