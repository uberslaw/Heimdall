using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

public class FinanceModel(
    HeimdallDbContext db,
    FinanceQueryService finance) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string Tab { get; set; } = "hardware";

    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Period { get; set; } = "30d";

    [BindProperty(SupportsGet = true)]
    public int? Year { get; set; }

    public IReadOnlyList<Team> Teams { get; private set; } = [];
    public IReadOnlyList<FinanceQueryService.HardwareGroupRow> HardwareGroups { get; private set; } = [];
    public IReadOnlyList<AppLicensePurchase> Purchases { get; private set; } = [];
    public IReadOnlyList<ProcessPick> ProcessPicks { get; private set; } = [];
    public FinanceQueryService.FinanceMetricsBundle? Metrics { get; private set; }

    public int? EditingMachineId { get; private set; }
    public Machine? EditingMachine { get; private set; }
    public IReadOnlyList<FinanceQueryService.PurchaseCopySource> PurchaseCopySources { get; private set; } = [];

    [BindProperty] public int? EditMachineId { get; set; }
    [BindProperty] public decimal? PurchaseCost { get; set; }
    [BindProperty] public string? PurchaseCurrency { get; set; } = "AUD";
    [BindProperty] public DateOnly? PurchaseDate { get; set; }
    [BindProperty] public DateOnly? WarrantyStartDate { get; set; }
    [BindProperty] public DateOnly? WarrantyEndDate { get; set; }
    [BindProperty] public string? HardwareBrand { get; set; }
    [BindProperty] public string? HardwareModel { get; set; }
    [BindProperty] public string? HardwareCpu { get; set; }
    [BindProperty] public string? HardwareGpu { get; set; }
    [BindProperty] public double? HardwareRamGb { get; set; }
    [BindProperty] public double? HardwareDiskGb { get; set; }
    [BindProperty] public bool HardwareManualOverride { get; set; } = true;

    [BindProperty] public int? PurchaseId { get; set; }
    [BindProperty] public string? Vendor { get; set; }
    [BindProperty] public string? SoftwareName { get; set; }
    [BindProperty] public string? ProcessName { get; set; }
    [BindProperty] public int? ProcessCatalogEntryId { get; set; }
    [BindProperty] public double LicenseCost { get; set; }
    [BindProperty] public double MaintenanceCost { get; set; }
    [BindProperty] public int PurchaseYear { get; set; } = DateTime.UtcNow.Year;
    [BindProperty] public string WorkloadKind { get; set; } = LicenseWorkloadKinds.Design;
    [BindProperty] public string ComputeBias { get; set; } = LicenseComputeBiases.Either;
    [BindProperty] public string? Notes { get; set; }

    public string ActiveTab => NormalizeTab(Tab);

    public async Task OnGetAsync(int? editMachine, CancellationToken ct)
    {
        await finance.EnsurePurchasesImportedFromAppLicenseCostsAsync(ct);
        await LoadAsync(ct);
        if (editMachine is int mid)
            await LoadMachineEditAsync(mid, ct);
    }

    public async Task<IActionResult> OnPostSaveMachineAsync(CancellationToken ct)
    {
        if (EditMachineId is not int id)
        {
            TempData["Error"] = "No machine selected.";
            return RedirectToFinance("hardware");
        }

        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (machine is null)
        {
            TempData["Error"] = "Machine not found.";
            return RedirectToFinance("hardware");
        }

        machine.PurchaseCost = PurchaseCost is > 0 ? PurchaseCost : null;
        machine.PurchaseCurrency = string.IsNullOrWhiteSpace(PurchaseCurrency) ? "AUD" : PurchaseCurrency.Trim().ToUpperInvariant();
        machine.PurchaseDate = PurchaseDate;
        machine.WarrantyStartDate = WarrantyStartDate;
        machine.WarrantyEndDate = WarrantyEndDate;
        machine.HardwareBrand = TrimOrNull(HardwareBrand);
        machine.HardwareModel = TrimOrNull(HardwareModel);
        machine.HardwareCpu = TrimOrNull(HardwareCpu);
        machine.HardwareGpu = TrimOrNull(HardwareGpu);
        machine.HardwareRamGb = HardwareRamGb;
        machine.HardwareDiskGb = HardwareDiskGb;
        machine.HardwareManualOverride = HardwareManualOverride;
        await db.SaveChangesAsync(ct);
        TempData["Message"] = $"Saved finance fields for {machine.Hostname}.";
        return RedirectToFinance("hardware", TeamId);
    }

    public async Task<IActionResult> OnPostSavePurchaseAsync(CancellationToken ct)
    {
        var process = ConfigService.NormalizeProcessName(ProcessName ?? "");
        if (string.IsNullOrWhiteSpace(process) || string.IsNullOrWhiteSpace(SoftwareName))
        {
            TempData["Error"] = "Software name and a Discovery process are required.";
            return RedirectToFinance("software");
        }

        var catalogHit = await db.ProcessCatalogEntries.AsNoTracking()
            .AnyAsync(e => e.ProcessName == process, ct);
        var knownHit = await db.KnownApps.AsNoTracking()
            .AnyAsync(k => k.ProcessName == process, ct);
        if (!catalogHit && !knownHit)
        {
            TempData["Error"] = $"Process '{process}' was not found in Discovery catalog or Known apps.";
            return RedirectToFinance("software");
        }

        var year = PurchaseYear is >= 1990 and <= 2100 ? PurchaseYear : DateTime.UtcNow.Year;
        var kind = string.Equals(WorkloadKind, LicenseWorkloadKinds.Simulation, StringComparison.OrdinalIgnoreCase)
            ? LicenseWorkloadKinds.Simulation
            : LicenseWorkloadKinds.Design;
        var bias = ComputeBias switch
        {
            LicenseComputeBiases.Cpu => LicenseComputeBiases.Cpu,
            LicenseComputeBiases.Gpu => LicenseComputeBiases.Gpu,
            _ => LicenseComputeBiases.Either
        };

        int? catalogId = ProcessCatalogEntryId;
        if (catalogId is null)
        {
            catalogId = await db.ProcessCatalogEntries.AsNoTracking()
                .Where(e => e.ProcessName == process)
                .OrderByDescending(e => e.Id)
                .Select(e => (int?)e.Id)
                .FirstOrDefaultAsync(ct);
        }

        if (PurchaseId is int pid)
        {
            var row = await db.AppLicensePurchases.FirstOrDefaultAsync(p => p.Id == pid, ct);
            if (row is null)
            {
                TempData["Error"] = "Purchase not found.";
                return RedirectToFinance("software");
            }

            row.Vendor = Vendor?.Trim() ?? "";
            row.SoftwareName = SoftwareName.Trim();
            row.ProcessName = process;
            row.ProcessCatalogEntryId = catalogId;
            row.LicenseCost = Math.Max(0, LicenseCost);
            row.MaintenanceCost = Math.Max(0, MaintenanceCost);
            row.PurchaseYear = year;
            row.WorkloadKind = kind;
            row.ComputeBias = bias;
            row.Notes = TrimOrNull(Notes);
        }
        else
        {
            db.AppLicensePurchases.Add(new AppLicensePurchase
            {
                Vendor = Vendor?.Trim() ?? "",
                SoftwareName = SoftwareName.Trim(),
                ProcessName = process,
                ProcessCatalogEntryId = catalogId,
                LicenseCost = Math.Max(0, LicenseCost),
                MaintenanceCost = Math.Max(0, MaintenanceCost),
                PurchaseYear = year,
                WorkloadKind = kind,
                ComputeBias = bias,
                Notes = TrimOrNull(Notes),
                CreatedUtc = DateTimeOffset.UtcNow
            });
        }

        await db.SaveChangesAsync(ct);
        await finance.SyncAppLicenseCostsFromPurchasesAsync(ct);
        TempData["Message"] = "License purchase saved (Socratize costs synced).";
        return RedirectToFinance("software");
    }

    public async Task<IActionResult> OnPostDeletePurchaseAsync(int id, CancellationToken ct)
    {
        var row = await db.AppLicensePurchases.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (row is not null)
        {
            db.AppLicensePurchases.Remove(row);
            await db.SaveChangesAsync(ct);
            await finance.SyncAppLicenseCostsFromPurchasesAsync(ct);
            TempData["Message"] = "License purchase deleted.";
        }

        return RedirectToFinance("software");
    }

    public async Task<IActionResult> OnPostImportLicenseCostsAsync(CancellationToken ct)
    {
        var added = await finance.ImportMissingPurchasesFromAppLicenseCostsAsync(ct);
        if (added > 0)
            await finance.SyncAppLicenseCostsFromPurchasesAsync(ct);
        TempData["Message"] = added > 0
            ? $"Imported {added} license purchase(s) from Utilization costs."
            : "No new Utilization license costs to import.";
        return RedirectToFinance("software");
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Teams = await db.Teams.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
        Tab = ActiveTab;

        if (ActiveTab == "hardware")
            HardwareGroups = await finance.GetHardwareGroupsAsync(TeamId, ct);
        else if (ActiveTab == "software")
        {
            Purchases = await db.AppLicensePurchases.AsNoTracking()
                .OrderByDescending(p => p.PurchaseYear)
                .ThenBy(p => p.SoftwareName)
                .ToListAsync(ct);
            ProcessPicks = (await db.ProcessCatalogEntries.AsNoTracking()
                    .OrderBy(e => e.ProcessName)
                    .Select(e => new { e.Id, e.ProcessName, Hint = e.CompanyName ?? e.DisplayName })
                    .ToListAsync(ct))
                .GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var x = g.First();
                    return new ProcessPick(x.Id, x.ProcessName, x.Hint);
                })
                .OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else
        {
            Metrics = await finance.GetMetricsAsync(Period, Year, ct);
        }
    }

    private async Task LoadMachineEditAsync(int id, CancellationToken ct)
    {
        EditingMachine = await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
        if (EditingMachine is null)
            return;
        EditingMachineId = id;
        EditMachineId = id;
        PurchaseCost = EditingMachine.PurchaseCost;
        PurchaseCurrency = EditingMachine.PurchaseCurrency ?? "AUD";
        PurchaseDate = EditingMachine.PurchaseDate;
        WarrantyStartDate = EditingMachine.WarrantyStartDate;
        WarrantyEndDate = EditingMachine.WarrantyEndDate;
        HardwareBrand = EditingMachine.HardwareBrand;
        HardwareModel = EditingMachine.HardwareModel;
        HardwareCpu = EditingMachine.HardwareCpu;
        HardwareGpu = EditingMachine.HardwareGpu;
        HardwareRamGb = EditingMachine.HardwareRamGb;
        HardwareDiskGb = EditingMachine.HardwareDiskGb;
        HardwareManualOverride = EditingMachine.HardwareManualOverride;
        PurchaseCopySources = await finance.GetPurchaseCopySourcesAsync(id, ct);
    }

    private IActionResult RedirectToFinance(string tab, int? teamId = null) =>
        RedirectToPage(new { tab, teamId, period = Period, year = Year });

    private static string NormalizeTab(string? tab) =>
        (tab ?? "hardware").Trim().ToLowerInvariant() switch
        {
            "software" or "licenses" => "software",
            "metrics" or "costs" => "metrics",
            _ => "hardware"
        };

    private static string? TrimOrNull(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public sealed record ProcessPick(int Id, string ProcessName, string? Hint);
}
