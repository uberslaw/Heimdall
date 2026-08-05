# Patch: Heimdall.Api/Data/Entities.cs

## 1. Two new nullable columns on `Machine`

Add these next to the existing `PendingCommandsJson` / `RestartRdsProgressJson`
pair (same file, same class — confirmed exact current text below):

```diff
     /// <summary>JSON array of pending agent commands (RestartTermService, …).</summary>
     public string? PendingCommandsJson { get; set; }
     /// <summary>Restart RDS workflow progress (phase, attempts, verification result).</summary>
     public string? RestartRdsProgressJson { get; set; }
+
+    /// <summary>JSON TuflowStartRequestDto queued for the agent; cleared once heartbeat status confirms pickup.</summary>
+    public string? PendingTuflowStartJson { get; set; }
+    /// <summary>Latest JSON TuflowRunStatusDto reported by the agent for the run it is tracking, if any.</summary>
+    public string? TuflowRunStatusJson { get; set; }
```

## 2. Schema patch (SQLite `ALTER TABLE`, POC has no migrations)

In `Heimdall.Api/Services/IngestService.cs`, inside
`SeedData.EnsureSchemaPatchesAsync`, add two more `TryExec` calls next to
the existing `Machines` column patches:

```diff
         await TryExec(db, "ALTER TABLE Machines ADD COLUMN Region TEXT NULL");
         await TryExec(db, "ALTER TABLE Machines ADD COLUMN Office TEXT NULL");
         await TryExec(db, "ALTER TABLE Machines ADD COLUMN Country TEXT NULL");
+        await TryExec(db, "ALTER TABLE Machines ADD COLUMN PendingTuflowStartJson TEXT NULL");
+        await TryExec(db, "ALTER TABLE Machines ADD COLUMN TuflowRunStatusJson TEXT NULL");
         await TryExec(db, "ALTER TABLE ProcessRuns ADD COLUMN PeakGpuPercent REAL NULL");
```

`TryExec` already swallows "duplicate column" errors on re-run (that's how
the other POC column patches work), so this is safe to apply repeatedly.

## 3. New entity — `TuflowRunRecord` (durable per-machine run history)

`Machine.TuflowRunStatusJson` above is a single mutable field — it only
ever holds the *latest* run's status, so a crash's detail is gone the
moment someone queues the next run. Add a dedicated append-style entity
instead: one row per `RunId`, created when a run is queued and updated in
place as that same run progresses, but never overwritten by a later run
(which gets its own new row). This is the same shape as `FleetMetricSnapshot`
/ `MachineIdentityEvent` elsewhere in this file — a per-event/per-run history
table — just keyed by `RunId` instead of being purely append-only, since we
want live updates to an in-progress run's row, not a new row every poll tick.

Add this class anywhere in `Entities.cs` (next to `FleetMetricSnapshot` is a
natural spot — same "history table for a POC feature" flavour):

```csharp
/// <summary>
/// One row per TUFLOW run request (keyed by RunId), created when queued and updated in place as the
/// run progresses/finishes. Unlike Machine.TuflowRunStatusJson (which only ever holds the latest run),
/// each run gets its own permanent row here — so a crash's ErrorSummary/ExitCode survives even after a
/// later run overwrites the "current status" field. Powers the per-machine run history on Machine.cshtml.
/// </summary>
public class TuflowRunRecord
{
    public int Id { get; set; }
    public required string RunId { get; set; }
    /// <summary>"Which simulation" — see TuflowStartRequestDto.RunName for how it's resolved.</summary>
    public required string RunName { get; set; }
    public int MachineId { get; set; }
    public Machine Machine { get; set; } = null!;
    public required string TcfPath { get; set; }
    public DateTimeOffset RequestedUtc { get; set; }
    public string? RequestedBy { get; set; }
    public DateTimeOffset? StartedUtc { get; set; }
    /// <summary>Set once, the first time this run's state becomes Stopped/Completed/Failed.</summary>
    public DateTimeOffset? EndedUtc { get; set; }
    /// <summary>Live-updated — one of TuflowRunStates.* (Starting/Running/.../Stopped/Completed/Failed).</summary>
    public required string State { get; set; }
    public double? PercentComplete { get; set; }
    public double? SimulationTimeHours { get; set; }
    public double? SimulationEndTimeHours { get; set; }
    /// <summary>TUFLOW's own "Approximate Clock Time Remaining (h)" from the .tsf — see
    /// TuflowRunStatusDto.ClockTimeRemainingHours. Kept here (not just on the live Machine.TuflowRunStatusJson)
    /// so the Fleet Sim Progress page can read "how long left" straight off this row like everything else.</summary>
    public double? ClockTimeRemainingHours { get; set; }
    public int? WarningCount { get; set; }
    public double? MassErrorPercent { get; set; }
    public int? ExitCode { get; set; }
    public string? ErrorSummary { get; set; }
    public string? LastCheckpointFile { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}
```

