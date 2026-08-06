using Heimdall.Api.Data;
using Heimdall.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

public class CostModel(HeimdallDbContext db) : PageModel
{
    public IReadOnlyList<CostRow> Rows { get; private set; } = [];
    public IReadOnlyList<IdentityEventRow> IdentityHistory { get; private set; } = [];
    public int ActiveWarrantyCount { get; private set; }
    public int ExpiredWarrantyCount { get; private set; }
    public int UnknownWarrantyCount { get; private set; }
    public decimal? TotalPurchaseCost { get; private set; }
    public double TotalUserHours30d { get; private set; }
    public double TotalSupportHours30d { get; private set; }
    public int ReimagedCount { get; private set; }

    [BindProperty]
    public int? EditingMachineId { get; set; }

    [BindProperty]
    public decimal? PurchaseCost { get; set; }

    [BindProperty]
    public string? PurchaseCurrency { get; set; } = "AUD";

    [BindProperty]
    public DateOnly? WarrantyStartDate { get; set; }

    [BindProperty]
    public DateOnly? WarrantyEndDate { get; set; }

    [BindProperty]
    public string? HardwareGpu { get; set; }

    [BindProperty]
    public string? HardwareCpu { get; set; }

    [BindProperty]
    public double? HardwareRamGb { get; set; }

    [BindProperty]
    public double? HardwareDiskGb { get; set; }

    [BindProperty]
    public string? HardwareBrand { get; set; }

    [BindProperty]
    public string? HardwareModel { get; set; }

    [BindProperty]
    public string? HardwareSerialNumber { get; set; }

    [BindProperty]
    public string? BiosSerial { get; set; }

    [BindProperty]
    public string? AssetSerial { get; set; }

    [BindProperty]
    public int? PsuWatts { get; set; }

    [BindProperty]
    public decimal? SupportHourlyRate { get; set; }

    [BindProperty]
    public bool HardwareManualOverride { get; set; } = true;

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Warranty { get; set; }

    public async Task<IActionResult> OnGetAsync(int? edit)
    {
        if (!OpsPartial.IsPartial(Request))
            return OpsPartial.RedirectToOpsTab(Request, "cost");

        await LoadAsync();
        if (edit is int id)
            await LoadEditAsync(id);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (EditingMachineId is not int id)
        {
            TempData["Error"] = "No machine selected.";
            return RedirectToOpsCost();
        }

        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Id == id);
        if (machine is null)
        {
            TempData["Error"] = "Machine not found.";
            return RedirectToOpsCost();
        }

        machine.PurchaseCost = PurchaseCost;
        machine.PurchaseCurrency = string.IsNullOrWhiteSpace(PurchaseCurrency)
            ? (PurchaseCost is not null ? "AUD" : null)
            : PurchaseCurrency.Trim().ToUpperInvariant();
        machine.WarrantyStartDate = WarrantyStartDate;
        machine.WarrantyEndDate = WarrantyEndDate;
        machine.HardwareGpu = NullIfEmpty(HardwareGpu);
        machine.HardwareCpu = NullIfEmpty(HardwareCpu);
        machine.HardwareRamGb = HardwareRamGb;
        machine.HardwareDiskGb = HardwareDiskGb;
        machine.HardwareBrand = NullIfEmpty(HardwareBrand);
        machine.HardwareModel = NullIfEmpty(HardwareModel);
        machine.HardwareSerialNumber = NullIfEmpty(HardwareSerialNumber);
        machine.BiosSerial = NullIfEmpty(BiosSerial);
        machine.AssetSerial = NullIfEmpty(AssetSerial);
        machine.PsuWatts = PsuWatts;
        machine.SupportHourlyRate = SupportHourlyRate;
        // Saving from Cost UI opts into manual override so agent won't clobber
        machine.HardwareManualOverride = HardwareManualOverride;

