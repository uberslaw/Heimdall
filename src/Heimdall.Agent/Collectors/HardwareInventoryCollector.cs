using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using Heimdall.Shared;
using Microsoft.Win32;

namespace Heimdall.Agent.Collectors;

/// <summary>
/// Best-effort WMI / registry hardware inventory for Cost page enrichment.
/// Call on a slow cadence (config refresh / daily), not every sample.
/// PSU rated wattage and live power draw are NOT available via WMI for desktops — enter PsuWatts manually on Cost.
/// </summary>
[SupportedOSPlatform("windows")]
public static class HardwareInventoryCollector
{
    public sealed record Snapshot(
        string? BiosSerial,
        string? AssetSerial,
        string? PreferredSerial,
        string? Brand,
        string? Model,
        string? Cpu,
        double? RamGb,
        double? DiskGb,
        string? Gpu,
        string? HostnameCityCode,
        string? HostnameChassisHint,
        string? MachineGuid,
        string? SmbiosUuid,
        DateTimeOffset? OsInstallDateUtc,
        DateTimeOffset? WindowsFolderCreatedUtc);

    public static Snapshot? TryCollect(string hostname, string? hostnameSerialPattern = null)
    {
        try
        {
            var rawBios = FirstRaw(
                QueryFirst("SELECT SerialNumber FROM Win32_BIOS", "SerialNumber"),
                QueryFirst("SELECT IdentifyingNumber FROM Win32_ComputerSystemProduct", "IdentifyingNumber"));

            var biosSerial = Clean(rawBios);
            if (HostnameSerialParser.IsGenericBiosSerial(biosSerial))
                biosSerial = null;

            var hostParse = HostnameSerialParser.Parse(hostname, hostnameSerialPattern);
            var preferred = HostnameSerialParser.PreferAssetSerial(
                biosSerial ?? rawBios,
                hostParse.AssetSerial,
                hostParse.Matched);

            var brand = QueryFirst("SELECT Manufacturer FROM Win32_ComputerSystem", "Manufacturer");
            var model = QueryFirst("SELECT Model FROM Win32_ComputerSystem", "Model");
            var cpu = QueryFirst("SELECT Name FROM Win32_Processor", "Name");
            var ramGb = TryRamGb();
            var diskGb = TryDiskGb();
            var gpu = TryGpuNames();
            var machineGuid = TryMachineGuid();
            var smbiosUuid = Clean(QueryFirst("SELECT UUID FROM Win32_ComputerSystemProduct", "UUID"));
            if (IsPlaceholderSmbiosUuid(smbiosUuid))
                smbiosUuid = null;

            var osInstall = TryOsInstallDateUtc();
            var winFolder = TryWindowsFolderCreatedUtc();

            return new Snapshot(
                biosSerial,
                hostParse.AssetSerial,
                preferred,
                Clean(brand),
                Clean(model),
                Clean(cpu),
                ramGb,
                diskGb,
                Clean(gpu),
                hostParse.CityCode,
                hostParse.ChassisHint,
                machineGuid,
                smbiosUuid,
                osInstall,
                winFolder);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPlaceholderSmbiosUuid(string? uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid))
            return true;
        var u = uuid.Trim();
        return u.Equals("00000000-0000-0000-0000-000000000000", StringComparison.OrdinalIgnoreCase)
               || u.Equals("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryMachineGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var value = key?.GetValue("MachineGuid")?.ToString();
            return Clean(value);
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? TryOsInstallDateUtc()
    {
        // WMI InstallDate often resets on feature updates — store separately from Windows folder created.
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT InstallDate FROM Win32_OperatingSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                var raw = obj["InstallDate"]?.ToString();
                var parsed = ParseCimDateTime(raw);
                if (parsed is not null)
                    return parsed;
            }
        }
        catch
        {
            // fall through to registry
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var install = key?.GetValue("InstallDate");
            if (install is int unix)
                return DateTimeOffset.FromUnixTimeSeconds(unix);
            if (install is long unixL)
                return DateTimeOffset.FromUnixTimeSeconds(unixL);
            if (install is not null && long.TryParse(install.ToString(), out var p))
                return DateTimeOffset.FromUnixTimeSeconds(p);
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static DateTimeOffset? TryWindowsFolderCreatedUtc()
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return null;
            var created = Directory.GetCreationTimeUtc(root);
            if (created.Year < 1990)
                return null;
            return new DateTimeOffset(DateTime.SpecifyKind(created, DateTimeKind.Utc));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parse WMI CIM_DATETIME (yyyyMMddHHmmss.ffffff±UUU).</summary>
    private static DateTimeOffset? ParseCimDateTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length < 14)
            return null;

        try
        {
            var dt = ManagementDateTimeConverter.ToDateTime(raw);
            return new DateTimeOffset(dt.ToUniversalTime());
        }
        catch
        {
            // Manual fallback
        }

        try
        {
            if (!DateTime.TryParseExact(raw[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var local))
                return null;
            return new DateTimeOffset(local.ToUniversalTime());
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

    /// <summary>Cheap per-upload logical disk free/used (fixed local drives only).</summary>
    public static IReadOnlyList<Heimdall.Shared.Contracts.DiskVolumeDto> TryCollectVolumes()
    {
        try
        {
            var list = new List<Heimdall.Shared.Contracts.DiskVolumeDto>();
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, VolumeName, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 3");
            foreach (ManagementObject obj in searcher.Get())
            {
                var id = obj["DeviceID"]?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(id)) continue;
                var size = ToUInt64(obj["Size"]);
                var free = ToUInt64(obj["FreeSpace"]);
                if (size == 0) continue;
                var totalGb = Math.Round(size / (1024.0 * 1024.0 * 1024.0), 1);
                var freeGb = Math.Round(free / (1024.0 * 1024.0 * 1024.0), 1);
                list.Add(new Heimdall.Shared.Contracts.DiskVolumeDto
                {
                    Name = id.TrimEnd('\\'),
                    Label = Clean(obj["VolumeName"]?.ToString()),
                    TotalGb = totalGb,
                    FreeGb = freeGb
                });
            }

            return list
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static ulong ToUInt64(object? value)
    {
        if (value is null) return 0;
        if (value is ulong u) return u;
        return ulong.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0;
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

    private static string? FirstRaw(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
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
