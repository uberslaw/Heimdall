using System.Text.Json;
using Heimdall.Api.Data;
using Heimdall.Shared;
using Heimdall.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

public class IngestService(HeimdallDbContext db, AppListService appLists, IConfiguration configuration)
{
    public async Task IngestAsync(IngestBatchDto batch, CancellationToken ct)
    {
        Machine? machine = null;
        var isNewMachine = false;

        if (batch.Heartbeat is not null)
        {
            (machine, isNewMachine) = await UpsertMachineAsync(batch.Heartbeat, ct);
        }

        foreach (var session in batch.Sessions)
        {
            machine ??= await EnsureMachineAsync(session.Hostname, ct);
            await UpsertSessionAsync(machine, session, ct);
        }

        foreach (var run in batch.ProcessRuns)
        {
            machine ??= await EnsureMachineAsync(run.Hostname, ct);
            await UpsertProcessRunAsync(machine, run, ct);
        }

        if (batch.DiscoveredProcesses.Count > 0)
        {
            machine ??= batch.Heartbeat is not null
                ? await db.Machines.FirstOrDefaultAsync(m => m.Hostname == batch.Heartbeat.Hostname, ct)
                : null;
            if (machine is not null)
            {
                // Inventory received — run analysis into PendingApproval (does not auto-track).
                await db.SaveChangesAsync(ct);
                await appLists.AnalyzeMachineAsync(machine.Hostname, batch.DiscoveredProcesses, requestAgentInventoryIfEmpty: false, ct);
                return;
            }
        }

        await db.SaveChangesAsync(ct);

        if (machine is not null && (isNewMachine || machine.AppsAnalyzedAt is null && machine.AppAnalysisStatus == AppAnalysisStatus.None))
        {
            // Reload tracked entity in case SaveChanges detached state
            var tracked = await db.Machines.FirstAsync(m => m.Id == machine.Id, ct);
            await appLists.QueueFirstSeenAnalysisAsync(tracked, ct);
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<(Machine Machine, bool IsNew)> UpsertMachineAsync(HeartbeatDto heartbeat, CancellationToken ct)
    {
        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Hostname == heartbeat.Hostname, ct);
        var isNew = machine is null;
        if (machine is null)
        {
            machine = new Machine
            {
                Hostname = heartbeat.Hostname,
                FirstSeenUtc = heartbeat.TimestampUtc,
                LastSeenUtc = heartbeat.TimestampUtc,
                PendingAppAnalysis = true
            };
            db.Machines.Add(machine);
        }

        machine.LastSeenUtc = heartbeat.TimestampUtc;
        machine.IsInUse = heartbeat.IsInUse;
        machine.OsVersion = heartbeat.OsVersion ?? machine.OsVersion;
        machine.AgentVersion = heartbeat.AgentVersion;
        if (!string.IsNullOrWhiteSpace(heartbeat.MachineGroup))
            MachineHierarchy.ApplyToMachine(machine, heartbeat.MachineGroup);
        else
            MachineHierarchy.EnsureDefaults(machine);

        ApplyIdentityFromHeartbeat(machine, heartbeat, isNew);
        ApplyHardwareFromHeartbeat(machine, heartbeat);

        return (machine, isNew);
    }

    /// <summary>
    /// MachineGuid changes on OS reimage; SmbiosUuid usually survives. Same hostname + new Guid → Reimaged.
    /// Fresh agent install alone (same Guid) is not a reimage.
    /// </summary>
    private void ApplyIdentityFromHeartbeat(Machine machine, HeartbeatDto heartbeat, bool isNew)
    {
        var newGuid = NullIfEmpty(heartbeat.MachineGuid);
        var newUuid = NullIfEmpty(heartbeat.SmbiosUuid);

        if (isNew)
        {
            machine.MachineGuid = newGuid;
            machine.SmbiosUuid = newUuid ?? machine.SmbiosUuid;
            if (newGuid is not null || newUuid is not null)
            {
                db.MachineIdentityEvents.Add(new MachineIdentityEvent
                {
                    Machine = machine,
                    EventType = "FirstSeen",
                    NewMachineGuid = newGuid,
                    NewSmbiosUuid = newUuid,
                    ObservedAtUtc = heartbeat.TimestampUtc,
                    Detail = "Initial identity from agent heartbeat"
                });
            }
            return;
        }

        var oldGuid = machine.MachineGuid;
        var oldUuid = machine.SmbiosUuid;

        if (!string.IsNullOrWhiteSpace(newGuid) &&
            !string.IsNullOrWhiteSpace(oldGuid) &&
            !string.Equals(oldGuid, newGuid, StringComparison.OrdinalIgnoreCase))
        {
            machine.LastReimagedUtc = heartbeat.TimestampUtc;
            db.MachineIdentityEvents.Add(new MachineIdentityEvent
            {
                Machine = machine,
                EventType = "Reimaged",
                OldMachineGuid = oldGuid,
                NewMachineGuid = newGuid,
                OldSmbiosUuid = oldUuid,
                NewSmbiosUuid = newUuid ?? oldUuid,
                ObservedAtUtc = heartbeat.TimestampUtc,
                Detail = "MachineGuid changed for same hostname (OS reimage)"
            });
            machine.MachineGuid = newGuid;
        }
        else if (string.IsNullOrWhiteSpace(oldGuid) && newGuid is not null)
        {
            machine.MachineGuid = newGuid;
        }

        if (!string.IsNullOrWhiteSpace(newUuid))
        {
            if (!string.IsNullOrWhiteSpace(oldUuid) &&
                !string.Equals(oldUuid, newUuid, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(oldGuid, newGuid, StringComparison.OrdinalIgnoreCase))
            {
                // Unusual: SMBIOS UUID changed without MachineGuid — record but do not call reimage
                db.MachineIdentityEvents.Add(new MachineIdentityEvent
                {
                    Machine = machine,
                    EventType = "UuidChanged",
                    OldMachineGuid = machine.MachineGuid,
                    NewMachineGuid = machine.MachineGuid,
                    OldSmbiosUuid = oldUuid,
                    NewSmbiosUuid = newUuid,
                    ObservedAtUtc = heartbeat.TimestampUtc,
                    Detail = "SmbiosUuid changed without MachineGuid change"
                });
            }
            machine.SmbiosUuid = newUuid;
        }
    }

    /// <summary>
    /// Agent fills blank hardware fields only. Manual Cost-page edits set HardwareManualOverride and win.
    /// Identity / OS-date / serial preference fields still update when not manually overridden for hardware strings.
    /// </summary>
    private void ApplyHardwareFromHeartbeat(Machine machine, HeartbeatDto heartbeat)
    {
        // Always refresh OS install signals when blank or agent has newer empty-safe values (not under hardware override for dates)
        if (heartbeat.OsInstallDateUtc is DateTimeOffset osInstall)
            machine.OsInstallDateUtc ??= osInstall;
        if (heartbeat.WindowsFolderCreatedUtc is DateTimeOffset winFolder)
            machine.WindowsFolderCreatedUtc ??= winFolder;

        // Hostname parse on API side as well (config pattern) so older agents still benefit
        var pattern = configuration["Heimdall:HostnameSerialPattern"];
        var hostParse = HostnameSerialParser.Parse(heartbeat.Hostname, pattern);
        if (hostParse.Matched)
        {
            machine.HostnameCityCode ??= hostParse.CityCode;
            machine.HostnameChassisHint ??= hostParse.ChassisHint;
            if (string.IsNullOrWhiteSpace(machine.AssetSerial) && !string.IsNullOrWhiteSpace(hostParse.AssetSerial))
                machine.AssetSerial = hostParse.AssetSerial;
        }

        if (!string.IsNullOrWhiteSpace(heartbeat.HostnameCityCode))
            machine.HostnameCityCode ??= heartbeat.HostnameCityCode.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(heartbeat.HostnameChassisHint))
            machine.HostnameChassisHint ??= heartbeat.HostnameChassisHint.Trim().ToUpperInvariant();

        if (machine.HardwareManualOverride)
            return;

        if (string.IsNullOrWhiteSpace(machine.BiosSerial) && !string.IsNullOrWhiteSpace(heartbeat.BiosSerial))
            machine.BiosSerial = heartbeat.BiosSerial.Trim();

        var assetFromHb = NullIfEmpty(heartbeat.AssetSerial) ?? hostParse.AssetSerial;
        if (string.IsNullOrWhiteSpace(machine.AssetSerial) && assetFromHb is not null)
            machine.AssetSerial = assetFromHb;

        var preferred = HostnameSerialParser.PreferAssetSerial(
            machine.BiosSerial ?? heartbeat.BiosSerial,
            machine.AssetSerial ?? assetFromHb,
            hostParse.Matched || !string.IsNullOrWhiteSpace(heartbeat.AssetSerial));

        if (string.IsNullOrWhiteSpace(machine.HardwareSerialNumber) && preferred is not null)
            machine.HardwareSerialNumber = preferred;
        else if (!string.IsNullOrWhiteSpace(preferred) &&
                 HostnameSerialParser.IsGenericBiosSerial(machine.HardwareSerialNumber) &&
                 !HostnameSerialParser.IsGenericBiosSerial(preferred))
            machine.HardwareSerialNumber = preferred;

        if (string.IsNullOrWhiteSpace(machine.HardwareBrand) && !string.IsNullOrWhiteSpace(heartbeat.HardwareBrand))
            machine.HardwareBrand = heartbeat.HardwareBrand.Trim();
        if (string.IsNullOrWhiteSpace(machine.HardwareModel) && !string.IsNullOrWhiteSpace(heartbeat.HardwareModel))
            machine.HardwareModel = heartbeat.HardwareModel.Trim();
        if (string.IsNullOrWhiteSpace(machine.HardwareCpu) && !string.IsNullOrWhiteSpace(heartbeat.HardwareCpu))
            machine.HardwareCpu = heartbeat.HardwareCpu.Trim();
        if (string.IsNullOrWhiteSpace(machine.HardwareGpu) && !string.IsNullOrWhiteSpace(heartbeat.HardwareGpu))
            machine.HardwareGpu = heartbeat.HardwareGpu.Trim();
        if (machine.HardwareRamGb is null && heartbeat.HardwareRamGb is > 0)
            machine.HardwareRamGb = heartbeat.HardwareRamGb;
        if (machine.HardwareDiskGb is null && heartbeat.HardwareDiskGb is > 0)
            machine.HardwareDiskGb = heartbeat.HardwareDiskGb;
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private async Task<Machine> EnsureMachineAsync(string hostname, CancellationToken ct)
    {
        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Hostname == hostname, ct);
        if (machine is not null)
        {
            MachineHierarchy.EnsureDefaults(machine);
            return machine;
        }

        var now = DateTimeOffset.UtcNow;
        machine = new Machine
        {
            Hostname = hostname,
            FirstSeenUtc = now,
            LastSeenUtc = now,
            Region = MachineHierarchy.DefaultRegion,
            Office = MachineHierarchy.DefaultOffice,
            MachineGroup = $"{MachineHierarchy.DefaultRegion}/{MachineHierarchy.DefaultOffice}"
        };
        db.Machines.Add(machine);
        return machine;
    }

    private async Task UpsertSessionAsync(Machine machine, SessionEventDto dto, CancellationToken ct)
    {
        var username = WindowsAccountEncoding.RepairAccountField(dto.Username) ?? dto.Username;
        var domain = WindowsAccountEncoding.RepairAccountField(dto.Domain);
        var clientName = WindowsAccountEncoding.RepairAccountField(dto.ClientName);

        var existing = await db.Sessions.FirstOrDefaultAsync(s => s.ExternalEventId == dto.EventId, ct);
        if (existing is null)
        {
            existing = new UserSession
            {
                ExternalEventId = dto.EventId,
                Machine = machine,
                SessionId = dto.SessionId,
                Username = username,
                Domain = domain,
                SessionType = dto.SessionType,
                State = dto.State,
                StartedAtUtc = dto.StartedAtUtc ?? dto.ObservedAtUtc,
                EndedAtUtc = dto.EndedAtUtc,
                LastObservedUtc = dto.ObservedAtUtc,
                ClientName = clientName,
                ClientAddress = dto.ClientAddress,
                ActiveSeconds = dto.ActiveSeconds,
                DisconnectedSeconds = dto.DisconnectedSeconds
            };
            db.Sessions.Add(existing);
            return;
        }

        existing.State = dto.State;
        existing.LastObservedUtc = dto.ObservedAtUtc;
        existing.EndedAtUtc = dto.EndedAtUtc ?? existing.EndedAtUtc;
        existing.ActiveSeconds = Math.Max(existing.ActiveSeconds, dto.ActiveSeconds);
        existing.DisconnectedSeconds = Math.Max(existing.DisconnectedSeconds, dto.DisconnectedSeconds);
        existing.ClientName = clientName ?? existing.ClientName;
        existing.ClientAddress = dto.ClientAddress ?? existing.ClientAddress;
        // Refresh identity when a fixed agent re-reports the same EventId, or when ingest can repair mojibake.
        if (!string.IsNullOrWhiteSpace(username)
            && (WindowsAccountEncoding.LooksLikeMojibakeAccount(existing.Username)
                || WindowsAccountEncoding.LooksLikeWindowsAccountToken(username)))
        {
            existing.Username = username;
            existing.Domain = domain ?? existing.Domain;
        }

        existing.SessionType = dto.SessionType;
    }

    private async Task UpsertProcessRunAsync(Machine machine, ProcessRunDto dto, CancellationToken ct)
    {
        var username = WindowsAccountEncoding.RepairAccountField(dto.Username) ?? dto.Username;
        var existing = await db.ProcessRuns.FirstOrDefaultAsync(p => p.ExternalRunId == dto.RunId, ct);
        if (existing is null)
        {
            db.ProcessRuns.Add(new ProcessRun
            {
                ExternalRunId = dto.RunId,
                Machine = machine,
                Username = username,
                ProcessName = dto.ProcessName,
                ExecutablePath = dto.ExecutablePath,
                ProcessId = dto.ProcessId,
                StartedAtUtc = dto.StartedAtUtc,
                EndedAtUtc = dto.EndedAtUtc,
                LastSeenAtUtc = dto.LastSeenAtUtc,
                SampleCount = dto.SampleCount,
                PeakCpuPercent = dto.PeakCpuPercent,
                PeakGpuPercent = dto.PeakGpuPercent,
                DiskReadBytes = dto.DiskReadBytes,
                DiskWriteBytes = dto.DiskWriteBytes
            });
            return;
        }

        existing.LastSeenAtUtc = dto.LastSeenAtUtc;
        existing.EndedAtUtc = dto.EndedAtUtc ?? existing.EndedAtUtc;
        existing.SampleCount = Math.Max(existing.SampleCount, dto.SampleCount);
        if (WindowsAccountEncoding.LooksLikeMojibakeAccount(existing.Username)
            || WindowsAccountEncoding.LooksLikeWindowsAccountToken(username))
            existing.Username = username;
        if (dto.PeakCpuPercent is double peak)
            existing.PeakCpuPercent = Math.Max(existing.PeakCpuPercent ?? 0, peak);
        if (dto.PeakGpuPercent is double gpu)
            existing.PeakGpuPercent = Math.Max(existing.PeakGpuPercent ?? 0, gpu);
        if (dto.DiskReadBytes is long read)
            existing.DiskReadBytes = Math.Max(existing.DiskReadBytes ?? 0, read);
        if (dto.DiskWriteBytes is long write)
            existing.DiskWriteBytes = Math.Max(existing.DiskWriteBytes ?? 0, write);
    }
}

public class ConfigService(HeimdallDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<AgentConfigDto> ResolveForHostAsync(string hostname, CancellationToken ct)
    {
        var machine = await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Hostname == hostname, ct);
        if (machine is not null)
            MachineHierarchy.EnsureDefaults(machine);

        var configs = await db.TrackingConfigs.AsNoTracking()
            .Where(c => c.IsEnabled)
            .ToListAsync(ct);

        var applicable = configs
            .Where(c => MatchesScope(c.Scope, c.ScopeValue, machine, hostname))
            .OrderByDescending(c => ScopeRank(c.Scope))
            .ThenByDescending(c => c.Priority)
            .ToList();

        var primary = applicable.FirstOrDefault() ?? CreateFallbackConfig();

        // Merge includes/excludes from all matching scopes (scoped Track Software entries + All).
        var include = new List<string>();
        var exclude = new List<string>();
        foreach (var cfg in applicable.AsEnumerable().Reverse())
        {
            foreach (var p in DeserializeList(cfg.IncludeProcessesJson))
                if (!include.Contains(p, StringComparer.OrdinalIgnoreCase))
                    include.Add(p);
            foreach (var p in DeserializeList(cfg.ExcludeProcessesJson))
                if (!exclude.Contains(p, StringComparer.OrdinalIgnoreCase))
                    exclude.Add(p);
        }

        if (applicable.Count == 0)
        {
            include = DeserializeList(primary.IncludeProcessesJson);
            exclude = DeserializeList(primary.ExcludeProcessesJson);
        }

        var knownApps = await db.KnownApps.AsNoTracking().Where(a => a.EnabledByDefault).ToListAsync(ct);
        foreach (var app in knownApps)
        {
            if (!include.Contains(app.ProcessName, StringComparer.OrdinalIgnoreCase))
                include.Add(app.ProcessName);
        }

        // Merge processes from AppLists assigned to scopes matching this host.
        // Pending analysis proposals are NOT included until approved.
        var appListAssignments = await db.AppListAssignments.AsNoTracking()
            .Include(a => a.AppList).ThenInclude(l => l.Entries)
            .Where(a => a.IsEnabled)
            .ToListAsync(ct);
        foreach (var assignment in appListAssignments)
        {
            if (!MatchesScope(assignment.Scope, assignment.ScopeValue, machine, hostname))
                continue;
            foreach (var entry in assignment.AppList.Entries)
            {
                if (!include.Contains(entry.ProcessName, StringComparer.OrdinalIgnoreCase))
                    include.Add(entry.ProcessName);
            }
        }

        // Apply active pauses from all applicable tracking configs.
        var now = DateTimeOffset.UtcNow;
        var applicableIds = applicable.Select(c => c.Id).Where(id => id > 0).ToList();
        // SQLite EF DateTimeOffset filters are unreliable — load then filter in memory.
        var activePauses = applicableIds.Count == 0
            ? []
            : (await db.ProcessPauses.AsNoTracking()
                .Where(p => applicableIds.Contains(p.TrackingConfigId))
                .ToListAsync(ct))
                .Where(p => p.PausedUntilUtc > now)
                .ToList();

        var pausedIncludes = activePauses
            .Where(p => p.ListKind == ProcessListKind.Include)
            .Select(p => p.ProcessName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pausedExcludes = activePauses
            .Where(p => p.ListKind == ProcessListKind.Exclude)
            .Select(p => p.ProcessName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (pausedIncludes.Count > 0)
            include = include.Where(p => !pausedIncludes.Contains(p)).ToList();
        if (pausedExcludes.Count > 0)
            exclude = exclude.Where(p => !pausedExcludes.Contains(p)).ToList();

        var metricPolicies = await db.MetricPolicies.AsNoTracking()
            .Where(p => p.IsEnabled)
            .ToListAsync(ct);

        var thresholds = ResolveMetricThresholds(metricPolicies, machine, hostname);
        var pauseDtos = activePauses
            .GroupBy(p => (p.ProcessName, p.ListKind), new PauseKeyComparer())
            .Select(g => g.OrderByDescending(x => x.PausedUntilUtc).First())
            .Select(p => new ProcessPauseDto
            {
                ProcessName = p.ProcessName,
                ListKind = p.ListKind.ToString(),
                PausedUntilUtc = p.PausedUntilUtc,
                Reason = p.Reason
            })
            .OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AgentConfigDto
        {
            ConfigVersion = primary.Id * 1000 + primary.SampleIntervalSeconds + include.Count + thresholds.Count + pauseDtos.Count,
            SampleIntervalSeconds = primary.SampleIntervalSeconds,
            UploadIntervalSeconds = primary.UploadIntervalSeconds,
            ConfigRefreshSeconds = primary.ConfigRefreshSeconds,
            MinCpuPercentToTrack = primary.MinCpuPercentToTrack,
            IncludeProcesses = include,
            ExcludeProcesses = exclude,
            KnownApps = knownApps.Select(a => new KnownAppDto
            {
                DisplayName = a.DisplayName,
                ProcessName = a.ProcessName,
                Enabled = a.EnabledByDefault && !pausedIncludes.Contains(a.ProcessName)
            }).ToList(),
            MetricThresholds = thresholds,
            ProcessPauses = pauseDtos,
            PendingAppAnalysis = machine?.PendingAppAnalysis == true
        };
    }

    private sealed class PauseKeyComparer : IEqualityComparer<(string ProcessName, ProcessListKind ListKind)>
    {
        public bool Equals((string ProcessName, ProcessListKind ListKind) x, (string ProcessName, ProcessListKind ListKind) y) =>
            x.ListKind == y.ListKind &&
            string.Equals(x.ProcessName, y.ProcessName, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string ProcessName, ProcessListKind ListKind) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ProcessName), obj.ListKind);
    }

