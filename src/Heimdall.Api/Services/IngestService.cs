using System.Text.Json;
using Heimdall.Api.Data;
using Heimdall.Shared;
using Heimdall.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

public class IngestService(HeimdallDbContext db, AppListService appLists, ProcessCatalogService catalog, IConfiguration configuration, RemoteMachineService remoteMachines, TuflowRunService tuflowRuns, ClientUpdateService clientUpdates)
{
    public async Task IngestAsync(IngestBatchDto batch, CancellationToken ct)
    {
        Machine? machine = null;
        var isNewMachine = false;
        var verifyRestartRdp = false;

        if (batch.Heartbeat is not null)
        {
            (machine, isNewMachine, verifyRestartRdp) = await UpsertMachineAsync(batch.Heartbeat, ct);
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

        if (batch.ProcessRuns.Count > 0 && machine is not null)
        {
            await catalog.UpsertAsync(
                batch.ProcessRuns
                    .Where(r => DiscoveryCatalogFilter.IsEligible(r.ProcessName, r.ExecutablePath))
                    .Select(r => new ProcessCatalogService.CatalogItem(
                        r.ProcessName, r.ExecutablePath, null)),
                machine.Hostname, "agent ingest", ct);
        }

        if (batch.DiscoveredProcesses.Count > 0)
        {
            machine ??= batch.Heartbeat is not null
                ? await db.Machines.FirstOrDefaultAsync(m => m.Hostname == batch.Heartbeat.Hostname, ct)
                : null;
            if (machine is not null)
            {
                // Inventory received — run analysis into PendingApproval (does not auto-track).
                // Strip TEMP/.tmp/non-exe before analysis so they never land in catalog or proposals.
                var eligibleInventory = batch.DiscoveredProcesses
                    .Where(p => DiscoveryCatalogFilter.IsEligible(p.ProcessName, p.ExecutablePath))
                    .ToList();
                await db.SaveChangesAsync(ct);
                await appLists.AnalyzeMachineAsync(machine.Hostname, eligibleInventory, requestAgentInventoryIfEmpty: false, ct);
                return;
            }
        }

        await db.SaveChangesAsync(ct);

        if (verifyRestartRdp && machine is not null)
            await remoteMachines.VerifyRestartRdpAsync(machine.Hostname, ct);

        if (machine is not null && (isNewMachine || machine.AppsAnalyzedAt is null && machine.AppAnalysisStatus == AppAnalysisStatus.None))
        {
            // Reload tracked entity in case SaveChanges detached state
            var tracked = await db.Machines.FirstAsync(m => m.Id == machine.Id, ct);
            await appLists.QueueFirstSeenAnalysisAsync(tracked, ct);
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<(Machine Machine, bool IsNew, bool VerifyRestartRdp)> UpsertMachineAsync(HeartbeatDto heartbeat, CancellationToken ct)
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
        var verifyRestartRdp = remoteMachines.ApplyHeartbeat(machine, heartbeat);
        await tuflowRuns.ApplyHeartbeatAsync(machine, heartbeat, ct);
        await clientUpdates.ApplyHeartbeatAsync(machine, heartbeat, ct);

        return (machine, isNew, verifyRestartRdp);
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

        // Agent buffers can emit the same EventId twice in one batch (periodic refresh across sample ticks).
        // DB lookup alone misses pending Added rows and causes UNIQUE constraint failures on SaveChanges.
        var existing = await db.Sessions.FirstOrDefaultAsync(s => s.ExternalEventId == dto.EventId, ct)
            ?? db.Sessions.Local.FirstOrDefault(s => s.ExternalEventId == dto.EventId);
        if (existing is null)
        {
            existing = new UserSession
            {
                ExternalEventId = dto.EventId,
                Machine = machine,
                SessionId = dto.SessionId,
                Username = username,
                Domain = domain,
                SessionType = CoerceSessionType(dto.SessionType, clientName, dto.ClientAddress),
                State = dto.State,
                StartedAtUtc = dto.StartedAtUtc ?? dto.ObservedAtUtc,
                EndedAtUtc = dto.EndedAtUtc,
                LastObservedUtc = dto.ObservedAtUtc,
                ClientName = clientName,
                ClientAddress = dto.ClientAddress,
                ActiveSeconds = dto.ActiveSeconds,
                DisconnectedSeconds = dto.DisconnectedSeconds,
                LocalActiveSeconds = dto.LocalActiveSeconds,
                LocalDisconnectedSeconds = dto.LocalDisconnectedSeconds,
                InboundRdpActiveSeconds = dto.InboundRdpActiveSeconds,
                InboundRdpDisconnectedSeconds = dto.InboundRdpDisconnectedSeconds
            };
            db.Sessions.Add(existing);
            return;
        }

        existing.State = dto.State;
        existing.LastObservedUtc = dto.ObservedAtUtc;
        existing.EndedAtUtc = dto.EndedAtUtc ?? existing.EndedAtUtc;
        existing.ActiveSeconds = Math.Max(existing.ActiveSeconds, dto.ActiveSeconds);
        existing.DisconnectedSeconds = Math.Max(existing.DisconnectedSeconds, dto.DisconnectedSeconds);
        existing.LocalActiveSeconds = Math.Max(existing.LocalActiveSeconds, dto.LocalActiveSeconds);
        existing.LocalDisconnectedSeconds = Math.Max(existing.LocalDisconnectedSeconds, dto.LocalDisconnectedSeconds);
        existing.InboundRdpActiveSeconds = Math.Max(existing.InboundRdpActiveSeconds, dto.InboundRdpActiveSeconds);
        existing.InboundRdpDisconnectedSeconds = Math.Max(existing.InboundRdpDisconnectedSeconds, dto.InboundRdpDisconnectedSeconds);
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

        // Agents that still clear SessionType on disconnect keep republishing Local; coerce from fingerprint.
        existing.SessionType = CoerceSessionType(
            dto.SessionType, existing.ClientName, existing.ClientAddress);
    }

    /// <summary>
    /// Agents may report Local after WTS clears client fields on disconnect while ClientName/Address remain.
    /// </summary>
    private static SessionType CoerceSessionType(SessionType reported, string? clientName, string? clientAddress)
    {
        if (reported == SessionType.Rdp)
            return SessionType.Rdp;

        if (!string.IsNullOrWhiteSpace(clientName))
            return SessionType.Rdp;

        if (string.IsNullOrWhiteSpace(clientAddress))
            return reported;

        var addr = clientAddress.Trim();
        return addr is "0.0.0.0" or "::" or "::1" ? reported : SessionType.Rdp;
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

        var primary = applicable.FirstOrDefault(c => !IsMachineOverrideConfig(c))
            ?? applicable.FirstOrDefault()
            ?? CreateFallbackConfig();

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

        var fleetSamplingEnabled = machine is not null
            && await db.FleetDashboardMachines.AsNoTracking()
                .AnyAsync(f => f.MachineId == machine.Id, ct);

        return new AgentConfigDto
        {
            ConfigVersion = primary.Id * 1000 + primary.SampleIntervalSeconds + include.Count + thresholds.Count + pauseDtos.Count
                + (fleetSamplingEnabled ? 17 : 0),
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
            PendingAppAnalysis = machine?.PendingAppAnalysis == true,
            PendingCommands = RemoteMachineService.DeserializeCommands(machine?.PendingCommandsJson),
            PendingTuflowStart = TuflowRunService.DeserializeStartRequest(machine?.PendingTuflowStartJson),
            PendingClientUpdate = ClientUpdateService.DeserializeRequest(machine?.PendingClientUpdateJson),
            FleetSamplingEnabled = fleetSamplingEnabled,
            FleetProcessNames = ["tuflow"]
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

    public const string MachineOverrideConfigPrefix = "Machine override:";

    public static bool IsMachineOverrideConfig(TrackingConfig cfg) =>
        cfg.Name.StartsWith(MachineOverrideConfigPrefix, StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<string>> GetMachineExcludeProcessesAsync(string hostname, CancellationToken ct)
    {
        var host = hostname.Trim();
        var cfg = await db.TrackingConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c =>
                c.Scope == ConfigScope.Machine &&
                c.ScopeValue == host &&
                c.IsEnabled &&
                c.Name.StartsWith(MachineOverrideConfigPrefix), ct);
        return cfg is null ? [] : DeserializeList(cfg.ExcludeProcessesJson);
    }

    /// <summary>Machine-scoped exclude list — does not modify global lists or app list assignments.</summary>
    public async Task SetMachineExcludeProcessesAsync(string hostname, IReadOnlyList<string> excludes, CancellationToken ct)
    {
        var host = hostname.Trim();
        var normalized = excludes
            .Select(NormalizeProcessName)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cfg = await db.TrackingConfigs.FirstOrDefaultAsync(c =>
            c.Scope == ConfigScope.Machine &&
            c.ScopeValue == host &&
            c.Name.StartsWith(MachineOverrideConfigPrefix), ct);

        if (normalized.Count == 0 && cfg is null)
            return;

        if (cfg is null)
        {
            var global = await db.TrackingConfigs.AsNoTracking()
                .Where(c => c.Scope == ConfigScope.All && c.IsEnabled)
                .OrderByDescending(c => c.Priority)
                .FirstOrDefaultAsync(ct);

            cfg = new TrackingConfig
            {
                Name = $"{MachineOverrideConfigPrefix}{host}",
                Scope = ConfigScope.Machine,
                ScopeValue = host,
                Priority = 0,
                IsEnabled = true,
                SampleIntervalSeconds = global?.SampleIntervalSeconds ?? 30,
                UploadIntervalSeconds = global?.UploadIntervalSeconds ?? 60,
                ConfigRefreshSeconds = global?.ConfigRefreshSeconds ?? 300,
                MinCpuPercentToTrack = global?.MinCpuPercentToTrack ?? 0,
                IncludeProcessesJson = "[]",
                ExcludeProcessesJson = "[]"
            };
            db.TrackingConfigs.Add(cfg);
        }

        cfg.ExcludeProcessesJson = JsonSerializer.Serialize(normalized);
        cfg.IsEnabled = normalized.Count > 0;
        await db.SaveChangesAsync(ct);
    }
}

public static class SeedData
{
    /// <summary>Placeholder hosts seeded only on a brand-new empty database (see DemoMachinesOffered flag).</summary>
    public static readonly string[] DemoHostnames =
    [
        "DEMO-SYD-01",
        "DEMO-SYD-02",
        "DEMO-LON-01",
        "DEMO-POC-01"
    ];

    private const string DemoMachinesOfferedFlag = "DemoMachinesOffered";

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
                new KnownApp { DisplayName = "Teams", ProcessName = "ms-teams" },
                new KnownApp { DisplayName = "Remote Desktop (mstsc)", ProcessName = "mstsc" },
                new KnownApp { DisplayName = "Remote Desktop (msrdc)", ProcessName = "msrdc" },
                new KnownApp { DisplayName = "Remote Desktop (msrdcw)", ProcessName = "msrdcw" }
            );
        }
        else
        {
            await EnsureKnownAppAsync(db, "Remote Desktop (mstsc)", "mstsc");
            await EnsureKnownAppAsync(db, "Remote Desktop (msrdc)", "msrdc");
            await EnsureKnownAppAsync(db, "Remote Desktop (msrdcw)", "msrdcw");
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

        // Demo hierarchy only once on a brand-new empty DB; never re-offer after removal (DemoMachinesOffered flag).
        if (!machines.Any() && !await HasSystemFlagAsync(db, DemoMachinesOfferedFlag))
        {
            var now = DateTimeOffset.UtcNow;
            db.Machines.AddRange(
                new Machine
                {
                    Hostname = DemoHostnames[0],
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
                    Hostname = DemoHostnames[1],
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
                    Hostname = DemoHostnames[2],
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
                    Hostname = DemoHostnames[3],
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
            await SetSystemFlagAsync(db, DemoMachinesOfferedFlag, "1");
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

        // One-shot / every-start cleanup: drop TEMP/.tmp/non-exe junk from discovery catalog.
        var catalogJunk = (await db.ProcessCatalogEntries.ToListAsync())
            .Where(e => DiscoveryCatalogFilter.IsIneligibleCatalogEntry(e.ProcessName, e.ExecutablePath))
            .ToList();
        if (catalogJunk.Count > 0)
            db.ProcessCatalogEntries.RemoveRange(catalogJunk);

        await db.SaveChangesAsync();
    }

    /// <summary>SQLite EnsureCreated does not migrate; patch new columns/tables for existing POC DBs.</summary>
    private static async Task EnsureSchemaPatchesAsync(HeimdallDbContext db)
    {
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN Region TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN Office TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN Country TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN PendingTuflowStartJson TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN TuflowRunStatusJson TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN PendingClientUpdateJson TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN ClientUpdateProgressJson TEXT NULL");
        await TryExec(db, "ALTER TABLE ProcessRuns ADD COLUMN PeakGpuPercent REAL NULL");
        await TryExec(db, "ALTER TABLE ProcessRuns ADD COLUMN DiskReadBytes INTEGER NULL");
        await TryExec(db, "ALTER TABLE ProcessRuns ADD COLUMN DiskWriteBytes INTEGER NULL");
        await TryExec(db, "ALTER TABLE Sessions ADD COLUMN LocalActiveSeconds INTEGER NOT NULL DEFAULT 0");
        await TryExec(db, "ALTER TABLE Sessions ADD COLUMN LocalDisconnectedSeconds INTEGER NOT NULL DEFAULT 0");
        await TryExec(db, "ALTER TABLE Sessions ADD COLUMN InboundRdpActiveSeconds INTEGER NOT NULL DEFAULT 0");
        await TryExec(db, "ALTER TABLE Sessions ADD COLUMN InboundRdpDisconnectedSeconds INTEGER NOT NULL DEFAULT 0");
        // Misclassified inbound RDP stored as Local (Console short-circuit) — flip type when remote client is present.
        // Time buckets stay 0 until a new agent republishes; Socratize falls back to SessionType for legacy rows.
        await TryExec(db, """
            UPDATE Sessions
            SET SessionType = 1
            WHERE SessionType = 0
              AND (
                    (ClientName IS NOT NULL AND TRIM(ClientName) != '')
                 OR (ClientAddress IS NOT NULL AND TRIM(ClientAddress) != ''
                     AND ClientAddress NOT IN ('0.0.0.0', '::', '::1'))
              )
            """);
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
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN DiscoveredInventoryJson TEXT NULL");
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
        await TryExec(db, """
            CREATE TABLE IF NOT EXISTS ProcessGroupAssignments (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProcessName TEXT NOT NULL,
                "Group" INTEGER NOT NULL,
                DisplayName TEXT NULL,
                UpdatedUtc TEXT NOT NULL
            )
            """);
        await TryExec(db, "CREATE UNIQUE INDEX IF NOT EXISTS IX_ProcessGroupAssignments_ProcessName ON ProcessGroupAssignments(ProcessName)");
        await TryExec(db, "CREATE INDEX IF NOT EXISTS IX_ProcessGroupAssignments_Group ON ProcessGroupAssignments(\"Group\")");
        await TryExec(db, "ALTER TABLE ProcessGroupAssignments ADD COLUMN Description TEXT NULL");

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
        await TryExec(db, """
            CREATE TABLE IF NOT EXISTS SystemFlags (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            )
            """);
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN LastIp TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN TermServiceStatus TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN TermServiceCheckedUtc TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN LastRdpProbeResultJson TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN LastRdpProbeUtc TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN LastPingResultJson TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN LastPingUtc TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN PendingCommandsJson TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN RestartRdsProgressJson TEXT NULL");

        // Staff Access / Remote Access Groups
        await TryExec(db, """
            CREATE TABLE IF NOT EXISTS RemoteAccessGroups (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                FavoritesOnly INTEGER NOT NULL DEFAULT 0,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL
            )
            """);
        await TryExec(db, """
            CREATE TABLE IF NOT EXISTS RemoteAccessGroupStaff (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GroupId INTEGER NOT NULL,
                Email TEXT NOT NULL,
                FOREIGN KEY (GroupId) REFERENCES RemoteAccessGroups(Id) ON DELETE CASCADE
            )
            """);
        await TryExec(db, "CREATE UNIQUE INDEX IF NOT EXISTS IX_RemoteAccessGroupStaff_Group_Email ON RemoteAccessGroupStaff(GroupId, Email)");
        await TryExec(db, "CREATE INDEX IF NOT EXISTS IX_RemoteAccessGroupStaff_Email ON RemoteAccessGroupStaff(Email)");
        await TryExec(db, """
            CREATE TABLE IF NOT EXISTS RemoteAccessGroupMachines (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GroupId INTEGER NOT NULL,
                Hostname TEXT NOT NULL,
                FOREIGN KEY (GroupId) REFERENCES RemoteAccessGroups(Id) ON DELETE CASCADE
            )
            """);
        await TryExec(db, "CREATE UNIQUE INDEX IF NOT EXISTS IX_RemoteAccessGroupMachines_Group_Host ON RemoteAccessGroupMachines(GroupId, Hostname)");
        await TryExec(db, "CREATE INDEX IF NOT EXISTS IX_RemoteAccessGroupMachines_Host ON RemoteAccessGroupMachines(Hostname)");
        await TryExec(db, "ALTER TABLE RemoteAccessGroupMachines ADD COLUMN FriendlyName TEXT NULL");
        await TryExec(db, """
            CREATE TABLE IF NOT EXISTS RemoteAccessFavoriteProcesses (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GroupId INTEGER NOT NULL,
                ProcessName TEXT NOT NULL,
                FOREIGN KEY (GroupId) REFERENCES RemoteAccessGroups(Id) ON DELETE CASCADE
            )
            """);
        await TryExec(db, "CREATE UNIQUE INDEX IF NOT EXISTS IX_RemoteAccessFavoriteProcesses_Group_Name ON RemoteAccessFavoriteProcesses(GroupId, ProcessName)");
        await TryExec(db, """
            CREATE TABLE IF NOT EXISTS RemoteAccessViewers (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GroupId INTEGER NOT NULL,
                ViewerId TEXT NOT NULL,
                Email TEXT NULL,
                LastHeartbeatUtc TEXT NOT NULL
            )
            """);
        await TryExec(db, "CREATE UNIQUE INDEX IF NOT EXISTS IX_RemoteAccessViewers_Group_Viewer ON RemoteAccessViewers(GroupId, ViewerId)");
        await TryExec(db, "CREATE INDEX IF NOT EXISTS IX_RemoteAccessViewers_Heartbeat ON RemoteAccessViewers(LastHeartbeatUtc)");
        await TryExec(db, """
            CREATE TABLE IF NOT EXISTS SessionDrilldownViewers (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Hostname TEXT NOT NULL,
                ViewerId TEXT NOT NULL,
                LastHeartbeatUtc TEXT NOT NULL
            )
            """);
        await TryExec(db, "CREATE UNIQUE INDEX IF NOT EXISTS IX_SessionDrilldownViewers_Host_Viewer ON SessionDrilldownViewers(Hostname, ViewerId)");
        await TryExec(db, "CREATE INDEX IF NOT EXISTS IX_SessionDrilldownViewers_Heartbeat ON SessionDrilldownViewers(LastHeartbeatUtc)");
        await TryExec(db, """
            CREATE TABLE IF NOT EXISTS MachineResourceMetrics (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                MachineId INTEGER NOT NULL,
                SampledAtUtc TEXT NOT NULL,
                IsCalibrationAverage INTEGER NOT NULL DEFAULT 0,
                CpuPercent REAL NULL,
                GpuPercent REAL NULL,
                RamPercent REAL NULL,
                RamUsedGb REAL NULL,
                RamTotalGb REAL NULL,
                DiskReadBytesPerSec REAL NULL,
                DiskWriteBytesPerSec REAL NULL,
                DiskReadLevel TEXT NOT NULL DEFAULT 'Low',
                DiskWriteLevel TEXT NOT NULL DEFAULT 'Low',
                TopCpuProcessesJson TEXT NOT NULL DEFAULT '[]',
                TopGpuProcessesJson TEXT NOT NULL DEFAULT '[]',
                TopRamProcessesJson TEXT NOT NULL DEFAULT '[]',
                TopDiskReadProcessesJson TEXT NOT NULL DEFAULT '[]',
                TopDiskWriteProcessesJson TEXT NOT NULL DEFAULT '[]',
                FavoriteProcessesJson TEXT NOT NULL DEFAULT '[]',
                FOREIGN KEY (MachineId) REFERENCES Machines(Id) ON DELETE CASCADE
            )
            """);
        await TryExec(db, "CREATE UNIQUE INDEX IF NOT EXISTS IX_MachineResourceMetrics_MachineId ON MachineResourceMetrics(MachineId)");
        await TryExec(db, """
            CREATE TABLE IF NOT EXISTS ProcessCatalogEntries (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProcessName TEXT NOT NULL,
                ExecutablePath TEXT NOT NULL DEFAULT '',
                DisplayName TEXT NULL,
                FileVersion TEXT NULL,
                ProductVersion TEXT NULL,
                CompanyName TEXT NULL,
                FileDescription TEXT NULL,
                FirstSeenUtc TEXT NOT NULL,
                LastSeenUtc TEXT NOT NULL,
                SeenCount INTEGER NOT NULL DEFAULT 1,
                FirstSeenHostname TEXT NULL,
                LastSeenHostname TEXT NULL,
                SuggestedGroup INTEGER NULL,
                SuggestionReason TEXT NULL
            )
            """);
        await TryExec(db, "CREATE UNIQUE INDEX IF NOT EXISTS IX_ProcessCatalogEntries_Name_Path ON ProcessCatalogEntries(ProcessName, ExecutablePath)");
        await TryExec(db, "CREATE INDEX IF NOT EXISTS IX_ProcessCatalogEntries_Company ON ProcessCatalogEntries(CompanyName)");
        await TryExec(db, "ALTER TABLE ProcessCatalogEntries ADD COLUMN SeenHostnamesJson TEXT NULL");
        await TryExec(db, "ALTER TABLE ProcessCatalogEntries ADD COLUMN Ignored INTEGER NOT NULL DEFAULT 0");
        await TryExec(db, "ALTER TABLE ProcessCatalogEntries ADD COLUMN ManualVersion TEXT NULL");
        await TryExec(db, "ALTER TABLE ProcessCatalogEntries ADD COLUMN Description TEXT NULL");
        await TryExec(db, "ALTER TABLE ProcessCatalogEntries ADD COLUMN Category TEXT NULL");
        await TryExec(db, "ALTER TABLE ProcessCatalogEntries ADD COLUMN Subcategory TEXT NULL");

        // Custom UI themes (Theme page) — full colour/font token model layered on a built-in base preset.
        await TryExec(db, """
            CREATE TABLE IF NOT EXISTS CustomThemes (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                BasePreset TEXT NOT NULL DEFAULT 'cosmic',
                PrimaryHex TEXT NOT NULL DEFAULT '#d4b86a',
                SecondaryHex TEXT NOT NULL DEFAULT '#c8ced8',
                AccentHex TEXT NOT NULL DEFAULT '#a88838',
                TextHex TEXT NOT NULL DEFAULT '#eef1f8',
                MutedHex TEXT NOT NULL DEFAULT '#a4aec4',
                PanelHex TEXT NOT NULL DEFAULT '#0a0e1a',
                PanelOpacity REAL NOT NULL DEFAULT 0.72,
                PanelAltHex TEXT NOT NULL DEFAULT '#0e1220',
                PanelAltOpacity REAL NOT NULL DEFAULT 0.80,
                HeaderBgHex TEXT NOT NULL DEFAULT '#060810',
                HeaderBgOpacity REAL NOT NULL DEFAULT 0.72,
                BorderHex TEXT NOT NULL DEFAULT '#c0c6d0',
                BorderOpacity REAL NOT NULL DEFAULT 0.16,
                GoldHex TEXT NOT NULL DEFAULT '#d4b86a',
                ShadeHex TEXT NOT NULL DEFAULT '#ffecbe',
                ShadeOpacityPercent REAL NOT NULL DEFAULT 12,
                HoverHex TEXT NOT NULL DEFAULT '#ecd898',
                BackgroundHex TEXT NOT NULL DEFAULT '#060810',
                BackgroundImagePath TEXT NULL,
                BackgroundOverlayOpacity REAL NOT NULL DEFAULT 0.38,
                HeadingFont TEXT NULL,
                BodyFont TEXT NULL,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL
            )
            """);
        await TryExec(db, "CREATE INDEX IF NOT EXISTS IX_CustomThemes_Name ON CustomThemes(Name)");

        // Historical Dashboard (TUFLOW fleet POC)
        await TryExec(db, """
            CREATE TABLE IF NOT EXISTS FleetDashboardMachines (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                MachineId INTEGER NOT NULL,
                AddedUtc TEXT NOT NULL,
                Notes TEXT NULL,
                FOREIGN KEY (MachineId) REFERENCES Machines(Id) ON DELETE CASCADE
            )
            """);
        await TryExec(db, "CREATE UNIQUE INDEX IF NOT EXISTS IX_FleetDashboardMachines_MachineId ON FleetDashboardMachines(MachineId)");
        await TryExec(db, """
            CREATE TABLE IF NOT EXISTS FleetMetricSnapshots (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SampledAtUtc TEXT NOT NULL,
                MachineId INTEGER NOT NULL,
                Username TEXT NULL,
                TuflowRunning INTEGER NOT NULL DEFAULT 0,
                CpuPercent REAL NULL,
                GpuPercent REAL NULL,
                GpuMemoryUsedMb REAL NULL,
                RamUsedMb REAL NULL,
                DiskReadMBps REAL NULL,
                DiskWriteMBps REAL NULL,
                NetworkInMBps REAL NULL,
                NetworkOutMBps REAL NULL,
                IsActive INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (MachineId) REFERENCES Machines(Id) ON DELETE CASCADE
            )
            """);
        await TryExec(db, "CREATE INDEX IF NOT EXISTS IX_FleetMetricSnapshots_Machine_Sampled ON FleetMetricSnapshots(MachineId, SampledAtUtc)");
        await TryExec(db, "CREATE INDEX IF NOT EXISTS IX_FleetMetricSnapshots_Sampled ON FleetMetricSnapshots(SampledAtUtc)");
        await TryExec(db, """
            CREATE TABLE IF NOT EXISTS TuflowRunRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RunId TEXT NOT NULL,
                RunName TEXT NOT NULL,
                MachineId INTEGER NOT NULL,
                TcfPath TEXT NOT NULL,
                RequestedUtc TEXT NOT NULL,
                RequestedBy TEXT NULL,
                StartedUtc TEXT NULL,
                EndedUtc TEXT NULL,
                State TEXT NOT NULL,
                PercentComplete REAL NULL,
                SimulationTimeHours REAL NULL,
                SimulationEndTimeHours REAL NULL,
                ClockTimeRemainingHours REAL NULL,
                WarningCount INTEGER NULL,
                MassErrorPercent REAL NULL,
                ExitCode INTEGER NULL,
                ErrorSummary TEXT NULL,
                LastCheckpointFile TEXT NULL,
                UpdatedUtc TEXT NOT NULL,
                FOREIGN KEY (MachineId) REFERENCES Machines(Id) ON DELETE CASCADE
            )
            """);
        await TryExec(db, "CREATE UNIQUE INDEX IF NOT EXISTS IX_TuflowRunRecords_RunId ON TuflowRunRecords(RunId)");
        await TryExec(db, "CREATE INDEX IF NOT EXISTS IX_TuflowRunRecords_MachineId_RequestedUtc ON TuflowRunRecords(MachineId, RequestedUtc)");
        // Safety net only: a no-op on a fresh install (CREATE TABLE above already includes RunName /
        // ClockTimeRemainingHours, so these just fail silently as "duplicate column"). Only do real work
        // if you applied the TuflowRunRecords table from an earlier version of this patch.
        await TryExec(db, "ALTER TABLE TuflowRunRecords ADD COLUMN RunName TEXT NOT NULL DEFAULT ''");
        await TryExec(db, "ALTER TABLE TuflowRunRecords ADD COLUMN ClockTimeRemainingHours REAL NULL");
        await TryExec(db, "ALTER TABLE FleetMetricSnapshots ADD COLUMN ProcessCpuPercent REAL NULL");
        await TryExec(db, "ALTER TABLE FleetMetricSnapshots ADD COLUMN ProcessGpuPercent REAL NULL");
        await TryExec(db, "ALTER TABLE FleetMetricSnapshots ADD COLUMN ProcessDiskReadMBps REAL NULL");
        await TryExec(db, "ALTER TABLE FleetMetricSnapshots ADD COLUMN ProcessDiskWriteMBps REAL NULL");

        // Machines list redesign — friendly names + team sections
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN FriendlyName TEXT NULL");
        await TryExec(db, "ALTER TABLE Machines ADD COLUMN TeamId INTEGER NULL");
        await TryExec(db, "CREATE INDEX IF NOT EXISTS IX_Machines_TeamId ON Machines(TeamId)");
        await TryExec(db, "ALTER TABLE AppLists ADD COLUMN IsTeamExcluded INTEGER NOT NULL DEFAULT 0");
        await TryExec(db, "ALTER TABLE AppLists ADD COLUMN IsSystem INTEGER NOT NULL DEFAULT 0");
        await TryExec(db, "ALTER TABLE AppLists ADD COLUMN SystemKey TEXT NULL");
        await TryExec(db, "CREATE UNIQUE INDEX IF NOT EXISTS IX_AppLists_SystemKey ON AppLists(SystemKey) WHERE SystemKey IS NOT NULL");

        await EnsureCanonicalTeamsAsync(db);
    }

    /// <summary>Create canonical org team names if missing (idempotent; does not rename or delete).</summary>
    private static async Task EnsureCanonicalTeamsAsync(HeimdallDbContext db)
    {
        string[] names =
        [
            "Flood", "Acoustics", "Visualisation", "Buildings", "Energy",
            "TSS", "General", "Traffic", "Civil"
        ];
        var existing = await db.Teams.AsNoTracking()
            .Select(t => t.Name)
            .ToListAsync();
        var set = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (set.Contains(name))
                continue;
            db.Teams.Add(new Team { Name = name });
            set.Add(name);
        }
        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync();
    }

    private static async Task<bool> HasSystemFlagAsync(HeimdallDbContext db, string key)
    {
        var conn = db.Database.GetDbConnection();
        var openedHere = conn.State != System.Data.ConnectionState.Open;
        if (openedHere)
            await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM SystemFlags WHERE Key = $k LIMIT 1;";
            var p = cmd.CreateParameter();
            p.ParameterName = "$k";
            p.Value = key;
            cmd.Parameters.Add(p);
            var result = await cmd.ExecuteScalarAsync();
            return result is not null and not DBNull;
        }
        finally
        {
            if (openedHere)
                await conn.CloseAsync();
        }
    }

    private static async Task SetSystemFlagAsync(HeimdallDbContext db, string key, string value)
    {
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR REPLACE INTO SystemFlags (Key, Value) VALUES ({0}, {1});",
            key, value);
    }

    private static async Task EnsureKnownAppAsync(HeimdallDbContext db, string displayName, string processName)
    {
        if (await db.KnownApps.AnyAsync(a => a.ProcessName == processName))
            return;
        db.KnownApps.Add(new KnownApp
        {
            DisplayName = displayName,
            ProcessName = processName,
            EnabledByDefault = true
        });
    }

    private static async Task TryExec(HeimdallDbContext db, string sql)
    {
        try { await db.Database.ExecuteSqlRawAsync(sql); }
        catch { /* column/table already exists */ }
    }
}
