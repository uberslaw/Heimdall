namespace Heimdall.Api.Services;

/// <summary>
/// Staff Access identity settings. When <see cref="RequireWindowsAuth"/> is true, sign-in and staff API
/// calls must match the browser's Windows login to a registered group email (see WindowsStaffIdentityService).
/// </summary>
public sealed class StaffAccessOptions
{
    /// <summary>When true, Staff Access uses Negotiate (NTLM/Kerberos) and ties sessions to Windows identity.</summary>
    public bool RequireWindowsAuth { get; set; } = true;

    /// <summary>
    /// Domain suffix(es) used to derive email from DOMAIN\sAMAccountName, e.g. "contoso.com" → user@contoso.com.
    /// </summary>
    public string[] EmailDomainSuffixes { get; set; } = [];

    /// <summary>
    /// When true and the host environment is Development, Staff Access skips Windows auth (typed email only)
    /// and shows a dev warning. Never enable in production.
    /// </summary>
    public bool AllowDevBypass { get; set; }

    /// <summary>
    /// Staff Access admin preview: Windows logins (or dev-bypass cookie emails) matching one of these addresses
    /// may open any group's Staff page via the Preview action on Admin → Remote Access Groups.
    /// </summary>
    public string[] AdminEmails { get; set; } = [];

    /// <summary>How long the admin preview cookie lasts after clicking Preview (minutes).</summary>
    public int AdminPreviewMinutes { get; set; } = 30;
}