    /// <summary>
    /// Adds a process to include lists for the given scopes (creates TrackingConfig rows as needed).
    /// </summary>
    public async Task TrackSoftwareAsync(
        string processName,
        string? displayName,
        string? executablePath,
        IEnumerable<(ConfigScope Scope, string ScopeValue)> scopes,
        CancellationToken ct)
    {
        var process = NormalizeProcessName(processName);
        if (process.Length == 0)
            return;

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            var existingApp = await db.KnownApps.FirstOrDefaultAsync(a => a.ProcessName == process, ct);
            if (existingApp is null)
            {
                db.KnownApps.Add(new KnownApp
                {
                    DisplayName = displayName.Trim(),
                    ProcessName = process,
                    EnabledByDefault = false // scoped tracking; not global default
                });
            }
        }

        // Optional path note stored as include entry "name|path" is overkill; keep process name only.
        // Path can be documented in config Name.
        var pathNote = string.IsNullOrWhiteSpace(executablePath) ? "" : $" ({executablePath.Trim()})";

        foreach (var (scope, scopeValue) in scopes.Distinct())
        {
            if (scope == ConfigScope.All)
            {
                var allCfg = await db.TrackingConfigs
                    .Where(c => c.Scope == ConfigScope.All && c.IsEnabled)
                    .OrderByDescending(c => c.Priority)
                    .FirstOrDefaultAsync(ct);
                if (allCfg is null)
                {
                    allCfg = CreateFallbackConfig();
                    allCfg.Id = 0;
                    db.TrackingConfigs.Add(allCfg);
                }
                AddInclude(allCfg, process);
                continue;
            }

            var value = scopeValue.Trim();
            var cfg = await db.TrackingConfigs.FirstOrDefaultAsync(c =>
                c.Scope == scope &&
                c.ScopeValue == value &&
                c.Name.StartsWith("Track:"), ct);

            if (cfg is null)
            {
                cfg = new TrackingConfig
                {
                    Name = $"Track:{process}{pathNote}",
                    Scope = scope,
                    ScopeValue = value,
                    Priority = ScopeRank(scope) * 10,
                    IsEnabled = true,
                    SampleIntervalSeconds = 30,
                    UploadIntervalSeconds = 60,
                    ConfigRefreshSeconds = 300,
                    IncludeProcessesJson = "[]",
                    ExcludeProcessesJson = "[]"
                };
                db.TrackingConfigs.Add(cfg);
            }
            else if (!cfg.Name.Contains(process, StringComparison.OrdinalIgnoreCase))
            {
                cfg.Name = $"Track:{process} +scoped";
            }

            AddInclude(cfg, process);
        }

