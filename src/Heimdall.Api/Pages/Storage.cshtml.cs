using System.Text.Json;
using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Heimdall.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

/// <summary>
/// Fleet → Storage: per-machine volume fill bars, last deep scan, hotspots; queue weekly/manual fleet scans.
/// </summary>
public class StorageModel(HeimdallDbContext db, StorageScanService storageScans) : PageModel
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public IReadOnlyList<StorageCard> Cards { get; private set; } = [];
    public StorageScanOptions ScanOptions { get; private set; } = new();
    public DateTimeOffset? LastWeeklyRunUtc { get; private set; }
    public int OnlineCount { get; private set; }
    public int WithVolumesCount { get; private set; }
    public int PendingScanCount { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "full";

    [BindProperty]
    public string? Hostname { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!OpsPartial.IsPartial(Request))
            return OpsPartial.RedirectToOpsTab(Request, "storage");

        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostRunAllAsync(CancellationToken ct)
    {
        var (_, _, _, message) = await storageScans.QueueFleetScansAsync(hostnames: null, ct, reason: "manual-all");
        TempData["Message"] = message;
        return RedirectToStorage();
    }

    public async Task<IActionResult> OnPostRunOneAsync(CancellationToken ct)
    {
        var host = Hostname?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            TempData["Error"] = "Missing hostname.";
            return RedirectToStorage();
        }

        var (_, _, _, message) = await storageScans.QueueFleetScansAsync([host], ct, reason: "manual-one");
        TempData["Message"] = message;
        return RedirectToStorage();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        ScanOptions = storageScans.GetOptions();
        LastWeeklyRunUtc = await storageScans.GetLastWeeklyRunUtcAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var onlineCutoff = now.AddMinutes(-5);

        var machines = await db.Machines.AsNoTracking()
            .OrderBy(m => m.Hostname)
            .Select(m => new
            {
                m.Hostname,
                m.FriendlyName,
                m.LastSeenUtc,
                m.AgentVersion,
                m.DiskVolumesJson,
                m.DiskVolumesUtc,
                m.DiskUsageScanJson,
                m.DiskUsageScanUtc,
                m.PendingDiskUsageScanJson
            })
            .ToListAsync(ct);

        var q = (Q ?? "").Trim();
        var cards = new List<StorageCard>(machines.Count);
        foreach (var m in machines)
        {
            if (q.Length > 0
                && m.Hostname.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                && (m.FriendlyName is null || m.FriendlyName.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0))
            {
                continue;
            }

            var volumes = DeserializeVolumes(m.DiskVolumesJson);
            var scan = DeserializeScan(m.DiskUsageScanJson);
            var pending = !string.IsNullOrWhiteSpace(m.PendingDiskUsageScanJson);
            var online = m.LastSeenUtc >= onlineCutoff;
            var maxUsed = volumes.Count == 0 ? -1.0 : volumes.Max(v => v.UsedPct);
            var supports = VersionCompare.TryGetSimpleVersion(m.AgentVersion) is int n
                           && n >= ScanOptions.MinAgentVersion;

            cards.Add(new StorageCard(
                m.Hostname,
                m.FriendlyName,
                online,
                m.LastSeenUtc,
                m.AgentVersion,
                supports,
                volumes,
                m.DiskVolumesUtc,
                scan,
                m.DiskUsageScanUtc,
                pending,
                maxUsed,
                ExtractHotspotBadges(scan)));
        }

        OnlineCount = cards.Count(c => c.Online);
        WithVolumesCount = cards.Count(c => c.Volumes.Count > 0);
        PendingScanCount = cards.Count(c => c.PendingScan);

        Sort = (Sort ?? "full").Trim().ToLowerInvariant();
        Cards = Sort switch
        {
            "name" => cards.OrderBy(c => c.Hostname, StringComparer.OrdinalIgnoreCase).ToList(),
            "scan" => cards
                .OrderByDescending(c => c.ScanUtc ?? DateTimeOffset.MinValue)
                .ThenBy(c => c.Hostname, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => cards
                .OrderByDescending(c => c.MaxUsedPct)
                .ThenBy(c => c.Hostname, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private IActionResult RedirectToStorage()
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(Q))
            qs.Add($"q={Uri.EscapeDataString(Q)}");
        if (!string.IsNullOrWhiteSpace(Sort) && !string.Equals(Sort, "full", StringComparison.OrdinalIgnoreCase))
            qs.Add($"sort={Uri.EscapeDataString(Sort)}");
        var suffix = qs.Count == 0 ? "" : "&" + string.Join("&", qs);
        return Redirect($"/Fleet?tab=storage{suffix}");
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "—";
        double v = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }

        return $"{v:0.##} {units[i]}";
    }

    public static string FormatLocalTimestamp(DateTimeOffset utc) =>
        utc.ToLocalTime().ToString("dd MMM yyyy HH:mm");

    public static string BarToneClass(double usedPct) =>
        usedPct >= 85 ? "hd-disk-bar-crit" : usedPct >= 70 ? "hd-disk-bar-warn" : "hd-disk-bar-ok";

    public static string HotspotLabel(DiskUsageHotspotDto h) => h.Key switch
    {
        DiskUsageHotspotKeys.CcmCache => "ccmcache",
        DiskUsageHotspotKeys.Projects => "Projects",
        DiskUsageHotspotKeys.Users => "Users",
        DiskUsageHotspotKeys.UserProfile => ShortProfileName(h.Path),
        _ => h.Key
    };

    private static string ShortProfileName(string path)
    {
        try
        {
            return Path.GetFileName(path.TrimEnd('\\', '/'));
        }
        catch
        {
            return path;
        }
    }

    private static IReadOnlyList<HotspotBadge> ExtractHotspotBadges(DiskUsageScanResultDto? scan)
    {
        if (scan?.Hotspots is null || scan.Hotspots.Count == 0)
            return [];

        const long minBadgeBytes = 512L * 1024 * 1024; // 512 MB
        var badges = new List<HotspotBadge>();

        foreach (var h in scan.Hotspots)
        {
            if (h.Key is DiskUsageHotspotKeys.UserProfile)
                continue;
            if (!h.Exists && h.SizeBytes <= 0)
                continue;
            if (h.SizeBytes < minBadgeBytes && h.Key != DiskUsageHotspotKeys.CcmCache)
                continue;
            if (h.Key == DiskUsageHotspotKeys.CcmCache && h.SizeBytes < 100L * 1024 * 1024)
                continue;

            badges.Add(new HotspotBadge(HotspotLabel(h), h.SizeBytes, h.Key));
        }

        // Top user profile if large
        var topProfile = scan.Hotspots
            .Where(h => h.Key == DiskUsageHotspotKeys.UserProfile && h.SizeBytes >= minBadgeBytes)
            .OrderByDescending(h => h.SizeBytes)
            .FirstOrDefault();
        if (topProfile is not null)
            badges.Add(new HotspotBadge(HotspotLabel(topProfile), topProfile.SizeBytes, topProfile.Key));

        return badges;
    }

    private static IReadOnlyList<DiskVolumeDto> DeserializeVolumes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<DiskVolumeDto>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static DiskUsageScanResultDto? DeserializeScan(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<DiskUsageScanResultDto>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public sealed record StorageCard(
        string Hostname,
        string? FriendlyName,
        bool Online,
        DateTimeOffset LastSeenUtc,
        string? AgentVersion,
        bool SupportsScan,
        IReadOnlyList<DiskVolumeDto> Volumes,
        DateTimeOffset? VolumesUtc,
        DiskUsageScanResultDto? Scan,
        DateTimeOffset? ScanUtc,
        bool PendingScan,
        double MaxUsedPct,
        IReadOnlyList<HotspotBadge> HotspotBadges);

    public sealed record HotspotBadge(string Label, long SizeBytes, string Key);
}
