using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Heimdall.Shared.Contracts;

namespace Heimdall.Agent.Collectors;

/// <summary>
/// Instantaneous CPU / GPU / RAM / disk snapshot + top-3-by-usage processes, used by Worker's live
/// resource-sampling loop (only runs while at least one Staff Access viewer needs this host — see
/// LiveSamplingService on the API side). Uses the WMI "Formatted" perf classes for CPU/RAM/disk
/// (already rate-calculated by WMI, so a single query per sample is enough) and the "GPU Engine"
/// performance-counter category for GPU, which needs a short priming delay per read (documented below).
/// Every query is best-effort: a failure degrades that one metric to null rather than throwing, so a
/// host without a dedicated GPU (or with WMI classes disabled by policy) still reports CPU/RAM/disk.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ResourceMetricsCollector
{
    private static readonly Regex GpuInstancePidRegex = new(@"pid_(\d+)_", RegexOptions.Compiled);

    public sealed record Sample(
        double? CpuPercent,
        double? GpuPercent,
        double? RamPercent,
        double? RamUsedGb,
        double? RamTotalGb,
        double? DiskReadBytesPerSec,
        double? DiskWriteBytesPerSec,
        IReadOnlyDictionary<string, ProcessUsage> ProcessesByName,
        double? GpuMemoryUsedMb = null,
        double? NetworkInBytesPerSec = null,
        double? NetworkOutBytesPerSec = null);

    public sealed record ProcessUsage(int ProcessId, double CpuPercent, double GpuPercent, double RamMb, double DiskReadBytesPerSec, double DiskWriteBytesPerSec);

    public static Sample Collect()
    {
        var processes = CollectPerProcess();
        var gpuByProcess = TryCollectGpuByProcess();
        foreach (var (name, gpu) in gpuByProcess)
        {
            if (processes.TryGetValue(name, out var existing))
                processes[name] = existing with { GpuPercent = existing.GpuPercent + gpu };
        }

        var (cpuTotal, ramPercent, ramUsedGb, ramTotalGb) = TryCollectSystemCpuRam();
        var (diskRead, diskWrite) = TryCollectDiskTotals();
        var gpuTotal = gpuByProcess.Count == 0 ? (double?)null : gpuByProcess.Values.DefaultIfEmpty(0).Max();
        var gpuMemMb = TryCollectGpuMemoryMb();
        var (netIn, netOut) = TryCollectNetworkTotals();

        return new Sample(cpuTotal, gpuTotal, ramPercent, ramUsedGb, ramTotalGb, diskRead, diskWrite, processes, gpuMemMb, netIn, netOut);
    }

    public static List<TopProcessSampleDto> TopByCpu(Sample s, int count) =>
        Top(s.ProcessesByName, count, p => p.CpuPercent);

    public static List<TopProcessSampleDto> TopByGpu(Sample s, int count) =>
        Top(s.ProcessesByName, count, p => p.GpuPercent);

    public static List<TopProcessSampleDto> TopByRam(Sample s, int count) =>
        Top(s.ProcessesByName, count, p => p.RamMb);

    public static List<TopProcessSampleDto> TopByDiskRead(Sample s, int count) =>
        Top(s.ProcessesByName, count, p => p.DiskReadBytesPerSec);

    public static List<TopProcessSampleDto> TopByDiskWrite(Sample s, int count) =>
        Top(s.ProcessesByName, count, p => p.DiskWriteBytesPerSec);

    /// <summary>Guarantees an entry for every requested favourite name that's currently running, regardless of rank.</summary>
    public static List<FavoriteProcessSampleDto> ResolveFavorites(Sample s, IEnumerable<string> favoriteNames)
    {
        var result = new List<FavoriteProcessSampleDto>();
        foreach (var name in favoriteNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!s.ProcessesByName.TryGetValue(name, out var p))
                continue;
            result.Add(new FavoriteProcessSampleDto
            {
                ProcessName = name,
                ProcessId = p.ProcessId,
                CpuPercent = Math.Round(p.CpuPercent, 1),
                GpuPercent = Math.Round(p.GpuPercent, 1),
                RamMb = Math.Round(p.RamMb, 1),
                DiskReadBytesPerSec = Math.Round(p.DiskReadBytesPerSec, 0),
                DiskWriteBytesPerSec = Math.Round(p.DiskWriteBytesPerSec, 0)
            });
        }
        return result;
    }

    private static List<TopProcessSampleDto> Top(
        IReadOnlyDictionary<string, ProcessUsage> processes, int count, Func<ProcessUsage, double> selector) =>
        processes
            .Select(kv => (Name: kv.Key, Usage: kv.Value, Value: selector(kv.Value)))
            .Where(x => x.Value > 0.01)
            .OrderByDescending(x => x.Value)
            .Take(count)
            .Select(x => new TopProcessSampleDto { ProcessName = x.Name, ProcessId = x.Usage.ProcessId, Value = Math.Round(x.Value, 1) })
            .ToList();

    /// <summary>Per-process CPU (normalized to 0-100 total-system, matching Task Manager's default), RAM (MB), disk bytes/sec via one WMI query.</summary>
    private static Dictionary<string, ProcessUsage> CollectPerProcess()
    {
        var result = new Dictionary<string, ProcessUsage>(StringComparer.OrdinalIgnoreCase);
        var coreCount = Math.Max(1, Environment.ProcessorCount);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT IDProcess, Name, PercentProcessorTime, WorkingSetPrivate, IOReadBytesPersec, IOWriteBytesPersec " +
                "FROM Win32_PerfFormattedData_PerfProc_Process");
            foreach (ManagementObject obj in searcher.Get())
            {
                try
                {
                    var name = StripInstanceSuffix(obj["Name"]?.ToString() ?? "");
                    if (name.Length == 0 || name.Equals("_Total", StringComparison.OrdinalIgnoreCase) || name.Equals("Idle", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var pid = ToInt(obj["IDProcess"]);
                    var cpuRaw = ToDouble(obj["PercentProcessorTime"]);
                    var cpu = cpuRaw / coreCount;
                    var ramMb = ToDouble(obj["WorkingSetPrivate"]) / (1024.0 * 1024.0);
                    var diskRead = ToDouble(obj["IOReadBytesPersec"]);
                    var diskWrite = ToDouble(obj["IOWriteBytesPersec"]);

                    if (result.TryGetValue(name, out var existing))
                    {
                        result[name] = existing with
                        {
                            CpuPercent = existing.CpuPercent + cpu,
                            RamMb = existing.RamMb + ramMb,
                            DiskReadBytesPerSec = existing.DiskReadBytesPerSec + diskRead,
                            DiskWriteBytesPerSec = existing.DiskWriteBytesPerSec + diskWrite
                        };
                    }
                    else
                    {
                        result[name] = new ProcessUsage(pid, cpu, 0, ramMb, diskRead, diskWrite);
                    }
                }
                catch { /* skip malformed row */ }
                finally { obj.Dispose(); }
            }
        }
        catch { /* WMI unavailable — return whatever we have (possibly empty) */ }

        return result;
    }

    /// <summary>
    /// GPU % per process name via the "GPU Engine" performance-counter category (Windows 10 2004+).
    /// Utilization Percentage is a PERF_100NSEC_TIMER counter — it needs a short elapsed time between
    /// construction and the read to compute a meaningful rate, so we prime with one throwaway read.
    /// Returns an empty map (not an error) on hosts without this category — GPU is reported as n/a.
    /// </summary>
    private static Dictionary<string, double> TryCollectGpuByProcess()
    {
        var byName = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        List<PerformanceCounter>? counters = null;
        try
        {
            if (!PerformanceCounterCategory.Exists("GPU Engine"))
                return byName;

            var category = new PerformanceCounterCategory("GPU Engine");
            var instanceNames = category.GetInstanceNames();
            if (instanceNames.Length == 0)
                return byName;

            counters = instanceNames
                .Select(n =>
                {
                    try { return new PerformanceCounter("GPU Engine", "Utilization Percentage", n, readOnly: true); }
                    catch { return null; }
                })
                .Where(c => c is not null)
                .Select(c => c!)
                .ToList();

            foreach (var c in counters) SafeNextValue(c);
            Thread.Sleep(60);

            var pids = new Dictionary<int, string>();
            foreach (var c in counters)
            {
                var value = SafeNextValue(c);
                if (value <= 0) continue;

                var match = GpuInstancePidRegex.Match(c.InstanceName);
                if (!match.Success) continue;
                var pid = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);

                if (!pids.TryGetValue(pid, out var name))
                {
                    name = TryGetProcessName(pid);
                    pids[pid] = name;
                }
                if (name.Length == 0) continue;

                byName[name] = byName.TryGetValue(name, out var existing) ? existing + value : value;
            }
        }
        catch { /* best-effort — GPU stays unavailable */ }
        finally
        {
            if (counters is not null)
                foreach (var c in counters) c.Dispose();
        }

        return byName;
    }

    private static (double? CpuPercent, double? RamPercent, double? RamUsedGb, double? RamTotalGb) TryCollectSystemCpuRam()
    {
        double? cpu = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name = '_Total'");
            foreach (ManagementObject obj in searcher.Get())
            {
                cpu = ToDouble(obj["PercentProcessorTime"]);
                obj.Dispose();
            }
        }
        catch { /* leave null */ }

        double? ramPercent = null, ramUsedGb = null, ramTotalGb = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                var totalKb = ToDouble(obj["TotalVisibleMemorySize"]);
                var freeKb = ToDouble(obj["FreePhysicalMemory"]);
                if (totalKb > 0)
                {
                    var usedKb = totalKb - freeKb;
                    ramPercent = Math.Round(usedKb / totalKb * 100.0, 1);
                    ramUsedGb = Math.Round(usedKb / (1024.0 * 1024.0), 1);
                    ramTotalGb = Math.Round(totalKb / (1024.0 * 1024.0), 1);
                }
                obj.Dispose();
            }
        }
        catch { /* leave null */ }

        return (cpu, ramPercent, ramUsedGb, ramTotalGb);
    }

    private static (double? Read, double? Write) TryCollectDiskTotals()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DiskReadBytesPersec, DiskWriteBytesPersec FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk WHERE Name = '_Total'");
            foreach (ManagementObject obj in searcher.Get())
            {
                var read = ToDouble(obj["DiskReadBytesPersec"]);
                var write = ToDouble(obj["DiskWriteBytesPersec"]);
                obj.Dispose();
                return (read, write);
            }
        }
        catch { /* leave null */ }

        return (null, null);
    }

    /// <summary>
    /// Best-effort GPU dedicated/local memory in use (MB). Tries GPU Adapter Memory perf counters first,
    /// then WMI GPUPerformanceCounters; returns null when unavailable.
    /// </summary>
    private static double? TryCollectGpuMemoryMb()
    {
        try
        {
            if (PerformanceCounterCategory.Exists("GPU Adapter Memory"))
            {
                var category = new PerformanceCounterCategory("GPU Adapter Memory");
                var instances = category.GetInstanceNames();
                double total = 0;
                var any = false;
                foreach (var instance in instances)
                {
                    try
                    {
                        using var c = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", instance, readOnly: true);
                        SafeNextValue(c);
                        Thread.Sleep(20);
                        var bytes = SafeNextValue(c);
                        if (bytes > 0)
                        {
                            total += bytes;
                            any = true;
                        }
                    }
                    catch { /* skip instance */ }
                }
                if (any)
                    return Math.Round(total / (1024.0 * 1024.0), 1);
            }
        }
        catch { /* fall through */ }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DedicatedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory");
            double total = 0;
            var any = false;
            foreach (ManagementObject obj in searcher.Get())
            {
                try
                {
                    var bytes = ToDouble(obj["DedicatedUsage"]);
                    if (bytes > 0)
                    {
                        total += bytes;
                        any = true;
                    }
                }
                finally { obj.Dispose(); }
            }
            if (any)
                return Math.Round(total / (1024.0 * 1024.0), 1);
        }
        catch { /* leave null */ }

        return null;
    }

    /// <summary>
    /// Sum BytesReceived/SentPerSec across non-loopback NICs via Win32_PerfFormattedData_Tcpip_NetworkInterface.
    /// </summary>
    private static (double? InBytesPerSec, double? OutBytesPerSec) TryCollectNetworkTotals()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, BytesReceivedPersec, BytesSentPersec FROM Win32_PerfFormattedData_Tcpip_NetworkInterface");
            double inbound = 0, outbound = 0;
            var any = false;
            foreach (ManagementObject obj in searcher.Get())
            {
                try
                {
                    var name = obj["Name"]?.ToString() ?? "";
                    if (name.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("isatap", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Teredo", StringComparison.OrdinalIgnoreCase))
                        continue;

                    inbound += ToDouble(obj["BytesReceivedPersec"]);
                    outbound += ToDouble(obj["BytesSentPersec"]);
                    any = true;
                }
                finally { obj.Dispose(); }
            }
            return any ? (inbound, outbound) : (null, null);
        }
        catch { /* leave null */ }

        return (null, null);
    }

    private static double SafeNextValue(PerformanceCounter c)
    {
        try { return c.NextValue(); }
        catch { return 0; }
    }

    private static string TryGetProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch { return ""; }
    }

    /// <summary>WMI's formatted process counters disambiguate same-named instances as "name#1", "name#2", ...</summary>
    private static string StripInstanceSuffix(string name)
    {
        var idx = name.LastIndexOf('#');
        if (idx <= 0) return name;
        return name[(idx + 1)..].All(char.IsDigit) ? name[..idx] : name;
    }

    private static int ToInt(object? value)
    {
        try { return value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    private static double ToDouble(object? value)
    {
        try { return value is null ? 0 : Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }
}