        await db.SaveChangesAsync(ct);
    }

    private static void AddInclude(TrackingConfig cfg, string process)
    {
        var list = DeserializeList(cfg.IncludeProcessesJson);
        if (!list.Contains(process, StringComparer.OrdinalIgnoreCase))
            list.Add(process);
        cfg.IncludeProcessesJson = JsonSerializer.Serialize(list);
        cfg.IsEnabled = true;
    }

    public static bool MatchesScope(ConfigScope scope, string? scopeValue, Machine? machine, string hostname)
    {
        return scope switch
        {
            ConfigScope.All => true,
            ConfigScope.Machine => string.Equals(scopeValue, hostname, StringComparison.OrdinalIgnoreCase),
            ConfigScope.Group => machine?.MachineGroup is not null &&
                                 string.Equals(scopeValue, machine.MachineGroup, StringComparison.OrdinalIgnoreCase),
            ConfigScope.Region => machine?.Region is not null &&
                                  string.Equals(scopeValue, machine.Region, StringComparison.OrdinalIgnoreCase),
            ConfigScope.Country => machine?.Country is not null &&
                                   string.Equals(scopeValue, machine.Country, StringComparison.OrdinalIgnoreCase),
            ConfigScope.Office => machine is not null &&
                                  !string.IsNullOrWhiteSpace(machine.Region) &&
                                  !string.IsNullOrWhiteSpace(machine.Office) &&
                                  (string.Equals(scopeValue, $"{machine.Region}/{machine.Office}", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(scopeValue, machine.Office, StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }

    /// <summary>Higher wins. Machine &gt; Office &gt; Country &gt; Region/Group &gt; All.</summary>
    public static int ScopeRank(ConfigScope scope) => scope switch
    {
        ConfigScope.Machine => 50,
        ConfigScope.Office => 40,
        ConfigScope.Country => 35,
        ConfigScope.Region => 30,
        ConfigScope.Group => 30,
        ConfigScope.All => 10,
        _ => 0
    };

    private static List<MetricThresholdDto> ResolveMetricThresholds(
        List<MetricPolicy> policies, Machine? machine, string hostname)
    {
        var result = new List<MetricThresholdDto>();
        foreach (MetricType metric in Enum.GetValues<MetricType>())
        {
            var match = policies
                .Where(p => p.MetricType == metric && MatchesScope(p.Scope, p.ScopeValue, machine, hostname))
                .OrderByDescending(p => ScopeRank(p.Scope))
                .ThenByDescending(p => p.Id)
                .FirstOrDefault();

            if (match is null)
                continue;

            result.Add(new MetricThresholdDto
            {
                MetricType = metric.ToString(),
                Scope = match.Scope.ToString(),
                ScopeValue = match.ScopeValue,
                RamPercent = match.RamPercentThreshold,
                RamMb = match.RamMbThreshold,
                GpuPercent = match.GpuPercentThreshold,
                DiskReadMBps = match.DiskReadMBpsThreshold,
                DiskWriteMBps = match.DiskWriteMBpsThreshold,
                DiskCombinedMBps = match.DiskCombinedMBpsThreshold
            });
        }

        return result;
    }

    private static TrackingConfig CreateFallbackConfig() => new()
    {
        Id = 0,
        Name = "Built-in",
        Scope = ConfigScope.All,
        SampleIntervalSeconds = 30,
        UploadIntervalSeconds = 60,
        ConfigRefreshSeconds = 300,
        IncludeProcessesJson = """["revit","acad","excel","chrome","msedge","WINWORD","POWERPNT"]""",
        ExcludeProcessesJson = """["Idle","System","svchost","csrss","smss","wininit","services","lsass","fontdrvhost","RuntimeBroker","SearchHost","ShellExperienceHost"]"""
    };

    private static List<string> DeserializeList(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static string NormalizeProcessName(string value)
    {
        var s = value.Trim();
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            s = s[..^4];
        return s;
    }
}

public static class SeedData
{
    public static async Task EnsureSeededAsync(HeimdallDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        await EnsureSchemaPatchesAsync(db);

        if (!await db.KnownApps.AnyAsync())
        {
            db.KnownApps.AddRange(
                new KnownApp { DisplayName = "Revit", ProcessName = "Revit" },
                new KnownApp { DisplayName = "AutoCAD", ProcessName = "acad" },
                new KnownApp { DisplayName = "Navisworks", ProcessName = "roamer" },
                new KnownApp { DisplayName = "Rhino", ProcessName = "Rhino" },
                new KnownApp { DisplayName = "Excel", ProcessName = "EXCEL" },
                new KnownApp { DisplayName = "Word", ProcessName = "WINWORD" },
                new KnownApp { DisplayName = "PowerPoint", ProcessName = "POWERPNT" },
                new KnownApp { DisplayName = "Chrome", ProcessName = "chrome" },
                new KnownApp { DisplayName = "Edge", ProcessName = "msedge" },
                new KnownApp { DisplayName = "Teams", ProcessName = "ms-teams" }
            );
        }

        if (!await db.TrackingConfigs.AnyAsync())
        {
            db.TrackingConfigs.Add(new TrackingConfig
            {
                Name = "Default (all machines)",
                Scope = ConfigScope.All,
                Priority = 0,
                IsEnabled = true,
                SampleIntervalSeconds = 30,
                UploadIntervalSeconds = 60,
                ConfigRefreshSeconds = 300,
                MinCpuPercentToTrack = 0,
                IncludeProcessesJson = "[]",
                ExcludeProcessesJson = """["Idle","System","svchost","csrss","smss","wininit","services","lsass","fontdrvhost","RuntimeBroker","SearchHost","ShellExperienceHost","dwm","conhost"]"""
            });
        }

        if (!await db.SoeApps.AnyAsync())
        {
            db.SoeApps.AddRange(SoeCatalog.CreateSeedEntities());
        }
        else
        {
            // Merge any newly added catalog entries without wiping admin customizations
            var existing = await db.SoeApps.Select(s => s.ProcessName).ToListAsync();
            var existingSet = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var seed in SoeCatalog.CreateSeedEntities())
            {
                if (!existingSet.Contains(seed.ProcessName))
                    db.SoeApps.Add(seed);
            }
        }

        // Backfill Region/Office on existing machines
        var machines = await db.Machines.ToListAsync();
        foreach (var m in machines)
            MachineHierarchy.EnsureDefaults(m);

        // Demo hierarchy if we have no machines yet — seed placeholder hosts for empty tree UX
        if (!machines.Any())
        {
            var now = DateTimeOffset.UtcNow;
            db.Machines.AddRange(
                new Machine
                {
                    Hostname = "DEMO-SYD-01",
                    MachineGroup = "APAC/Sydney",
                    Region = "APAC",
                    Office = "Sydney",
                    Country = "Australia",
                    FirstSeenUtc = now,
                    LastSeenUtc = now.AddMinutes(-2),
                    IsInUse = false,
                    AgentVersion = "seed"
                },
                new Machine
                {
                    Hostname = "DEMO-SYD-02",
                    MachineGroup = "APAC/Sydney",
                    Region = "APAC",
                    Office = "Sydney",
                    Country = "Australia",
                    FirstSeenUtc = now,
                    LastSeenUtc = now.AddMinutes(-5),
                    IsInUse = true,
                    AgentVersion = "seed"
                },
                new Machine
                {
                    Hostname = "DEMO-LON-01",
                    MachineGroup = "EMEA/London",
                    Region = "EMEA",
                    Office = "London",
                    Country = "United Kingdom",
                    FirstSeenUtc = now,
                    LastSeenUtc = now.AddMinutes(-1),
                    IsInUse = false,
                    AgentVersion = "seed"
                },
                new Machine
                {
                    Hostname = "DEMO-POC-01",
                    MachineGroup = "POC",
                    Region = MachineHierarchy.DefaultRegion,
                    Office = MachineHierarchy.DefaultOffice,
                    Country = "Local",
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                    IsInUse = false,
                    AgentVersion = "seed"
                }
            );
        }

        if (!await db.MetricPolicies.AnyAsync())
        {
            db.MetricPolicies.AddRange(
                new MetricPolicy
                {
                    Name = "Default high RAM",
                    MetricType = MetricType.HighRam,
                    Scope = ConfigScope.All,
                    IsEnabled = true,
                    RamPercentThreshold = 85,
                    RamMbThreshold = 16000
                },
                new MetricPolicy
                {
                    Name = "Default high GPU",
                    MetricType = MetricType.HighGpu,
                    Scope = ConfigScope.All,
                    IsEnabled = true,
                    GpuPercentThreshold = 90
                },
                new MetricPolicy
                {
                    Name = "Default high disk",
                    MetricType = MetricType.HighDisk,
                    Scope = ConfigScope.All,
                    IsEnabled = true,
                    DiskReadMBpsThreshold = 200,
                    DiskWriteMBpsThreshold = 200,
                    DiskCombinedMBpsThreshold = 350
                }
            );
        }

        if (!await db.UtilizationCriteria.AnyAsync())
        {
            db.UtilizationCriteria.Add(new UtilizationCriteria
            {
                Scope = "Global",
                WeightUsers = 25,
                WeightDailyUtil = 35,
                WeightMetricBusy = 20,
                WeightAppValue = 20,
                IdealMinUsers = 2,
                IdealDailyUtilPct = 40,
                WorkingHoursPerDay = 8,
                BusyCpuPercentThreshold = 25,
                BusyGpuPercentThreshold = 20,
                IdealMetricBusyPct = 15,
                IdealMaxCostPerHour = 50,
                HighScoreThreshold = 75,
                AdequateScoreThreshold = 50,
                MixedScoreThreshold = 30
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>SQLite EnsureCreated does not migrate; patch new columns/tables for existing POC DBs.</summary>
    private static async Task EnsureSchemaPatchesAsync(HeimdallDbContext db)
    {
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN Region TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN Office TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN Country TEXT NULL");
        await TryExec(db, "ALTER TABLE ProcessRuns ADD COLUMN PeakGpuPercent REAL NULL");
        await TryExec(db, "ALTER TABLE ProcessRuns ADD COLUMN DiskReadBytes INTEGER NULL");
        await TryExec(db, "ALTER TABLE ProcessRuns ADD COLUMN DiskWriteBytes INTEGER NULL");
        await TryExec(db, """
            CREATE TABLE IF NOT EXISTS MetricPolicies (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                MetricType INTEGER NOT NULL,
                Scope INTEGER NOT NULL,
                ScopeValue TEXT NULL,
                IsEnabled INTEGER NOT NULL,
                RamPercentThreshold REAL NULL,
                RamMbThreshold REAL NULL,
                DiskReadMBpsThreshold REAL NULL,
                DiskWriteMBpsThreshold REAL NULL,
                DiskCombinedMBpsThreshold REAL NULL,
                GpuPercentThreshold REAL NULL
            )
            """);
        await TryExec(db, """
            CREATE TABLE IF NOT EXISTS Teams (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Code TEXT NULL,
                ParentTeamId INTEGER NULL,
                FOREIGN KEY (ParentTeamId) REFERENCES Teams(Id)
            )
            """);
        await TryExec(db, """
            CREATE TABLE IF NOT EXISTS PersonTeams (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Username TEXT NOT NULL,
                Domain TEXT NULL,
                DisplayName TEXT NULL,
                Email TEXT NULL,
                TeamId INTEGER NOT NULL,
                FOREIGN KEY (TeamId) REFERENCES Teams(Id) ON DELETE CASCADE
            )
            """);
        await TryExec(db, """
            CREATE TABLE IF NOT EXISTS ProcessPauses (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TrackingConfigId INTEGER NOT NULL,
                ProcessName TEXT NOT NULL,
                ListKind INTEGER NOT NULL,
                PausedUntilUtc TEXT NOT NULL,
                Reason TEXT NULL,
                FOREIGN KEY (TrackingConfigId) REFERENCES TrackingConfigs(Id) ON DELETE CASCADE
            )
            """);
        await TryExec(db, """
            CREATE TABLE IF NOT EXISTS SoeApps (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                DisplayName TEXT NOT NULL,
                ProcessName TEXT NOT NULL,
                Category TEXT NOT NULL,
                Vendor TEXT NULL
            )
            """);
        await TryExec(db, """
            CREATE TABLE IF NOT EXISTS UtilizationCriteria (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Scope TEXT NOT NULL,
                ScopeValue TEXT NULL,
                WeightUsers REAL NOT NULL,
                WeightDailyUtil REAL NOT NULL,
                WeightMetricBusy REAL NOT NULL,
                WeightAppValue REAL NOT NULL,
                IdealMinUsers INTEGER NOT NULL,
                IdealDailyUtilPct REAL NOT NULL,
                WorkingHoursPerDay REAL NOT NULL,
                BusyCpuPercentThreshold REAL NOT NULL,
                BusyGpuPercentThreshold REAL NOT NULL,
                IdealMetricBusyPct REAL NOT NULL,
                IdealMaxCostPerHour REAL NOT NULL,
                HighScoreThreshold REAL NOT NULL,
                AdequateScoreThreshold REAL NOT NULL,
                MixedScoreThreshold REAL NOT NULL
            )
            """);
        await TryExec(db, """
            CREATE TABLE IF NOT EXISTS AppLicenseCosts (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProcessName TEXT NOT NULL,
                DisplayName TEXT NULL,
                LicenseCostPerYear REAL NOT NULL
            )
            """);
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN AppsAnalyzedAt TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN PendingAppAnalysis INTEGER NOT NULL DEFAULT 0");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN AppAnalysisStatus INTEGER NOT NULL DEFAULT 0");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN AppAnalysisProposalJson TEXT NULL");
        await TryExec(db, "UPDATE Machines SET AppAnalysisProposalJson = '[]' WHERE AppAnalysisProposalJson IS NULL");
        await TryExec(db, """
            CREATE TABLE IF NOT EXISTS AppLists (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                TeamId INTEGER NULL,
                Notes TEXT NULL,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                IsAutoDiscovered INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (TeamId) REFERENCES Teams(Id) ON DELETE SET NULL
            )
            """);
        await TryExec(db, """
            CREATE TABLE IF NOT EXISTS AppListEntries (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AppListId INTEGER NOT NULL,
                ProcessName TEXT NOT NULL,
                DisplayName TEXT NULL,
                FOREIGN KEY (AppListId) REFERENCES AppLists(Id) ON DELETE CASCADE
            )
            """);
        await TryExec(db, """
            CREATE TABLE IF NOT EXISTS AppListAssignments (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AppListId INTEGER NOT NULL,
                Scope INTEGER NOT NULL,
                ScopeValue TEXT NULL,
                Priority INTEGER NOT NULL DEFAULT 0,
                IsEnabled INTEGER NOT NULL DEFAULT 1,
                FOREIGN KEY (AppListId) REFERENCES AppLists(Id) ON DELETE CASCADE
            )
            """);
        await TryExec(db, """
            CREATE TABLE IF NOT EXISTS AppListAuditLogs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Utc TEXT NOT NULL,
                Action TEXT NOT NULL,
                AppListId INTEGER NULL,
                AppListName TEXT NULL,
                Scope INTEGER NULL,
                ScopeValue TEXT NULL,
                MachineHostname TEXT NULL,
                Detail TEXT NOT NULL,
                Actor TEXT NULL
            )
            """);

        // Cost / hardware inventory on Machines
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN PurchaseCost TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN PurchaseCurrency TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN WarrantyStartDate TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN WarrantyEndDate TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN HardwareGpu TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN HardwareCpu TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN HardwareRamGb REAL NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN HardwareDiskGb REAL NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN HardwareBrand TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN HardwareModel TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN HardwareSerialNumber TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN HardwareManualOverride INTEGER NOT NULL DEFAULT 0");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN BiosSerial TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN AssetSerial TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN HostnameCityCode TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN HostnameChassisHint TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN PsuWatts INTEGER NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN PowerDrawWatts INTEGER NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN SupportHourlyRate TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN OsInstallDateUtc TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN WindowsFolderCreatedUtc TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN MachineGuid TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN SmbiosUuid TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN LastReimagedUtc TEXT NULL");
        await TryExec(db, """
            CREATE TABLE IF NOT EXISTS MachineIdentityEvents (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                MachineId INTEGER NOT NULL,
                EventType TEXT NOT NULL,
                OldMachineGuid TEXT NULL,
                NewMachineGuid TEXT NULL,
                OldSmbiosUuid TEXT NULL,
                NewSmbiosUuid TEXT NULL,
                ObservedAtUtc TEXT NOT NULL,
                Detail TEXT NULL,
                FOREIGN KEY (MachineId) REFERENCES Machines(Id) ON DELETE CASCADE
            )
            """);
    }

    private static async Task TryExec(HeimdallDbContext db, string sql)
    {
        try { await db.Database.ExecuteSqlRawAsync(sql); }
        catch { /* column/table already exists */ }
    }
}
