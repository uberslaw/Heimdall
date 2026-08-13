using Heimdall.Api.Services;
using Heimdall.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

/// <summary>
/// Public remote workstation pool — no StaffAccessGuard. Browse, Connect (heimdall-rdp), reserve ≤24h.
/// </summary>
public class RemoteWorkstationsModel(
    MachineBookingService bookings,
    AdvertisedSoftwareService advertised,
    WindowsStaffIdentityService identity,
    StaffAccessGuard guard) : PageModel
{
    public IReadOnlyList<MachineBookingService.PoolMachineRow> PoolRows { get; private set; } = [];
    public IReadOnlyDictionary<int, IReadOnlyList<AdvertisedSoftwareService.AdvertisedApp>> SoftwareByMachine { get; private set; } =
        new Dictionary<int, IReadOnlyList<AdvertisedSoftwareService.AdvertisedApp>>();
    public IReadOnlyList<string> AllSoftwareLabels { get; private set; } = [];
    public IReadOnlyList<string> TeamNames { get; private set; } = [];
    public IReadOnlyList<string> AdminTitleCandidates { get; private set; } = [];
    public IReadOnlySet<string> SelectedDisplayTitles { get; private set; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool DisplayTitlesConfigured { get; private set; }

    public string? PrefillEmail { get; private set; }
    public string? PrefillName { get; private set; }
    public bool IsAdmin { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Team { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Software { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty]
    public int BookMachineId { get; set; }

    [BindProperty]
    public string BookName { get; set; } = "";

    [BindProperty]
    public string BookEmail { get; set; } = "";

    [BindProperty]
    public string BookStartLocal { get; set; } = "";

    [BindProperty]
    public string BookDuration { get; set; } = "2h";

    [BindProperty]
    public string? BookNotes { get; set; }

    [BindProperty]
    public int CancelBookingId { get; set; }

    [BindProperty]
    public string CancelEmail { get; set; } = "";

    [BindProperty]
    public string[] DisplayTitles { get; set; } = [];

    [BindProperty]
    public string? DisplayTitleAdd { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
    }

    public async Task<IActionResult> OnPostBookAsync(CancellationToken ct)
    {
        if (!TryResolveBookingWindow(BookStartLocal, BookDuration, out var startUtc, out var endUtc, out var err))
        {
            TempData["Error"] = err;
            return RedirectToPage(FilterRoute());
        }

        var result = await bookings.TryCreateAsync(
            BookMachineId,
            BookEmail,
            startUtc,
            endUtc,
            BookNotes,
            ct,
            BookName);
        TempData[result.Ok ? (result.ActiveSessionWarning ? "Warning" : "Message") : "Error"] = result.Message;
        return RedirectToPage(FilterRoute());
    }

    public async Task<IActionResult> OnPostCancelBookingAsync(CancellationToken ct)
    {
        IsAdmin = guard.IsConfiguredAdmin(HttpContext);
        var email = CancelEmail;
        if (string.IsNullOrWhiteSpace(email))
            email = guard.TryGetVerifiedEmail(HttpContext) ?? "";

        var result = await bookings.TryCancelAsync(CancelBookingId, email, IsAdmin, ct);
        TempData[result.Ok ? "Message" : "Error"] = result.Message;
        return RedirectToPage(FilterRoute());
    }

    public async Task<IActionResult> OnPostSaveDisplayTitlesAsync(CancellationToken ct)
    {
        if (!guard.IsConfiguredAdmin(HttpContext))
        {
            TempData["Error"] = "Admin only.";
            return RedirectToPage(FilterRoute());
        }

        var titles = new List<string>(DisplayTitles ?? []);
        if (!string.IsNullOrWhiteSpace(DisplayTitleAdd))
            titles.Add(DisplayTitleAdd);
        await advertised.SaveDisplayedTitlesAsync(titles, ct);
        var count = titles
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        TempData["Message"] = count == 0
            ? "No software titles will show on the public pool until you select some."
            : $"Showing {count} software title(s) on the public pool.";
        return RedirectToPage(FilterRoute());
    }

    public async Task<IActionResult> OnPostClearDisplayTitlesAsync(CancellationToken ct)
    {
        if (!guard.IsConfiguredAdmin(HttpContext))
        {
            TempData["Error"] = "Admin only.";
            return RedirectToPage(FilterRoute());
        }

        await advertised.ClearDisplayedTitlesAsync(ct);
        TempData["Message"] = "Display titles cleared — the pool shows every advertised app again.";
        return RedirectToPage(FilterRoute());
    }

    public IActionResult OnGetConfigureRdp()
    {
        Response.Headers.CacheControl = "no-store";
        var file = RdpProtocolHandler.TryCreateUserConfigureLauncher();
        return file is null
            ? StatusCode(StatusCodes.Status500InternalServerError, "Configure launcher is not packaged on this server.")
            : file;
    }

    public Task<IActionResult> OnGetConnectRdpAsync(string hostname, CancellationToken ct) =>
        ConnectRdpAsync(hostname, settings: false, ct);

    public Task<IActionResult> OnGetConnectRdpSettingsAsync(string hostname, CancellationToken ct) =>
        ConnectRdpAsync(hostname, settings: true, ct);

    private async Task<IActionResult> ConnectRdpAsync(string hostname, bool settings, CancellationToken ct)
    {
        var resolved = await bookings.TryResolvePublicConnectTargetAsync(hostname, ct);
        if (!resolved.Ok || string.IsNullOrWhiteSpace(resolved.Target))
            return BadRequest(resolved.Error ?? "Not allowed.");

        var pool = await bookings.ListPoolAsync(null, null, adminFullPool: true, ct);
        var row = pool.FirstOrDefault(r =>
            string.Equals(r.Hostname, hostname, StringComparison.OrdinalIgnoreCase)
            || string.Equals(r.LastIp, hostname, StringComparison.OrdinalIgnoreCase)
            || string.Equals(r.ConnectTarget, resolved.Target, StringComparison.OrdinalIgnoreCase));
        if (row is null)
            return BadRequest("Machine not in pool.");
        if (row.ConnectBlocked != MachineBookingService.ConnectBlockReason.None)
        {
            return BadRequest(row.ConnectBlocked == MachineBookingService.ConnectBlockReason.ActiveSession
                ? "Connect blocked: machine has an Active session."
                : "Connect blocked: machine is reserved right now.");
        }

        var file = settings
            ? RdpConnectFile.TryCreateSettingsLauncher(resolved.Target)
            : RdpConnectFile.TryCreate(resolved.Target);
        return file is null ? BadRequest() : file;
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        IsAdmin = guard.IsConfiguredAdmin(HttpContext);
        PrefillEmail = guard.TryGetVerifiedEmail(HttpContext)
            ?? identity.GetCandidateEmails(HttpContext).FirstOrDefault();
        PrefillName = WindowsStaffIdentityService.FormatDisplayName(identity.GetWindowsPrincipalName(HttpContext));

        var emailNorm = PrefillEmail is null
            ? null
            : WindowsStaffIdentityService.NormalizeEmail(PrefillEmail);

        var rows = await bookings.ListPoolAsync(emailNorm, ragHostnameFilter: null, adminFullPool: true, ct);
        var hosts = rows.Select(r => (r.MachineId, r.Hostname)).ToList();
        var rawByMachine = await advertised.ListByMachineAsync(hosts, ct);
        var displayed = await advertised.GetDisplayedTitlesAsync(ct);
        DisplayTitlesConfigured = displayed is not null;

        var catalogNames = rawByMachine.Values
            .SelectMany(apps => apps)
            .Select(a => a.DisplayName)
            .Where(n => !string.IsNullOrWhiteSpace(n));
        AdminTitleCandidates = catalogNames
            .Concat(displayed ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        SelectedDisplayTitles = displayed is null
            ? AdminTitleCandidates.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : displayed;

        SoftwareByMachine = rawByMachine.ToDictionary(
            kv => kv.Key,
            kv => AdvertisedSoftwareService.ProjectDisplayed(kv.Value, displayed));
        TeamNames = rows
            .Select(r => r.TeamName)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t)
            .Cast<string>()
            .ToList();

        IEnumerable<MachineBookingService.PoolMachineRow> filtered = rows;

        if (!string.IsNullOrWhiteSpace(Status))
        {
            filtered = Status.Trim().ToLowerInvariant() switch
            {
                "available" => filtered.Where(r =>
                    r.ConnectBlocked == MachineBookingService.ConnectBlockReason.None
                    && r.SessionState != SessionState.Disconnected),
                "inuse" or "active" => filtered.Where(r => r.HasActiveSession),
                "disconnected" => filtered.Where(r =>
                    !r.HasActiveSession && r.SessionState == SessionState.Disconnected),
                "booked" => filtered.Where(r =>
                    r.ConnectBlocked == MachineBookingService.ConnectBlockReason.BookedNow),
                _ => filtered
            };
        }

        if (!string.IsNullOrWhiteSpace(Team))
        {
            var t = Team.Trim();
            filtered = filtered.Where(r =>
                string.Equals(r.TeamName, t, StringComparison.OrdinalIgnoreCase));
        }

        var afterStatusTeam = filtered.ToList();
        AllSoftwareLabels = afterStatusTeam
            .SelectMany(r => SoftwareByMachine.TryGetValue(r.MachineId, out var apps) ? apps : [])
            .Select(a => a.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        filtered = afterStatusTeam;
        if (!string.IsNullOrWhiteSpace(Software))
        {
            var s = Software.Trim();
            filtered = filtered.Where(r =>
                SoftwareByMachine.TryGetValue(r.MachineId, out var apps)
                && apps.Any(a => string.Equals(a.DisplayName, s, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(Q))
        {
            filtered = filtered.Where(r =>
                SoftwareByMachine.TryGetValue(r.MachineId, out var apps)
                && apps.Any(a => AdvertisedSoftwareService.MatchesQuery(a, Q)));
        }

        PoolRows = filtered.ToList();
    }

    private object FilterRoute() => new
    {
        Status,
        Team,
        Software,
        Q
    };

    public static bool TryResolveBookingWindow(
        string? startLocal,
        string? durationKey,
        out DateTimeOffset startUtc,
        out DateTimeOffset endUtc,
        out string error)
    {
        startUtc = DateTimeOffset.UtcNow;
        endUtc = startUtc;
        error = "";

        if (string.IsNullOrWhiteSpace(startLocal))
        {
            startUtc = DateTimeOffset.UtcNow;
        }
        else if (!DateTimeOffset.TryParse(startLocal, out var parsedLocal))
        {
            error = "Invalid start date/time.";
            return false;
        }
        else
        {
            startUtc = parsedLocal.ToUniversalTime();
        }

        var k = (durationKey ?? "2h").Trim().ToLowerInvariant();
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
            var localStart = startUtc.ToLocalTime();
            var endLocal = new DateTimeOffset(localStart.Year, localStart.Month, localStart.Day, 23, 59, 0, localStart.Offset);
            if (endLocal <= localStart)
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

    public static string FormatDisconnected(MachineBookingService.PoolMachineRow r)
    {
        if (r.SessionState != SessionState.Disconnected)
            return "";
        if (r.DisconnectedSeconds <= 0)
            return "Disconnected";
        var ts = TimeSpan.FromSeconds(r.DisconnectedSeconds);
        if (ts.TotalHours >= 1)
            return $"Disconnected for {ts.TotalHours:0.#}h";
        if (ts.TotalMinutes >= 1)
            return $"Disconnected for {(int)ts.TotalMinutes}m";
        return $"Disconnected for {(int)ts.TotalSeconds}s";
    }

    public static string FormatActiveSince(MachineBookingService.PoolMachineRow r)
    {
        if (!r.HasActiveSession || r.SessionStartedAtUtc is null)
            return "Active";
        var ago = DateTimeOffset.UtcNow - r.SessionStartedAtUtc.Value;
        if (ago.TotalHours >= 1)
            return $"Active since {ago.TotalHours:0.#}h ago";
        if (ago.TotalMinutes >= 1)
            return $"Active since {(int)ago.TotalMinutes}m ago";
        return "Active";
    }

    public static string FormatBookingWindow(MachineBookingService.PoolBookingRow b)
    {
        var who = !string.IsNullOrWhiteSpace(b.BookedByName) ? b.BookedByName! : b.BookedByEmail;
        return $"{who} {b.StartUtc.ToLocalTime():h:mmtt} - {b.EndUtc.ToLocalTime():h:mmtt}";
    }

    public static string RowCss(MachineBookingService.PoolMachineRow r) =>
        r.ConnectBlocked is MachineBookingService.ConnectBlockReason.ActiveSession
            or MachineBookingService.ConnectBlockReason.BookedNow
            ? "hd-rwa-row-busy"
            : "hd-rwa-row-free";
}
