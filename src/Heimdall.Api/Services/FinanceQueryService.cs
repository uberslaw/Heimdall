using Heimdall.Api.Data;
using Heimdall.Shared;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

/// <summary>Finance hub: hardware fingerprints, license purchases, and $/hour metrics.</summary>
public sealed class FinanceQueryService(HeimdallDbContext db, MachineUtilisationService util)
{
    public static string HardwareFingerprint(Machine m)
    {
        static string N(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s.Trim();
        var ram = m.HardwareRamGb is double r ? $"{r:0.#}GB" : "—";
        var disk = m.HardwareDiskGb is double d ? $"{d:0.#}GB" : "—";
        return $"{N(m.HardwareBrand)}|{N(m.HardwareModel)}|{N(m.HardwareCpu)}|{N(m.HardwareGpu)}|{ram}|{disk}";
    }

    public static string HardwareFingerprintLabel(Machine m)
    {
        var brand = string.IsNullOrWhiteSpace(m.HardwareBrand) ? null : m.HardwareBrand.Trim();
        var model = string.IsNullOrWhiteSpace(m.HardwareModel) ? null : m.HardwareModel.Trim();
        var title = string.Join(' ', new[] { brand, model }.Where(x => !string.IsNullOrEmpty(x)));
        if (string.IsNullOrEmpty(title))
            title = "Unknown model";
        var bits = new List<string>();
        if (!string.IsNullOrWhiteSpace(m.HardwareCpu)) bits.Add(m.HardwareCpu.Trim());
        if (!string.IsNullOrWhiteSpace(m.HardwareGpu)) bits.Add(m.HardwareGpu.Trim());
        if (m.HardwareRamGb is double ram) bits.Add($"{ram:0.#} GB RAM");
        if (m.HardwareDiskGb is double disk) bits.Add($"{disk:0.#} GB disk");
        return bits.Count == 0 ? title : $"{title} · {string.Join(" · ", bits)}";
    }

    /// <summary>
    /// Machines with purchase/warranty data the user can copy into an edit form.
    /// Same hardware fingerprint (Brand+Model+Cpu+Gpu+Ram+Disk) is listed first.
    /// </summary>
    public async Task<IReadOnlyList<PurchaseCopySource>> GetPurchaseCopySourcesAsync(
        int excludeMachineId,
        CancellationToken ct = default)
    {
        var machines = await db.Machines.AsNoTracking().ToListAsync(ct);
        var current = machines.FirstOrDefault(m => m.Id == excludeMachineId);
        if (current is null)
            return [];

        var fp = HardwareFingerprint(current);
        static bool HasPurchaseInfo(Machine m) =>
            m.PurchaseCost is > 0
            || m.PurchaseDate is not null
            || m.WarrantyStartDate is not null
            || m.WarrantyEndDate is not null;

        return machines
            .Where(m => m.Id != excludeMachineId && HasPurchaseInfo(m))
            .Select(m =>
            {
                var same = string.Equals(HardwareFingerprint(m), fp, StringComparison.Ordinal);
                var label = string.IsNullOrWhiteSpace(m.FriendlyName)
                    ? m.Hostname
                    : $"{m.FriendlyName.Trim()} ({m.Hostname})";
                return new PurchaseCopySource(
                    m.Id,
                    label,
                    same,
                    m.PurchaseCost,
                    m.PurchaseCurrency,
                    m.PurchaseDate,
                    m.WarrantyStartDate,
                    m.WarrantyEndDate);
            })
            .OrderByDescending(s => s.SameSpecs)
            .ThenBy(s => s.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task EnsurePurchasesImportedFromAppLicenseCostsAsync(CancellationToken ct = default)
    {
        // First visit only: seed purchase history from existing Utilization costs.
        if (await db.AppLicensePurchases.AnyAsync(ct))
            return;
        await ImportMissingPurchasesFromAppLicenseCostsAsync(ct);
    }

    /// <summary>
    /// Import <see cref="AppLicenseCost"/> rows for process names not yet present in purchase history.
    /// Returns the number of purchase rows added.
    /// </summary>
    public async Task<int> ImportMissingPurchasesFromAppLicenseCostsAsync(CancellationToken ct = default)
    {
        var year = DateTime.UtcNow.Year;
        var existing = await db.AppLicensePurchases.AsNoTracking()
            .Select(p => p.ProcessName)
            .ToListAsync(ct);
        var have = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        var costs = await db.AppLicenseCosts.AsNoTracking().ToListAsync(ct);
        var added = 0;
        foreach (var c in costs)
        {
            if (c.LicenseCostPerYear <= 0 || string.IsNullOrWhiteSpace(c.ProcessName))
                continue;
            if (have.Contains(c.ProcessName))
                continue;

            db.AppLicensePurchases.Add(new AppLicensePurchase
            {
                Vendor = "",
                SoftwareName = string.IsNullOrWhiteSpace(c.DisplayName) ? c.ProcessName : c.DisplayName!,
                ProcessName = c.ProcessName,
                LicenseCost = c.LicenseCostPerYear,
                MaintenanceCost = 0,
                PurchaseYear = year,
                WorkloadKind = LicenseWorkloadKinds.Design,
                ComputeBias = LicenseComputeBiases.Either,
                CreatedUtc = DateTimeOffset.UtcNow
            });
            have.Add(c.ProcessName);
            added++;
        }

        if (added > 0)
            await db.SaveChangesAsync(ct);
        return added;
    }

    /// <summary>Keep <see cref="AppLicenseCost"/> in sync with the latest purchase year per process (for Socratize).</summary>
    public async Task SyncAppLicenseCostsFromPurchasesAsync(CancellationToken ct = default)
    {
        var purchases = await db.AppLicensePurchases.AsNoTracking().ToListAsync(ct);
        var latestByProcess = purchases
            .GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(p => p.PurchaseYear).ThenByDescending(p => p.Id).First())
            .ToList();

        var existing = await db.AppLicenseCosts.ToListAsync(ct);
        var byName = existing.ToDictionary(x => x.ProcessName, StringComparer.OrdinalIgnoreCase);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in latestByProcess)
        {
            var total = p.LicenseCost + p.MaintenanceCost;
            if (total <= 0)
                continue;
            keep.Add(p.ProcessName);
            if (byName.TryGetValue(p.ProcessName, out var row))
            {
                row.LicenseCostPerYear = total;
                if (!string.IsNullOrWhiteSpace(p.SoftwareName))
                    row.DisplayName = p.SoftwareName;
            }
            else
            {
                var add = new AppLicenseCost
                {
                    ProcessName = p.ProcessName,
                    DisplayName = p.SoftwareName,
                    LicenseCostPerYear = total
                };
                db.AppLicenseCosts.Add(add);
                byName[p.ProcessName] = add;
            }
        }

        foreach (var row in existing.Where(r => !keep.Contains(r.ProcessName)).ToList())
            db.AppLicenseCosts.Remove(row);

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<HardwareGroupRow>> GetHardwareGroupsAsync(int? teamId, CancellationToken ct = default)
    {
        var q = db.Machines.AsNoTracking().Include(m => m.Team).AsQueryable();
        if (teamId is int tid)
            q = q.Where(m => m.TeamId == tid);

        var machines = await q.OrderBy(m => m.Hostname).ToListAsync(ct);
        return machines
            .GroupBy(HardwareFingerprint)
            .Select(g =>
            {
                var sample = g.First();
                var teams = g
                    .Select(m => m.Team?.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n)
                    .ToList();
                var hosts = g
                    .Select(m => new HardwareHostRef(
                        m.Id,
                        m.Hostname,
                        string.IsNullOrWhiteSpace(m.FriendlyName) ? m.Hostname : m.FriendlyName!,
                        m.Team?.Name,
                        m.PurchaseCost,
                        m.PurchaseDate,
                        m.WarrantyEndDate))
                    .OrderBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new HardwareGroupRow(
                    g.Key,
                    HardwareFingerprintLabel(sample),
                    sample.HardwareBrand,
                    sample.HardwareModel,
                    sample.HardwareCpu,
                    sample.HardwareGpu,
                    sample.HardwareRamGb,
                    sample.HardwareDiskGb,
                    g.Count(),
                    teams!,
                    hosts,
                    g.Where(m => m.PurchaseCost is > 0).Sum(m => m.PurchaseCost!.Value),
                    g.Count(m => m.WarrantyEndDate is DateOnly we && we >= DateOnly.FromDateTime(DateTime.UtcNow)));
            })
            .OrderByDescending(r => r.MachineCount)
            .ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<FinanceMetricsBundle> GetMetricsAsync(string period, int? metricYear, CancellationToken ct = default)
    {
        period = MachineUtilisationService.NormalizePeriod(period);
        var now = DateTimeOffset.UtcNow;
        var (from, to, windowSeconds) = MachineUtilisationService.ResolveWindow(period, now);
        var year = metricYear ?? now.Year;

        var machines = await db.Machines.AsNoTracking().Include(m => m.Team).ToListAsync(ct);
        var machineIds = machines.Select(m => m.Id).ToList();
        var utilRows = await util.ComputeAsync(machineIds, period, ct);

        var sessions = await db.Sessions.AsNoTracking()
            .Where(s => s.StartedAtUtc < to && (s.EndedAtUtc == null || s.EndedAtUtc > from))
            .ToListAsync(ct);

        var windowHours = windowSeconds / 3600.0;
        var hwRows = new List<HardwareCostMetricRow>();
        foreach (var m in machines)
        {
            utilRows.TryGetValue(m.Id, out var u);
            var activeHours = ((u?.ActivePct ?? 0) / 100.0) * windowHours;
            // Prefer non-ops Active only for HW $/h denominator.
            var userActiveSec = sessions
                .Where(s => s.MachineId == m.Id && !SupportAccount.IsOpsSupport(s.Username, s.Domain))
                .Sum(s => MachineUtilisationService.ActiveSecondsInWindow(s, from, to));
            var userHours = userActiveSec / 3600.0;
            double? costPerHour = null;
            if (m.PurchaseCost is > 0 && userHours > 0.01)
                costPerHour = (double)m.PurchaseCost.Value / userHours;

            hwRows.Add(new HardwareCostMetricRow(
                m.Id,
                m.Hostname,
                string.IsNullOrWhiteSpace(m.FriendlyName) ? m.Hostname : m.FriendlyName!,
                m.Team?.Name,
                HardwareFingerprintLabel(m),
                m.PurchaseCost,
                userHours,
                activeHours,
                costPerHour));
        }

        var purchases = await db.AppLicensePurchases.AsNoTracking()
            .Where(p => p.PurchaseYear == year)
            .ToListAsync(ct);
        // If no rows for selected year, fall back to latest year per process for metrics display.
        if (purchases.Count == 0)
        {
            purchases = (await db.AppLicensePurchases.AsNoTracking().ToListAsync(ct))
                .GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(p => p.PurchaseYear).First())
                .ToList();
        }

        var processNames = purchases.Select(p => p.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var runs = processNames.Count == 0
            ? []
            : await db.ProcessRuns.AsNoTracking()
                .Where(r => processNames.Contains(r.ProcessName)
                            && r.StartedAtUtc < to
                            && (r.EndedAtUtc == null || r.EndedAtUtc > from))
                .ToListAsync(ct);

        var periodDays = Math.Max(1.0, (to - from).TotalDays);
        var swRows = new List<SoftwareCostMetricRow>();
        foreach (var p in purchases.OrderBy(x => x.SoftwareName))
        {
            var procRuns = runs.Where(r =>
                string.Equals(r.ProcessName, p.ProcessName, StringComparison.OrdinalIgnoreCase)
                && ProcessRunMetrics.HasRuntime(r)).ToList();

            double usageSeconds;
            if (string.Equals(p.WorkloadKind, LicenseWorkloadKinds.Simulation, StringComparison.OrdinalIgnoreCase))
            {
                // Licence-hours ≈ sum of run durations (concurrent instances = multi-seat).
                usageSeconds = ProcessRunMetrics.SumDurationSeconds(procRuns, from, to);
            }
            else
            {
                // Design: ProcessRun open time overlapping Active session presence for same machine+user.
                usageSeconds = DesignActiveOverlapSeconds(procRuns, sessions, from, to);
            }

            var usageHours = usageSeconds / 3600.0;
            var annualizedHours = usageHours * (365.0 / periodDays);
            var totalCost = p.LicenseCost + p.MaintenanceCost;
            double? costPerHour = totalCost > 0 && annualizedHours > 0.01
                ? totalCost / annualizedHours
                : null;

            var avgConcurrent = ProcessRunMetrics.AvgConcurrentProcesses(procRuns, from, to);

            // Intensity: peak util on runs (GPU preferred when bias says Gpu / Either with GPU peaks).
            var (lowShare, highShare, utilHoursPerDollar) = ComputeIntensity(p, procRuns);

            swRows.Add(new SoftwareCostMetricRow(
                p.Id,
                p.Vendor,
                p.SoftwareName,
                p.ProcessName,
                p.WorkloadKind,
                p.ComputeBias,
                p.PurchaseYear,
                totalCost,
                usageHours,
                annualizedHours,
                costPerHour,
                avgConcurrent,
                lowShare,
                highShare,
                utilHoursPerDollar));
        }

        return new FinanceMetricsBundle(period, from, to, year, hwRows, swRows);
    }

    private static double DesignActiveOverlapSeconds(
        IReadOnlyList<ProcessRun> runs,
        IReadOnlyList<UserSession> sessions,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        double total = 0;
        foreach (var run in runs)
        {
            var (rStart, rEnd) = Clip(run.StartedAtUtc, run.EndedAtUtc ?? run.LastSeenAtUtc, from, to);
            if (rEnd <= rStart) continue;

            var userSessions = sessions.Where(s =>
                s.MachineId == run.MachineId
                && string.Equals(s.Username, run.Username, StringComparison.OrdinalIgnoreCase)).ToList();

            // Approximate Active fraction of overlapping session wall time.
            foreach (var s in userSessions)
            {
                var (sStart, sEnd) = Clip(s.StartedAtUtc, s.EndedAtUtc ?? s.LastObservedUtc, from, to);
                var oStart = rStart > sStart ? rStart : sStart;
                var oEnd = rEnd < sEnd ? rEnd : sEnd;
                if (oEnd <= oStart) continue;

                var wall = (s.EndedAtUtc ?? s.LastObservedUtc) - s.StartedAtUtc;
                var wallSec = Math.Max(1.0, wall.TotalSeconds);
                var activeFrac = Math.Clamp(s.ActiveSeconds / wallSec, 0, 1);
                total += (oEnd - oStart).TotalSeconds * activeFrac;
            }
        }

        return total;
    }

    private static (DateTimeOffset Start, DateTimeOffset End) Clip(
        DateTimeOffset start, DateTimeOffset end, DateTimeOffset from, DateTimeOffset to)
    {
        if (start < from) start = from;
        if (end > to) end = to;
        return (start, end);
    }

    private static (double? LowShare, double? HighShare, double? UtilHoursPerDollar) ComputeIntensity(
        AppLicensePurchase p,
        IReadOnlyList<ProcessRun> runs)
    {
        if (runs.Count == 0)
            return (null, null, null);

        var peaks = new List<double>();
        foreach (var r in runs)
        {
            double? peak = p.ComputeBias switch
            {
                LicenseComputeBiases.Cpu => r.PeakCpuPercent,
                LicenseComputeBiases.Gpu => r.PeakGpuPercent ?? r.PeakCpuPercent,
                _ => r.PeakGpuPercent ?? r.PeakCpuPercent
            };
            if (peak is double v && v >= 0)
                peaks.Add(v);
        }

        if (peaks.Count == 0)
            return (null, null, null);

        var low = peaks.Count(v => v < 50) / (double)peaks.Count;
        var high = 1.0 - low;

        // Approximate util-hours: mean peak% / 100 × sum duration hours / cost
        var sumHours = ProcessRunMetrics.SumDurationSeconds(runs) / 3600.0;
        var meanPeak = peaks.Average();
        var utilHours = (meanPeak / 100.0) * sumHours;
        var cost = p.LicenseCost + p.MaintenanceCost;
        double? perDollar = cost > 0.01 ? utilHours / cost : null;
        return (low, high, perDollar);
    }

    public sealed record PurchaseCopySource(
        int MachineId,
        string Label,
        bool SameSpecs,
        decimal? PurchaseCost,
        string? PurchaseCurrency,
        DateOnly? PurchaseDate,
        DateOnly? WarrantyStartDate,
        DateOnly? WarrantyEndDate);

    public sealed record HardwareHostRef(
        int MachineId,
        string Hostname,
        string DisplayName,
        string? TeamName,
        decimal? PurchaseCost,
        DateOnly? PurchaseDate,
        DateOnly? WarrantyEndDate);

    public sealed record HardwareGroupRow(
        string Fingerprint,
        string Label,
        string? Brand,
        string? Model,
        string? Cpu,
        string? Gpu,
        double? RamGb,
        double? DiskGb,
        int MachineCount,
        IReadOnlyList<string> Teams,
        IReadOnlyList<HardwareHostRef> Hosts,
        decimal PurchaseCostSum,
        int WarrantyActiveCount);

    public sealed record HardwareCostMetricRow(
        int MachineId,
        string Hostname,
        string DisplayName,
        string? TeamName,
        string SpecLabel,
        decimal? PurchaseCost,
        double UserActiveHours,
        double ActiveHours,
        double? CostPerUserHour);

    public sealed record SoftwareCostMetricRow(
        int PurchaseId,
        string Vendor,
        string SoftwareName,
        string ProcessName,
        string WorkloadKind,
        string ComputeBias,
        int PurchaseYear,
        double TotalCost,
        double UsageHoursInPeriod,
        double AnnualizedHours,
        double? CostPerHour,
        double AvgConcurrent,
        double? IntensityLowShare,
        double? IntensityHighShare,
        double? UtilHoursPerDollar);

    public sealed record FinanceMetricsBundle(
        string Period,
        DateTimeOffset FromUtc,
        DateTimeOffset ToUtc,
        int Year,
        IReadOnlyList<HardwareCostMetricRow> Hardware,
        IReadOnlyList<SoftwareCostMetricRow> Software);
}
