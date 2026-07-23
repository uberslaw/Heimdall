using Heimdall.Api.Data;

namespace Heimdall.Api.Services;

/// <summary>
/// Region → Office → Machine hierarchy. MachineGroup "Region/Office" or plain "POC" maps into the tree.
/// </summary>
public static class MachineHierarchy
{
    public const string DefaultRegion = "POC";
    public const string DefaultOffice = "Local";

    public static (string Region, string Office) ResolveLocation(string? machineGroup, string? region, string? office)
    {
        if (!string.IsNullOrWhiteSpace(region) && !string.IsNullOrWhiteSpace(office))
            return (region.Trim(), office.Trim());

        if (!string.IsNullOrWhiteSpace(machineGroup))
        {
            var parsed = ParseGroup(machineGroup);
            return (
                string.IsNullOrWhiteSpace(region) ? parsed.Region : region.Trim(),
                string.IsNullOrWhiteSpace(office) ? parsed.Office : office.Trim()
            );
        }

        return (
            string.IsNullOrWhiteSpace(region) ? DefaultRegion : region.Trim(),
            string.IsNullOrWhiteSpace(office) ? DefaultOffice : office.Trim()
        );
    }

    public static (string Region, string Office) ParseGroup(string machineGroup)
    {
        var g = machineGroup.Trim();
        var slash = g.IndexOf('/');
        if (slash > 0 && slash < g.Length - 1)
            return (g[..slash].Trim(), g[(slash + 1)..].Trim());

        // Plain group (e.g. "POC") → Region = value, Office = Local
        if (string.Equals(g, DefaultRegion, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(g, "Local", StringComparison.OrdinalIgnoreCase))
            return (DefaultRegion, DefaultOffice);

        return (DefaultRegion, g);
    }

    public static void ApplyToMachine(Machine machine, string? machineGroup = null)
    {
        if (!string.IsNullOrWhiteSpace(machineGroup))
            machine.MachineGroup = machineGroup.Trim();

        var (region, office) = ResolveLocation(machine.MachineGroup, machine.Region, machine.Office);
        machine.Region = region;
        machine.Office = office;
        if (string.IsNullOrWhiteSpace(machine.Country))
            machine.Country = DeriveCountry(region);
    }

    public static void EnsureDefaults(Machine machine)
    {
        if (string.IsNullOrWhiteSpace(machine.Region) || string.IsNullOrWhiteSpace(machine.Office))
            ApplyToMachine(machine);
        else if (string.IsNullOrWhiteSpace(machine.Country))
            machine.Country = DeriveCountry(machine.Region!);
    }

    /// <summary>POC country mapping from region (e.g. APAC → Australia).</summary>
    public static string DeriveCountry(string region)
    {
        if (string.Equals(region, "APAC", StringComparison.OrdinalIgnoreCase))
            return "Australia";
        if (string.Equals(region, "EMEA", StringComparison.OrdinalIgnoreCase))
            return "United Kingdom";
        if (string.Equals(region, "AMER", StringComparison.OrdinalIgnoreCase))
            return "United States";
        if (string.Equals(region, DefaultRegion, StringComparison.OrdinalIgnoreCase))
            return "Local";
        return region;
    }

    public static IReadOnlyList<RegionNode> BuildTree(IEnumerable<Machine> machines)
    {
        var list = machines.ToList();
        foreach (var m in list)
            EnsureDefaults(m);

        return list
            .GroupBy(m => m.Region!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key)
            .Select(regionGroup => new RegionNode(
                regionGroup.Key,
                regionGroup
                    .GroupBy(m => m.Office!, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g.Key)
                    .Select(officeGroup => new OfficeNode(
                        officeGroup.Key,
                        officeGroup.OrderBy(m => m.Hostname, StringComparer.OrdinalIgnoreCase).ToList()
                    ))
                    .ToList()
            ))
            .ToList();
    }

    public record RegionNode(string Name, IReadOnlyList<OfficeNode> Offices);
    public record OfficeNode(string Name, IReadOnlyList<Machine> Machines);
}
