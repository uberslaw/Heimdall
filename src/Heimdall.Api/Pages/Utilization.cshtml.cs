using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

public class UtilizationModel(HeimdallDbContext db) : PageModel
{
    public UtilizationCriteria Criteria { get; private set; } = new();
    public List<AppLicenseRowVm> LicenseRows { get; private set; } = [];

    [BindProperty] public double WeightUsers { get; set; } = 25;
    [BindProperty] public double WeightDailyUtil { get; set; } = 35;
    [BindProperty] public double WeightMetricBusy { get; set; } = 20;
    [BindProperty] public double WeightAppValue { get; set; } = 20;
    [BindProperty] public int IdealMinUsers { get; set; } = 2;
    [BindProperty] public double IdealDailyUtilPct { get; set; } = 40;
    [BindProperty] public double WorkingHoursPerDay { get; set; } = 8;
    [BindProperty] public double BusyCpuPercentThreshold { get; set; } = 25;
    [BindProperty] public double BusyGpuPercentThreshold { get; set; } = 20;
    [BindProperty] public double IdealMetricBusyPct { get; set; } = 15;
    [BindProperty] public double IdealMaxCostPerHour { get; set; } = 50;
    [BindProperty] public double HighScoreThreshold { get; set; } = 75;
    [BindProperty] public double AdequateScoreThreshold { get; set; } = 50;
    [BindProperty] public double MixedScoreThreshold { get; set; } = 30;

    [BindProperty] public List<LicenseEditVm> LicenseEdits { get; set; } = [];
    [BindProperty] public string? NewLicenseProcessName { get; set; }
    [BindProperty] public string? NewLicenseDisplayName { get; set; }
    [BindProperty] public double? NewLicenseCostPerYear { get; set; }

