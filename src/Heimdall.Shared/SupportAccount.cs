namespace Heimdall.Shared;

/// <summary>
/// Detects ops / support logons: username starts with "ops." (case-insensitive),
/// or domain is OPS / starts with OPS\, or SAM-style "OPS\…" already split.
/// </summary>
public static class SupportAccount
{
    public static bool IsOpsSupport(string? username, string? domain = null)
    {
        if (!string.IsNullOrWhiteSpace(domain))
        {
            var d = domain.Trim();
            if (d.Equals("OPS", StringComparison.OrdinalIgnoreCase) ||
                d.StartsWith("OPS\\", StringComparison.OrdinalIgnoreCase) ||
                d.StartsWith("OPS.", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (string.IsNullOrWhiteSpace(username))
            return false;

        var u = username.Trim();

        // DOMAIN\user form in a single field
        var slash = u.IndexOf('\\');
        if (slash > 0)
        {
            var dom = u[..slash];
            var user = u[(slash + 1)..];
            if (dom.Equals("OPS", StringComparison.OrdinalIgnoreCase))
                return true;
            return user.StartsWith("ops.", StringComparison.OrdinalIgnoreCase);
        }

        // user@domain
        var at = u.IndexOf('@');
        if (at > 0)
        {
            var user = u[..at];
            var dom = u[(at + 1)..];
            if (dom.StartsWith("OPS", StringComparison.OrdinalIgnoreCase))
                return true;
            return user.StartsWith("ops.", StringComparison.OrdinalIgnoreCase);
        }

        return u.StartsWith("ops.", StringComparison.OrdinalIgnoreCase);
    }
}