        await db.SaveChangesAsync();
        TempData["Message"] = $"Saved cost/hardware for {machine.Hostname}.";
        return RedirectToOpsCost(id);
    }

    public async Task<IActionResult> OnPostClearOverrideAsync(int machineId)
    {
        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Id == machineId);
        if (machine is null)
        {
            TempData["Error"] = "Machine not found.";
            return RedirectToOpsCost();
        }

        machine.HardwareManualOverride = false;
        await db.SaveChangesAsync();
        TempData["Message"] = $"Cleared manual override on {machine.Hostname} — agent may fill blank hardware fields on next heartbeat.";
        return RedirectToOpsCost(machineId);
    }

    private IActionResult RedirectToOpsCost(int? edit = null)
    {
        var q = new List<string> { "tab=cost" };
        if (edit is int id) q.Add("edit=" + id);
        if (!string.IsNullOrWhiteSpace(Q)) q.Add("q=" + Uri.EscapeDataString(Q));
        if (!string.IsNullOrWhiteSpace(Warranty)) q.Add("warranty=" + Uri.EscapeDataString(Warranty));
        return new RedirectResult("/Fleet?" + string.Join("&", q));
    }

    private async Task LoadEditAsync(int id)
    {
        var m = await db.Machines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (m is null) return;

        EditingMachineId = m.Id;
        PurchaseCost = m.PurchaseCost;
        PurchaseCurrency = m.PurchaseCurrency ?? "AUD";
        WarrantyStartDate = m.WarrantyStartDate;
        WarrantyEndDate = m.WarrantyEndDate;
        HardwareGpu = m.HardwareGpu;
        HardwareCpu = m.HardwareCpu;
        HardwareRamGb = m.HardwareRamGb;
        HardwareDiskGb = m.HardwareDiskGb;
        HardwareBrand = m.HardwareBrand;
        HardwareModel = m.HardwareModel;
        HardwareSerialNumber = m.HardwareSerialNumber;
        BiosSerial = m.BiosSerial;
        AssetSerial = m.AssetSerial;
        PsuWatts = m.PsuWatts;
        SupportHourlyRate = m.SupportHourlyRate;
        // Default checked so a Cost-page save protects agent-reported blanks unless cleared
        HardwareManualOverride = true;

        IdentityHistory = (await db.MachineIdentityEvents.AsNoTracking()
                .Where(e => e.MachineId == id)
                .OrderByDescending(e => e.ObservedAtUtc)
                .Take(20)
                .ToListAsync())
            .Select(e => new IdentityEventRow(
                e.EventType,
                e.ObservedAtUtc,
                e.OldMachineGuid,
                e.NewMachineGuid,
                e.Detail))
            .ToList();
    }

    private async Task LoadAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var since = DateTimeOffset.UtcNow.AddDays(-30);

        var machines = await db.Machines.AsNoTracking().OrderBy(m => m.Hostname).ToListAsync();
        var sessions = (await db.Sessions.AsNoTracking().ToListAsync())
            .Where(s => s.StartedAtUtc >= since || s.EndedAtUtc is null || s.EndedAtUtc >= since)
            .ToList();

        var rows = new List<CostRow>();
        foreach (var m in machines)
        {
            var status = ResolveWarranty(m.WarrantyStartDate, m.WarrantyEndDate, today);
            var machineSessions = sessions.Where(s => s.MachineId == m.Id).ToList();
            long userSeconds = 0;
            long supportSeconds = 0;
            foreach (var s in machineSessions)
            {
                if (SupportAccount.IsOpsSupport(s.Username, s.Domain))
                    supportSeconds += s.ActiveSeconds;
                else
                    userSeconds += s.ActiveSeconds;
            }

            var totalActive = userSeconds + supportSeconds;
            decimal? costPerUserHour = null;
            if (m.PurchaseCost is > 0 && userSeconds > 0)
            {
                var hours = userSeconds / 3600.0m;
                if (hours > 0)
                    costPerUserHour = Math.Round(m.PurchaseCost.Value / hours, 2);
            }

            decimal? supportCostEstimate = null;
            if (m.SupportHourlyRate is > 0 && supportSeconds > 0)
                supportCostEstimate = Math.Round(m.SupportHourlyRate.Value * (supportSeconds / 3600.0m), 2);

            double? userToSupportRatio = null;
            if (supportSeconds > 0)
                userToSupportRatio = Math.Round(userSeconds / (double)supportSeconds, 2);

            rows.Add(new CostRow(
                m.Id,
                m.Hostname,
                m.Region,
                m.Office,
                m.PurchaseCost,
                m.PurchaseCurrency ?? (m.PurchaseCost is not null ? "AUD" : null),
                m.WarrantyStartDate,
                m.WarrantyEndDate,
                status,
                m.HardwareBrand,
                m.HardwareModel,
                m.HardwareSerialNumber,
                m.BiosSerial,
                m.AssetSerial,
                m.HostnameCityCode,
                m.HostnameChassisHint,
                m.HardwareCpu,
                m.HardwareRamGb,
                m.HardwareDiskGb,
                m.HardwareGpu,
                m.PsuWatts,
                m.SupportHourlyRate,
                m.HardwareManualOverride,
                userSeconds,
                supportSeconds,
                totalActive,
                costPerUserHour,
                supportCostEstimate,
                userToSupportRatio,
                m.OsInstallDateUtc,
                m.WindowsFolderCreatedUtc,
                m.LastReimagedUtc,
                m.MachineGuid,
                m.SmbiosUuid));
        }

        if (!string.IsNullOrWhiteSpace(Q))
        {
            var q = Q.Trim();
            rows = rows.Where(r =>
                r.Hostname.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (r.Brand?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (r.Model?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (r.SerialNumber?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (r.AssetSerial?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (r.BiosSerial?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (r.Cpu?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (r.Gpu?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(Warranty) &&
            !string.Equals(Warranty, "all", StringComparison.OrdinalIgnoreCase))
        {
            rows = rows.Where(r =>
                string.Equals(r.WarrantyStatus, Warranty, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        Rows = rows;
        ActiveWarrantyCount = rows.Count(r => r.WarrantyStatus == "Active");
        ExpiredWarrantyCount = rows.Count(r => r.WarrantyStatus == "Expired");
        UnknownWarrantyCount = rows.Count(r => r.WarrantyStatus == "Unknown");
        TotalPurchaseCost = rows.Where(r => r.PurchaseCost is not null).Sum(r => r.PurchaseCost!.Value);
        TotalUserHours30d = Math.Round(rows.Sum(r => r.UserSeconds30d) / 3600.0, 1);
        TotalSupportHours30d = Math.Round(rows.Sum(r => r.SupportSeconds30d) / 3600.0, 1);
        ReimagedCount = rows.Count(r => r.LastReimagedUtc is not null);
    }

    public static string ResolveWarranty(DateOnly? start, DateOnly? end, DateOnly today)
    {
        if (end is null && start is null)
            return "Unknown";
        if (end is DateOnly e)
            return e >= today ? "Active" : "Expired";
        // Start only — treat as unknown until end is set
        return "Unknown";
    }

    public static string BadgeClass(string status) => status switch
    {
        "Active" => "badge-active",
        "Expired" => "badge-expired",
        _ => "badge-ended"
    };

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public sealed record IdentityEventRow(
        string EventType,
        DateTimeOffset ObservedAtUtc,
        string? OldMachineGuid,
        string? NewMachineGuid,
        string? Detail);

    public sealed record CostRow(
        int Id,
        string Hostname,
        string? Region,
        string? Office,
        decimal? PurchaseCost,
        string? Currency,
        DateOnly? WarrantyStart,
        DateOnly? WarrantyEnd,
        string WarrantyStatus,
        string? Brand,
        string? Model,
        string? SerialNumber,
        string? BiosSerial,
        string? AssetSerial,
        string? CityCode,
        string? ChassisHint,
        string? Cpu,
        double? RamGb,
        double? DiskGb,
        string? Gpu,
        int? PsuWatts,
        decimal? SupportHourlyRate,
        bool ManualOverride,
        long UserSeconds30d,
        long SupportSeconds30d,
        long ActiveSeconds30d,
        decimal? CostPerUserHour,
        decimal? SupportCostEstimate,
        double? UserToSupportRatio,
        DateTimeOffset? OsInstallDateUtc,
        DateTimeOffset? WindowsFolderCreatedUtc,
        DateTimeOffset? LastReimagedUtc,
        string? MachineGuid,
        string? SmbiosUuid);
}