    public double WeightSum => WeightUsers + WeightDailyUtil + WeightMetricBusy + WeightAppValue;

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
    }

    public async Task<IActionResult> OnPostSaveCriteriaAsync(CancellationToken ct)
    {
        var row = await EnsureCriteriaAsync(ct);
        row.WeightUsers = Math.Max(0, WeightUsers);
        row.WeightDailyUtil = Math.Max(0, WeightDailyUtil);
        row.WeightMetricBusy = Math.Max(0, WeightMetricBusy);
        row.WeightAppValue = Math.Max(0, WeightAppValue);
        row.IdealMinUsers = Math.Max(0, IdealMinUsers);
        row.IdealDailyUtilPct = Math.Clamp(IdealDailyUtilPct, 0, 100);
        row.WorkingHoursPerDay = Math.Clamp(WorkingHoursPerDay, 1, 24);
        row.BusyCpuPercentThreshold = Math.Clamp(BusyCpuPercentThreshold, 0, 100);
        row.BusyGpuPercentThreshold = Math.Clamp(BusyGpuPercentThreshold, 0, 100);
        row.IdealMetricBusyPct = Math.Clamp(IdealMetricBusyPct, 0, 100);
        row.IdealMaxCostPerHour = Math.Max(0.01, IdealMaxCostPerHour);
        row.HighScoreThreshold = Math.Clamp(HighScoreThreshold, 0, 100);
        row.AdequateScoreThreshold = Math.Clamp(AdequateScoreThreshold, 0, 100);
        row.MixedScoreThreshold = Math.Clamp(MixedScoreThreshold, 0, 100);

        if (row.AdequateScoreThreshold > row.HighScoreThreshold)
            row.AdequateScoreThreshold = row.HighScoreThreshold;
        if (row.MixedScoreThreshold > row.AdequateScoreThreshold)
            row.MixedScoreThreshold = row.AdequateScoreThreshold;

        await db.SaveChangesAsync(ct);
        TempData["Message"] = "Utilization criteria saved (Global scope).";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSaveLicensesAsync(CancellationToken ct)
    {
        var existing = await db.AppLicenseCosts.ToListAsync(ct);
        var byProcess = existing.ToDictionary(x => x.ProcessName, StringComparer.OrdinalIgnoreCase);

        foreach (var edit in LicenseEdits)
        {
            var name = ConfigService.NormalizeProcessName(edit.ProcessName ?? "");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (edit.LicenseCostPerYear is null or <= 0)
            {
                if (byProcess.TryGetValue(name, out var remove))
                {
                    db.AppLicenseCosts.Remove(remove);
                    byProcess.Remove(name);
                }
                continue;
            }

            if (byProcess.TryGetValue(name, out var row))
            {
                row.LicenseCostPerYear = edit.LicenseCostPerYear.Value;
                if (!string.IsNullOrWhiteSpace(edit.DisplayName))
                    row.DisplayName = edit.DisplayName.Trim();
            }
            else
            {
                var add = new AppLicenseCost
                {
                    ProcessName = name,
                    DisplayName = string.IsNullOrWhiteSpace(edit.DisplayName) ? null : edit.DisplayName.Trim(),
                    LicenseCostPerYear = edit.LicenseCostPerYear.Value
                };
                db.AppLicenseCosts.Add(add);
                byProcess[name] = add;
            }
        }

        if (!string.IsNullOrWhiteSpace(NewLicenseProcessName) && NewLicenseCostPerYear is > 0)
        {
            var name = ConfigService.NormalizeProcessName(NewLicenseProcessName);
            if (!byProcess.ContainsKey(name))
            {
                db.AppLicenseCosts.Add(new AppLicenseCost
                {
                    ProcessName = name,
                    DisplayName = string.IsNullOrWhiteSpace(NewLicenseDisplayName) ? null : NewLicenseDisplayName.Trim(),
                    LicenseCostPerYear = NewLicenseCostPerYear.Value
                });
            }
            else
            {
                byProcess[name].LicenseCostPerYear = NewLicenseCostPerYear.Value;
                if (!string.IsNullOrWhiteSpace(NewLicenseDisplayName))
                    byProcess[name].DisplayName = NewLicenseDisplayName.Trim();
            }
        }

        await db.SaveChangesAsync(ct);
        TempData["Message"] = "App license costs saved.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteLicenseAsync(int id, CancellationToken ct)
    {
        var row = await db.AppLicenseCosts.FindAsync([id], ct);
        if (row is not null)
        {
            db.AppLicenseCosts.Remove(row);
            await db.SaveChangesAsync(ct);
            TempData["Message"] = $"Removed license cost for {row.ProcessName}.";
        }
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Criteria = await EnsureCriteriaAsync(ct);
        BindFrom(Criteria);

        var known = await db.KnownApps.AsNoTracking().OrderBy(a => a.DisplayName).ToListAsync(ct);
        var costs = await db.AppLicenseCosts.AsNoTracking().ToListAsync(ct);
        var costByProcess = costs.ToDictionary(c => c.ProcessName, StringComparer.OrdinalIgnoreCase);

        var rows = new List<AppLicenseRowVm>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in known)
        {
            costByProcess.TryGetValue(app.ProcessName, out var cost);
            rows.Add(new AppLicenseRowVm(
                cost?.Id,
                app.ProcessName,
                app.DisplayName,
                cost?.LicenseCostPerYear));
            seen.Add(app.ProcessName);
        }

        foreach (var cost in costs.OrderBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Contains(cost.ProcessName))
                continue;
            rows.Add(new AppLicenseRowVm(cost.Id, cost.ProcessName, cost.DisplayName, cost.LicenseCostPerYear));
        }

        LicenseRows = rows;
        LicenseEdits = rows.Select(r => new LicenseEditVm
        {
            Id = r.Id,
            ProcessName = r.ProcessName,
            DisplayName = r.DisplayName,
            LicenseCostPerYear = r.LicenseCostPerYear
        }).ToList();
    }

    private async Task<UtilizationCriteria> EnsureCriteriaAsync(CancellationToken ct)
    {
        var row = await db.UtilizationCriteria.FirstOrDefaultAsync(ct);
        if (row is not null)
            return row;

        row = new UtilizationCriteria { Scope = "Global" };
        db.UtilizationCriteria.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    private void BindFrom(UtilizationCriteria c)
    {
        WeightUsers = c.WeightUsers;
        WeightDailyUtil = c.WeightDailyUtil;
        WeightMetricBusy = c.WeightMetricBusy;
        WeightAppValue = c.WeightAppValue;
        IdealMinUsers = c.IdealMinUsers;
        IdealDailyUtilPct = c.IdealDailyUtilPct;
        WorkingHoursPerDay = c.WorkingHoursPerDay;
        BusyCpuPercentThreshold = c.BusyCpuPercentThreshold;
        BusyGpuPercentThreshold = c.BusyGpuPercentThreshold;
        IdealMetricBusyPct = c.IdealMetricBusyPct;
        IdealMaxCostPerHour = c.IdealMaxCostPerHour;
        HighScoreThreshold = c.HighScoreThreshold;
        AdequateScoreThreshold = c.AdequateScoreThreshold;
        MixedScoreThreshold = c.MixedScoreThreshold;
    }
}

public sealed record AppLicenseRowVm(int? Id, string ProcessName, string? DisplayName, double? LicenseCostPerYear);

public class LicenseEditVm
{
    public int? Id { get; set; }
    public string? ProcessName { get; set; }
    public string? DisplayName { get; set; }
    public double? LicenseCostPerYear { get; set; }
}
