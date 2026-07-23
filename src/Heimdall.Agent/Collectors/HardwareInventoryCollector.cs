using System.Management;
using System.Runtime.Versioning;

namespace Heimdall.Agent.Collectors;

/// <summary>
/// Best-effort WMI hardware inventory for Cost page enrichment.
/// Call on a slow cadence (config refresh / daily), not every sample.
/// </summary>
[SupportedOSPlatform("windows")]
public static class HardwareInventoryCollector
{
    public sealed record Snapshot(
        string? SerialNumber,
        string? Brand,
        string? Model,
        string? Cpu,
        double? RamGb,
        double? DiskGb,
        string? Gpu);

    public static Snapshot? TryCollect()
    {
        try
        {
            var serial = FirstNonEmpty(
                QueryFirst("SELECT SerialNumber FROM Win32_BIOS", "SerialNumber"),
                QueryFirst("SELECT IdentifyingNumber FROM Win32_ComputerSystemProduct", "IdentifyingNumber"));

            var brand = QueryFirst("SELECT Manufacturer FROM Win32_ComputerSystem", "Manufacturer");
            var model = QueryFirst("SELECT Model FROM Win32_ComputerSystem", "Model");
            var cpu = QueryFirst("SELECT Name FROM Win32_Processor", "Name");
            var ramGb = TryRamGb();
            var diskGb = TryDiskGb();
            var gpu = TryGpuNames();

            return new Snapshot(
                Clean(serial),
                Clean(brand),
                Clean(model),
                Clean(cpu),
                ramGb,
                diskGb,
                Clean(gpu));
        }
        catch
        {
            return null;
        }
    }

    private static double? TryRamGb()
    {
        try
        {
            ulong total = 0;
            using var searcher = new ManagementObjectSearcher("SELECT Capacity FROM Win32_PhysicalMemory");
            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["Capacity"] is ulong u)
                    total += u;
                else if (obj["Capacity"] is not null && ulong.TryParse(obj["Capacity"].ToString(), out var p))
                    total += p;
            }

            if (total == 0)
                return null;

            return Math.Round(total / (1024.0 * 1024.0 * 1024.0), 1);
        }
        catch
        {
            return null;
        }
    }

    private static double? TryDiskGb()
    {
        try
        {
            // Prefer physical disks; fall back to fixed logical drives
            ulong total = 0;
            using (var searcher = new ManagementObjectSearcher("SELECT Size FROM Win32_DiskDrive WHERE MediaType IS NOT NULL"))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    if (obj["Size"] is ulong u)
                        total += u;
                    else if (obj["Size"] is not null && ulong.TryParse(obj["Size"].ToString(), out var p))
                        total += p;
                }
            }

            if (total == 0)
            {
                using var logical = new ManagementObjectSearcher(
                    "SELECT Size FROM Win32_LogicalDisk WHERE DriveType = 3");
                foreach (ManagementObject obj in logical.Get())
                {
                    if (obj["Size"] is ulong u)
                        total += u;
                    else if (obj["Size"] is not null && ulong.TryParse(obj["Size"].ToString(), out var p))
                        total += p;
                }
            }

            if (total == 0)
                return null;

            return Math.Round(total / (1024.0 * 1024.0 * 1024.0), 0);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGpuNames()
    {
        try
        {
            var names = new List<string>();
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = Clean(obj["Name"]?.ToString());
                if (name is null) continue;
                // Skip generic Microsoft Basic / Remote Desktop adapters when real GPU exists
                if (name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Remote Desktop", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    names.Add(name);
            }

            if (names.Count == 0)
                return null;

            return string.Join("; ", names);
        }
        catch
        {
            return null;
        }
    }

    private static string? QueryFirst(string wql, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(wql);
            foreach (ManagementObject obj in searcher.Get())
            {
                var v = Clean(obj[property]?.ToString());
                if (v is not null)
                    return v;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v) &&
                !v.Equals("To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase) &&
                !v.Equals("Default string", StringComparison.OrdinalIgnoreCase) &&
                !v.Equals("None", StringComparison.OrdinalIgnoreCase) &&
                !v.Equals("System Serial Number", StringComparison.OrdinalIgnoreCase))
                return v;
        }

        return null;
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var t = value.Trim();
        return t.Length == 0 ? null : t;
    }
}

