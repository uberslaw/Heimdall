using System.Text.Json;
using Heimdall.Api.Data;
using Heimdall.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

public class IngestService(HeimdallDbContext db)
{
    public async Task IngestAsync(IngestBatchDto batch, CancellationToken ct)
    {
        Machine? machine = null;

        if (batch.Heartbeat is not null)
        {
            machine = await UpsertMachineAsync(batch.Heartbeat, ct);
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

        await db.SaveChangesAsync(ct);
    }

    private async Task<Machine> UpsertMachineAsync(HeartbeatDto heartbeat, CancellationToken ct)
    {
        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Hostname == heartbeat.Hostname, ct);
        if (machine is null)
        {
            machine = new Machine
            {
                Hostname = heartbeat.Hostname,
                FirstSeenUtc = heartbeat.TimestampUtc,
                LastSeenUtc = heartbeat.TimestampUtc
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

        return machine;
    }

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
        var existing = await db.Sessions.FirstOrDefaultAsync(s => s.ExternalEventId == dto.EventId, ct);
        if (existing is null)
        {
            existing = new UserSession
            {
                ExternalEventId = dto.EventId,
                Machine = machine,
                SessionId = dto.SessionId,
                Username = dto.Username,
                Domain = dto.Domain,
                SessionType = dto.SessionType,
                State = dto.State,
                StartedAtUtc = dto.StartedAtUtc ?? dto.ObservedAtUtc,
                EndedAtUtc = dto.EndedAtUtc,
                LastObservedUtc = dto.ObservedAtUtc,
                ClientName = dto.ClientName,
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
        existing.ClientName = dto.ClientName ?? existing.ClientName;
        existing.ClientAddress = dto.ClientAddress ?? existing.ClientAddress;
    }

    private async Task UpsertProcessRunAsync(Machine machine, ProcessRunDto dto, CancellationToken ct)
    {
        var existing = await db.ProcessRuns.FirstOrDefaultAsync(p => p.ExternalRunId == dto.RunId, ct);
        if (existing is null)
        {
            db.ProcessRuns.Add(new ProcessRun
            {
                ExternalRunId = dto.RunId,
                Machine = machine,
                Username = dto.Username,
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

        var metricPolicies = await db.MetricPolicies.AsNoTracking()
            .Where(p => p.IsEnabled)
            .ToListAsync(ct);

        var thresholds = ResolveMetricThresholds(metricPolicies, machine, hostname);

        return new AgentConfigDto
        {
            ConfigVersion = primary.Id * 1000 + primary.SampleIntervalSeconds + include.Count + thresholds.Count,
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
                Enabled = a.EnabledByDefault
            }).ToList(),
            MetricThresholds = thresholds
        };
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
            ConfigScope.Office => machine is not null &&
                                  !string.IsNullOrWhiteSpace(machine.Region) &&
                                  !string.IsNullOrWhiteSpace(machine.Office) &&
                                  (string.Equals(scopeValue, $"{machine.Region}/{machine.Office}", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(scopeValue, machine.Office, StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }

    public static int ScopeRank(ConfigScope scope) => scope switch
    {
        ConfigScope.Machine => 50,
        ConfigScope.Office => 40,
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
    }

    private static async Task TryExec(HeimdallDbContext db, string sql)
    {
        try { await db.Database.ExecuteSqlRawAsync(sql); }
        catch { /* column/table already exists */ }
    }
}
