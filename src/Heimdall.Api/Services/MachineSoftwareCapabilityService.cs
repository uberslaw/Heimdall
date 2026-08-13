using System.Text.Json;
using Heimdall.Api.Data;
using Heimdall.Shared;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

/// <summary>
/// Software capability tags for the public remote workstation pool.
/// Detected proposals stay Pending until an admin Approves them for the public filter.
/// </summary>
public sealed class MachineSoftwareCapabilityService(HeimdallDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Curated process-name → capability label (modelling / common studio apps).</summary>
    private static readonly Dictionary<string, string> ProcessLabelMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Revit"] = "Revit",
        ["acad"] = "AutoCAD",
        ["accoreconsole"] = "AutoCAD",
        ["roamer"] = "Navisworks",
        ["Navisworks"] = "Navisworks",
        ["3dsmax"] = "3ds Max",
        ["Rhino"] = "Rhino",
        ["Grasshopper"] = "Rhino",
        ["Civil3D"] = "Civil 3D",
        ["acadlt"] = "AutoCAD",
        ["Photoshop"] = "Photoshop",
        ["Illustrator"] = "Illustrator",
        ["EXCEL"] = "Excel",
        ["WINWORD"] = "Word",
        ["POWERPNT"] = "PowerPoint",
        ["ms-teams"] = "Teams",
        ["Teams"] = "Teams",
        ["chrome"] = "Chrome",
        ["msedge"] = "Edge",
        ["Tuflow"] = "TUFLOW",
        ["TuflowFV"] = "TUFLOW",
    };

    public sealed record CapabilityRow(
        int Id,
        int MachineId,
        string Hostname,
        string Label,
        MachineSoftwareCapabilitySource Source,
        MachineSoftwareCapabilityStatus Status,
        DateTimeOffset CreatedUtc,
        string? ProposedBy);

    public async Task<IReadOnlyList<string>> ListApprovedLabelsForMachineAsync(int machineId, CancellationToken ct)
    {
        return await db.MachineSoftwareCapabilities.AsNoTracking()
            .Where(c => c.MachineId == machineId && c.Status == MachineSoftwareCapabilityStatus.Approved)
            .Select(c => c.Label)
            .OrderBy(l => l)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<string>>> ListApprovedLabelsByMachineAsync(
        IReadOnlyCollection<int> machineIds,
        CancellationToken ct)
    {
        if (machineIds.Count == 0)
            return new Dictionary<int, IReadOnlyList<string>>();

        var rows = await db.MachineSoftwareCapabilities.AsNoTracking()
            .Where(c => machineIds.Contains(c.MachineId) && c.Status == MachineSoftwareCapabilityStatus.Approved)
            .Select(c => new { c.MachineId, c.Label })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.MachineId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(x => x.Label).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList());
    }

    public async Task<IReadOnlyList<string>> ListDistinctApprovedLabelsAsync(CancellationToken ct)
    {
        return await db.MachineSoftwareCapabilities.AsNoTracking()
            .Where(c => c.Status == MachineSoftwareCapabilityStatus.Approved)
            .Select(c => c.Label)
            .Distinct()
            .OrderBy(l => l)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CapabilityRow>> ListAllAsync(CancellationToken ct)
    {
        var rows = await db.MachineSoftwareCapabilities.AsNoTracking()
            .Include(c => c.Machine)
            .OrderBy(c => c.Status)
            .ThenBy(c => c.Machine.Hostname)
            .ThenBy(c => c.Label)
            .ToListAsync(ct);

        return rows.Select(c => new CapabilityRow(
            c.Id,
            c.MachineId,
            c.Machine.Hostname,
            c.Label,
            c.Source,
            c.Status,
            c.CreatedUtc,
            c.ProposedBy)).ToList();
    }

    public async Task<(bool Ok, string Message)> TryAddManualAsync(
        int machineId,
        string label,
        string? proposedBy,
        CancellationToken ct)
    {
        var norm = NormalizeLabel(label);
        if (norm.Length == 0)
            return (false, "Label is required.");

        var machine = await db.Machines.AsNoTracking()
            .Include(m => m.Team)
            .FirstOrDefaultAsync(m => m.Id == machineId, ct);
        if (machine is null)
            return (false, "Machine not found.");
        if (machine.Team is null || !machine.Team.IsPublicFacing)
            return (false, "Machine is not in the public remote workstation pool.");

        var existing = await db.MachineSoftwareCapabilities
            .FirstOrDefaultAsync(c => c.MachineId == machineId && c.Label == norm, ct);
        if (existing is not null)
        {
            if (existing.Status == MachineSoftwareCapabilityStatus.Approved)
                return (false, $"“{norm}” is already approved on {machine.Hostname}.");
            existing.Status = MachineSoftwareCapabilityStatus.Pending;
            existing.Source = MachineSoftwareCapabilitySource.Manual;
            existing.ProposedBy = proposedBy;
            existing.ReviewedUtc = null;
            existing.ReviewedBy = null;
            await db.SaveChangesAsync(ct);
            return (true, $"Re-queued “{norm}” for approval on {machine.Hostname}.");
        }

        db.MachineSoftwareCapabilities.Add(new MachineSoftwareCapability
        {
            MachineId = machineId,
            Label = norm,
            Source = MachineSoftwareCapabilitySource.Manual,
            Status = MachineSoftwareCapabilityStatus.Pending,
            CreatedUtc = DateTimeOffset.UtcNow,
            ProposedBy = proposedBy
        });
        await db.SaveChangesAsync(ct);
        return (true, $"Proposed “{norm}” on {machine.Hostname} (pending approval).");
    }

    public async Task<(bool Ok, string Message)> TrySetStatusAsync(
        int id,
        MachineSoftwareCapabilityStatus status,
        string? reviewedBy,
        CancellationToken ct)
    {
        var row = await db.MachineSoftwareCapabilities
            .Include(c => c.Machine)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (row is null)
            return (false, "Capability not found.");

        row.Status = status;
        row.ReviewedUtc = DateTimeOffset.UtcNow;
        row.ReviewedBy = reviewedBy;
        await db.SaveChangesAsync(ct);
        return (true, $"{status} “{row.Label}” on {row.Machine.Hostname}.");
    }

    public async Task<(bool Ok, string Message)> TryDeleteAsync(int id, CancellationToken ct)
    {
        var row = await db.MachineSoftwareCapabilities
            .Include(c => c.Machine)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (row is null)
            return (false, "Capability not found.");
        db.MachineSoftwareCapabilities.Remove(row);
        await db.SaveChangesAsync(ct);
        return (true, $"Removed “{row.Label}” from {row.Machine.Hostname}.");
    }

    /// <summary>
    /// Scan catalog sightings for public-pool machines and propose Pending capability labels.
    /// Does not auto-approve.
    /// </summary>
    public async Task<(int Proposed, int Skipped)> ProposeFromCatalogAsync(CancellationToken ct)
    {
        var poolMachines = await db.Machines.AsNoTracking()
            .Where(m => m.TeamId != null && m.Team != null && m.Team.IsPublicFacing)
            .Select(m => new { m.Id, m.Hostname })
            .ToListAsync(ct);
        if (poolMachines.Count == 0)
            return (0, 0);

        var hostToId = poolMachines.ToDictionary(m => m.Hostname, m => m.Id, StringComparer.OrdinalIgnoreCase);
        var existing = await db.MachineSoftwareCapabilities
            .Where(c => hostToId.Values.Contains(c.MachineId))
            .Select(c => new { c.MachineId, c.Label })
            .ToListAsync(ct);
        var existingKeys = new HashSet<string>(
            existing.Select(e => Key(e.MachineId, e.Label)),
            StringComparer.OrdinalIgnoreCase);

        var labelMap = new Dictionary<string, string>(ProcessLabelMap, StringComparer.OrdinalIgnoreCase);
        var knownApps = await db.KnownApps.AsNoTracking().ToListAsync(ct);
        foreach (var ka in knownApps)
        {
            if (!string.IsNullOrWhiteSpace(ka.ProcessName) && !string.IsNullOrWhiteSpace(ka.DisplayName))
                labelMap.TryAdd(ka.ProcessName.Trim(), ka.DisplayName.Trim());
        }

        var catalog = await db.ProcessCatalogEntries.AsNoTracking()
            .Where(e => !e.Ignored && e.SeenHostnamesJson != null && e.SeenHostnamesJson != "")
            .Select(e => new { e.ProcessName, e.ExecutablePath, e.SeenHostnamesJson })
            .ToListAsync(ct);

        var proposed = 0;
        var skipped = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in catalog)
        {
            var label = ResolveLabel(entry.ProcessName, entry.ExecutablePath, labelMap);
            if (label is null)
            {
                skipped++;
                continue;
            }

            foreach (var host in DeserializeHostnames(entry.SeenHostnamesJson))
            {
                if (!hostToId.TryGetValue(host, out var machineId))
                    continue;
                var k = Key(machineId, label);
                if (!existingKeys.Add(k))
                {
                    skipped++;
                    continue;
                }

                db.MachineSoftwareCapabilities.Add(new MachineSoftwareCapability
                {
                    MachineId = machineId,
                    Label = label,
                    Source = MachineSoftwareCapabilitySource.Detected,
                    Status = MachineSoftwareCapabilityStatus.Pending,
                    CreatedUtc = now,
                    ProposedBy = "catalog-detect"
                });
                proposed++;
            }
        }

        if (proposed > 0)
            await db.SaveChangesAsync(ct);

        return (proposed, skipped);
    }

    private static string? ResolveLabel(
        string processName,
        string? executablePath,
        IReadOnlyDictionary<string, string> labelMap)
    {
        var stem = processName.Trim();
        if (stem.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^4];

        if (labelMap.TryGetValue(stem, out var mapped))
            return NormalizeLabel(mapped);

        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            var prog = ProgramInstallRoot.TryExtract(executablePath);
            if (prog is not null
                && !string.IsNullOrWhiteSpace(prog.DisplayName)
                && !prog.DisplayName.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)
                && !prog.DisplayName.Contains("Common Files", StringComparison.OrdinalIgnoreCase))
            {
                // Prefer the product leaf of mega-vendor labels ("Autodesk / Revit" → "Revit").
                var display = prog.DisplayName;
                var slash = display.LastIndexOf('/');
                if (slash >= 0 && slash < display.Length - 1)
                    display = display[(slash + 1)..].Trim();
                return NormalizeLabel(display);
            }
        }

        return null;
    }

    private static IEnumerable<string> DeserializeHostnames(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            yield break;
        Dictionary<string, JsonElement>? map = null;
        try
        {
            map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOptions);
        }
        catch
        {
            yield break;
        }

        if (map is null)
            yield break;
        foreach (var key in map.Keys)
        {
            if (!string.IsNullOrWhiteSpace(key))
                yield return key.Trim();
        }
    }

    private static string Key(int machineId, string label) => $"{machineId}\0{label}";

    private static string NormalizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return "";
        var t = label.Trim();
        if (t.Length > 80)
            t = t[..80];
        return t;
    }
}