## 4. `HeimdallDbContext.cs` changes

See `patches/Heimdall.Api/Data/HeimdallDbContext.cs.patch.md` for the new
`DbSet` and `OnModelCreating` entry (kept as a separate patch file since
it's a different real file from `Entities.cs`).

## 5. Schema patch for the new table

Add to `EnsureSchemaPatchesAsync` (same function as section 2 above), using
the same `CREATE TABLE IF NOT EXISTS` style already used there for
`MetricPolicies`/`Teams`/etc.:

```diff
         await TryExec(db, """
             CREATE TABLE IF NOT EXISTS UtilizationCriteria (
                 ...
             )
             """);
+        await TryExec(db, """
+            CREATE TABLE IF NOT EXISTS TuflowRunRecords (
+                Id INTEGER PRIMARY KEY AUTOINCREMENT,
+                RunId TEXT NOT NULL,
+                RunName TEXT NOT NULL,
+                MachineId INTEGER NOT NULL,
+                TcfPath TEXT NOT NULL,
+                RequestedUtc TEXT NOT NULL,
+                RequestedBy TEXT NULL,
+                StartedUtc TEXT NULL,
+                EndedUtc TEXT NULL,
+                State TEXT NOT NULL,
+                PercentComplete REAL NULL,
+                SimulationTimeHours REAL NULL,
+                SimulationEndTimeHours REAL NULL,
+                ClockTimeRemainingHours REAL NULL,
+                WarningCount INTEGER NULL,
+                MassErrorPercent REAL NULL,
+                ExitCode INTEGER NULL,
+                ErrorSummary TEXT NULL,
+                LastCheckpointFile TEXT NULL,
+                UpdatedUtc TEXT NOT NULL,
+                FOREIGN KEY (MachineId) REFERENCES Machines(Id) ON DELETE CASCADE
+            )
+            """);
+        await TryExec(db, "CREATE UNIQUE INDEX IF NOT EXISTS IX_TuflowRunRecords_RunId ON TuflowRunRecords(RunId)");
+        await TryExec(db, "CREATE INDEX IF NOT EXISTS IX_TuflowRunRecords_MachineId_RequestedUtc ON TuflowRunRecords(MachineId, RequestedUtc)");
+        // Safety net only: a no-op on a fresh install (CREATE TABLE above already includes RunName /
+        // ClockTimeRemainingHours, so these just fail silently as "duplicate column"). Only do real work
+        // if you applied the TuflowRunRecords table from an earlier version of this patch.
+        await TryExec(db, "ALTER TABLE TuflowRunRecords ADD COLUMN RunName TEXT NOT NULL DEFAULT ''");
+        await TryExec(db, "ALTER TABLE TuflowRunRecords ADD COLUMN ClockTimeRemainingHours REAL NULL");
```

(Confirmed `TryExec` is `try { await db.Database.ExecuteSqlRawAsync(sql); } catch { }` — a blanket
catch-and-ignore, so it's safe for `CREATE INDEX IF NOT EXISTS` too, not just
`CREATE TABLE`/`ALTER TABLE`.)

## 6. Four new nullable columns on the *existing* `FleetMetricSnapshot`

For the Fleet Sim Progress page's per-run GPU/CPU/Disk averages (see
`patches/Heimdall.Api/Services/FleetDashboardService.cs.patch.md` and
`TuflowRunService.GetFleetProgressAsync`). Confirmed current text of this
class in the real `Entities.cs` (line ~509) below — `FleetSnapshotDto`
already carries `ProcessCpuPercent`/`ProcessGpuPercent`/
`ProcessDiskReadMBps`/`ProcessDiskWriteMBps` into `IngestSnapshotAsync`
today, but only uses them transiently to compute `IsActive`, then discards
them — nothing persists the process-specific figures, only the
whole-machine gauges below them. That's the gap this section closes.

