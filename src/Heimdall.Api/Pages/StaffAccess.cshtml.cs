using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Heimdall.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

/// <summary>
/// Staff RDP pool: public-team machines, booking ≤24h, Connect .rdp.
/// Live metrics remain on <see cref="StaffModel"/> per Remote Access Group.
/// </summary>
public class StaffAccessModel(
    RemoteAccessGroupService groups,
    StaffAccessGuard guard,
    WindowsStaffIdentityService identity,
    MachineBookingService bookings) : PageModel
{
    public string? SignedInEmail { get; private set; }
    public List<RemoteAccessGroup> MyGroups { get; private set; } = [];
    public string? WindowsUser { get; private set; }
    public bool DevBypassActive { get; private set; }
    public bool WindowsAuthRequired { get; private set; }
    public bool IsAdmin { get; private set; }
    public IReadOnlyList<MachineBookingService.PoolMachineRow> PoolRows { get; private set; } = [];

    [BindProperty]
    public string Email { get; set; } = "";

    [BindProperty]
    public int BookMachineId { get; set; }

    [BindProperty]
    public string BookDuration { get; set; } = "2h";

    [BindProperty]
    public string? BookNotes { get; set; }

    [BindProperty]
    public int CancelBookingId { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        DevBypassActive = guard.IsDevBypassActive;
        WindowsAuthRequired = guard.IsWindowsAuthRequired;

        if (!await guard.EnsureWindowsAuthAsync(HttpContext))
            return new EmptyResult();

        WindowsUser = identity.GetWindowsPrincipalName(HttpContext);
        IsAdmin = guard.IsConfiguredAdmin(HttpContext);
        SignedInEmail = guard.TryGetVerifiedEmail(HttpContext);

        if (SignedInEmail is not null)
        {
            MyGroups = await groups.FindGroupsForEmailAsync(SignedInEmail, ct);
            await LoadPoolAsync(SignedInEmail, ct);
            return Page();
        }

        var autoEmail = await guard.TryResolveEmailFromWindowsAsync(HttpContext, groups, ct);
        if (autoEmail is null && identity.GetWindowsPrincipalName(HttpContext) is not null)
        {
            // Authenticated Windows user with no RAG: still allow Staff RDP pool via candidate email.
            autoEmail = identity.GetCandidateEmails(HttpContext).FirstOrDefault();
            if (autoEmail is not null)
                autoEmail = WindowsStaffIdentityService.NormalizeEmail(autoEmail);
        }
        else if (autoEmail is null && IsAdmin)
        {
            autoEmail = identity.GetCandidateEmails(HttpContext).FirstOrDefault()
                ?? (DevBypassActive ? StaffAuthService.TryGetEmail(HttpContext) : null);
            if (autoEmail is not null)
                autoEmail = WindowsStaffIdentityService.NormalizeEmail(autoEmail);
        }

        if (autoEmail is not null)
        {
            StaffAuthService.SignIn(HttpContext, autoEmail);
            TempData["Message"] = $"Signed in as {autoEmail}.";
            return RedirectToPage();
        }

        if (WindowsUser is not null && guard.IsWindowsAuthRequired)
            Email = identity.GetCandidateEmails(HttpContext).FirstOrDefault() ?? "";

        return Page();
    }

    public async Task<IActionResult> OnPostSignInAsync(CancellationToken ct)
    {
        DevBypassActive = guard.IsDevBypassActive;
        WindowsAuthRequired = guard.IsWindowsAuthRequired;

        if (!await guard.EnsureWindowsAuthAsync(HttpContext))
            return new EmptyResult();

        WindowsUser = identity.GetWindowsPrincipalName(HttpContext);

        if (string.IsNullOrWhiteSpace(Email) || !Email.Contains('@'))
        {
            TempData["Error"] = "Enter a valid email address.";
            return RedirectToPage();
        }

        if (!guard.CanSignInWithEmail(HttpContext, Email))
        {
            var who = WindowsStaffIdentityService.FormatDisplayName(WindowsUser);
            TempData["Error"] =
                $"That email does not match your Windows login ({who}). You can only sign in as yourself — ask an admin to register the email that matches your account.";
            return RedirectToPage();
        }

        StaffAuthService.SignIn(HttpContext, Email);
        TempData["Message"] = $"Signed in as {Email.Trim()}.";
        return RedirectToPage();
    }

    public IActionResult OnPostSignOut()
    {
        StaffAuthService.SignOut(HttpContext);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostBookAsync(CancellationToken ct)
    {
        if (!await guard.EnsureWindowsAuthAsync(HttpContext))
            return new EmptyResult();

        var email = guard.TryGetVerifiedEmail(HttpContext);
        if (email is null)
            return RedirectToPage();

        if (!TryResolveDuration(BookDuration, out var start, out var end, out var err))
        {
            TempData["Error"] = err;
            return RedirectToPage();
        }

        var result = await bookings.TryCreateAsync(BookMachineId, email, start, end, BookNotes, ct);
        TempData[result.Ok ? (result.ActiveSessionWarning ? "Warning" : "Message") : "Error"] = result.Message;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCancelBookingAsync(CancellationToken ct)
    {
        if (!await guard.EnsureWindowsAuthAsync(HttpContext))
            return new EmptyResult();

        var email = guard.TryGetVerifiedEmail(HttpContext);
        if (email is null)
            return RedirectToPage();

        var result = await bookings.TryCancelAsync(CancelBookingId, email, guard.IsConfiguredAdmin(HttpContext), ct);
        TempData[result.Ok ? "Message" : "Error"] = result.Message;
        return RedirectToPage();
    }

    public IActionResult OnGetConnectRdp(string hostname)
    {
        var file = RdpConnectFile.TryCreate(hostname);
        return file is null ? BadRequest() : file;
    }

    private async Task LoadPoolAsync(string email, CancellationToken ct)
    {
        IReadOnlyCollection<string>? ragFilter = null;
        var adminFull = IsAdmin;

        if (!adminFull)
        {
            var myGroups = await groups.FindGroupsForEmailAsync(email, ct);
            if (myGroups.Count > 0)
            {
                var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var g in myGroups)
                {
                    foreach (var h in await groups.GroupHostnamesAsync(g.Id, ct))
                        hosts.Add(h);
                }

                // Optional RAG intersect: only when user has RAG membership.
                ragFilter = hosts;
            }
            // No RAG → all public-team machines (authenticated staff).
        }

        PoolRows = await bookings.ListPoolAsync(email, ragFilter, adminFull, ct);
    }

    private static bool TryResolveDuration(
        string? key,
        out DateTimeOffset startUtc,
        out DateTimeOffset endUtc,
        out string error)
    {
        startUtc = DateTimeOffset.UtcNow;
        endUtc = startUtc;
        error = "";
        var k = (key ?? "2h").Trim().ToLowerInvariant();

        TimeSpan? span = k switch
        {
            "1h" => TimeSpan.FromHours(1),
            "2h" => TimeSpan.FromHours(2),
            "4h" => TimeSpan.FromHours(4),
            "8h" => TimeSpan.FromHours(8),
            "24h" or "1d" => TimeSpan.FromHours(24),
            "eod" => null,
            _ => TimeSpan.FromHours(2)
        };

        if (k == "eod")
        {
            var local = DateTimeOffset.Now;
            var endLocal = new DateTimeOffset(local.Year, local.Month, local.Day, 23, 59, 0, local.Offset);
            if (endLocal <= local)
                endLocal = endLocal.AddDays(1);
            endUtc = endLocal.ToUniversalTime();
            if (endUtc - startUtc > MachineBookingService.MaxDuration)
                endUtc = startUtc + MachineBookingService.MaxDuration;
            return true;
        }

        if (span is null || span > MachineBookingService.MaxDuration)
        {
            error = "Choose a duration up to 24 hours.";
            return false;
        }

        endUtc = startUtc + span.Value;
        return true;
    }

    public static string ActiveUserBadgeClass(bool hasActive, MachineBooking? booking, bool bookingIsMine)
    {
        if (hasActive) return "badge-active";
        if (booking is not null && !bookingIsMine) return "badge-warn";
        return "badge-ended";
    }

    public static string FormatBooking(MachineBooking? b)
    {
        if (b is null) return "—";
        return $"{b.BookedByEmail} · until {b.EndUtc.ToLocalTime():g}";
    }
}