```diff
 public class FleetMetricSnapshot
 {
     public long Id { get; set; }
     public DateTimeOffset SampledAtUtc { get; set; }
     public int MachineId { get; set; }
     public Machine Machine { get; set; } = null!;
     public string? Username { get; set; }
     public bool TuflowRunning { get; set; }
     public double? CpuPercent { get; set; }
     public double? GpuPercent { get; set; }
     public double? GpuMemoryUsedMb { get; set; }
     public double? RamUsedMb { get; set; }
     public double? DiskReadMBps { get; set; }
     public double? DiskWriteMBps { get; set; }
     public double? NetworkInMBps { get; set; }
     public double? NetworkOutMBps { get; set; }
+    /// <summary>TUFLOW-process CPU % at this sample — see FleetSnapshotDto.ProcessCpuPercent. Null for
+    /// samples from older agents that don't report process-specific figures yet.</summary>
+    public double? ProcessCpuPercent { get; set; }
+    /// <summary>TUFLOW-process GPU % at this sample — see FleetSnapshotDto.ProcessGpuPercent.</summary>
+    public double? ProcessGpuPercent { get; set; }
+    /// <summary>TUFLOW-process disk read MB/s at this sample — see FleetSnapshotDto.ProcessDiskReadMBps.</summary>
+    public double? ProcessDiskReadMBps { get; set; }
+    /// <summary>TUFLOW-process disk write MB/s at this sample — see FleetSnapshotDto.ProcessDiskWriteMBps.</summary>
+    public double? ProcessDiskWriteMBps { get; set; }
     /// <summary>True when TuflowRunning and any active threshold is met (stored at ingest for stable history).</summary>
     public bool IsActive { get; set; }
 }
```

No `HeimdallDbContext.cs` change needed for this section — `FleetMetricSnapshot`
already has a `DbSet`/`OnModelCreating` entry, and these are plain nullable
scalar columns with no extra configuration (same as the existing
`CpuPercent`/`GpuPercent`/etc. immediately above them).

There is deliberately **no** `ProcessNetworkInMBps`/`ProcessNetworkOutMBps` —
the agent's fleet sampler has never computed a per-process network figure
(only whole-machine `NetworkInMBps`/`NetworkOutMBps`), and adding one is a
real Agent-side sampling change, not just a DTO/column addition. The Fleet
Sim Progress page therefore shows Network as a whole-machine aggregate only
("otherwise just the total", per Chris's own fallback wording) while
GPU/CPU/Disk get genuine per-run (== per-TUFLOW-process, under the
one-run-per-machine model) figures alongside the same whole-machine
aggregate. See that page's patch notes for the full explanation.

## 7. Schema patch for the four new columns

Add to `EnsureSchemaPatchesAsync` (same function as sections 2 and 5):

```diff
         await TryExec(db, "ALTER TABLE TuflowRunRecords ADD COLUMN RunName TEXT NOT NULL DEFAULT ''");
+        await TryExec(db, "ALTER TABLE FleetMetricSnapshots ADD COLUMN ProcessCpuPercent REAL NULL");
+        await TryExec(db, "ALTER TABLE FleetMetricSnapshots ADD COLUMN ProcessGpuPercent REAL NULL");
+        await TryExec(db, "ALTER TABLE FleetMetricSnapshots ADD COLUMN ProcessDiskReadMBps REAL NULL");
+        await TryExec(db, "ALTER TABLE FleetMetricSnapshots ADD COLUMN ProcessDiskWriteMBps REAL NULL");
```
