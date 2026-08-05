# TUFLOW start/stop for Heimdall

Lets you queue a TUFLOW run from Heimdall's website and, separately, send a
graceful stop that finishes the current checkpoint and exits cleanly instead
of crashing when the network drops — so the run can be resumed later with at
most one checkpoint interval of lost progress.

This was written against your real Heimdall source (`C:\Heimdall\src`), not
guessed — the shapes below (`PendingCommandsJson`, `AgentConfigDto`,
`HeartbeatDto`, `RemoteMachineService`, `RemoteMachines.cshtml.cs`) are
copied from files I actually read, not assumed.

## Layout

```
TuflowLauncher/          Standalone console app. Runs on the modelling
                          machine as a child process of Heimdall.Agent;
                          owns the actual TUFLOW.exe process and the
                          graceful-stop signal. No dependency on Heimdall.

patches/                 Everything that touches your existing Heimdall
                          repo, laid out to mirror it 1:1:
  Heimdall.Shared/Contracts/
    TuflowRunContracts.cs        NEW file — drop in as-is.
    *.cs.patch.md                Small diffs to existing files.
  Heimdall.Api/
    Services/TuflowRunService.cs NEW file — drop in as-is. Also owns the
                                  fleet-wide averaging behind Fleet Sim
                                  Progress (GetFleetProgressAsync).
    Services/*.patch.md          Diffs to IngestService.cs / Program.cs /
                                  FleetDashboardService.cs.
    Pages/TuflowRuns.cshtml(.cs) NEW Razor page — the "browse the API
                                  website, click start/stop" surface
                                  (fleet-wide, one row per machine).
    Pages/FleetSimProgress.cshtml(.cs) NEW Razor page — one row per
                                  *currently active run*: who started it,
                                  what it's called, how long it's been
                                  going, ETA, and GPU/CPU/Disk/Network
                                  averages. Linked from TuflowRuns.cshtml.
    Pages/Machine.cshtml(.cs).patch.md  Adds a TUFLOW panel (progress,
                                  crash/error detail, run history) to your
                                  existing per-machine page.
    Data/Entities.cs.patch.md    Includes the new TuflowRunRecord entity
                                  (now with RunName/ClockTimeRemainingHours)
                                  and 4 new process-metric columns on the
                                  existing FleetMetricSnapshot.
    Data/HeimdallDbContext.cs.patch.md
  Heimdall.Agent/
    Collectors/TuflowRunHelper.cs NEW file — drop in as-is.
    Worker.cs.patch.md
    Services/HeimdallApiClient.cs.patch.md

tcf/                      Control-file snippets, unrelated to Heimdall.
```

## Apply order

1. `Heimdall.Shared` — add `TuflowRunContracts.cs`, apply the three
   `.patch.md` diffs (`RemoteMachineCommands`, `AgentConfigDto`,
   `HeartbeatDto`). Everything else depends on these types existing.
2. `Heimdall.Api` — add `TuflowRunService.cs` and the four new
   `TuflowRuns.cshtml`/`.cshtml.cs` + `FleetSimProgress.cshtml`/`.cshtml.cs`
   Pages files, apply the `Entities.cs` (now also defines `TuflowRunRecord`
   and the 4 new `FleetMetricSnapshot` process-metric columns —
   sections 1-7), `HeimdallDbContext.cs` (new `DbSet`), `IngestService.cs`
   (covers both `IngestService` and `ConfigService`), `Program.cs` (DI + the
   `/api/tuflow/{hostname}/pending` endpoint), `FleetDashboardService.cs`
   (persists the 4 process-metric columns instead of discarding them), and
   `Machine.cshtml`/`.cshtml.cs` (adds the per-machine TUFLOW panel)
   patches. `FleetSimProgress.cshtml(.cs)` needs no separate DI registration
   or route wiring — it depends on the same already-registered
   `TuflowRunService`, and Razor Pages auto-discovers new `.cshtml` files
   under `Pages/`.
3. `Heimdall.Agent` — add `TuflowRunHelper.cs`, apply the `Worker.cs` patch
   and the `HeimdallApiClient.cs` patch (adds `GetTuflowPendingAsync`).
4. Build `TuflowLauncher/` separately (`dotnet publish -c Release`) and copy
   the published output to each modelling machine — path referenced by
   `TuflowRunHelper.LauncherExePath`, override via the
   `HEIMDALL_TUFLOW_LAUNCHER_EXE` environment variable per machine, or edit
   the hardcoded default (`C:\Heimdall\TuflowLauncher\TuflowLauncher.exe`)
   to match wherever you actually deploy it.
5. Re-run/redeploy `Heimdall.Api` so the new `ALTER TABLE` statements in
   `EnsureSchemaPatchesAsync` execute against the live SQLite DB, and
   `Heimdall.Agent` on each modelling machine.

## Which machines can this run on

Only machines enrolled in the Historical Dashboard's existing "TUFLOW fleet"
list (`FleetDashboardMachine` — the same enrollment that already powers your
Historical Dashboard / fleet sampling) show up on the TUFLOW Runs page at
all. There's no separate "Flood group" concept to manage — enroll/unenroll
machines from wherever you already do that for the Historical Dashboard, and
the Runs page follows automatically. The page has no machine dropdown by
design: every row *is* a specific enrolled machine, so there's no way to
target a machine that isn't on that list, even by hand-editing a form post
(`TuflowRunService.QueueStartAsync`/`QueueStopGracefulAsync` both re-check
enrollment server-side too).

Each row also shows a live "licence / process" status — reusing the fleet
sampler's existing 30-second TUFLOW-process detection
(`FleetDashboardService.GetLiveFleetAsync`), not just runs Heimdall itself
started. A page-top strip shows "N of M Flood machines currently running
TUFLOW" as a rough licence-usage count (one licence assumed per running
instance — see the caveat in `TuflowRunService.cs`). Starting a new run is
blocked on a machine that's already showing TUFLOW running, whether Heimdall
queued that run or someone started it by hand.

## How it works

**Start.** Someone fills in machine + `.exe` path + `.tcf` path (+ optional
scenarios/events) on the new **TUFLOW Runs** page and submits. That's stored
as `Machine.PendingTuflowStartJson`. The agent picks it up on a dedicated
~20s poll (`GET /api/tuflow/{hostname}/pending` — see "Launch/stop latency"
below), spawns `TuflowLauncher.exe` with a `run-spec.json`, and starts
reporting progress back on every heartbeat (`UploadIntervalSeconds`, default
60s) via `HeartbeatDto.TuflowRunStatus`.

**Stop.** Clicking "Stop gracefully" adds the token `TuflowStopGraceful` to
`Machine.PendingCommandsJson` — the same flat string-list mechanism your
existing `RestartTermService` button uses, just a different token. The
agent writes a `stop.request` file into the run's working folder;
`TuflowLauncher` (which spawned TUFLOW inside its own process group) sends
it a `CTRL_BREAK_EVENT` targeted at that process group only, so TUFLOW
finishes writing its current output, releases the licence, and logs
`INTERRUPTED` — exactly the clean-stop behaviour documented for Ctrl+C in
the manual's Console Window section — without also killing the launcher or
the Agent service.

**Launch/stop latency.** Both of the above are picked up by a dedicated fast
poll (`Worker.RunTuflowPollTickAsync`, ~20s cadence, configurable) instead of
waiting on the general `ConfigRefreshSeconds` cycle (default 300s / 5 min)
that `RestartTermService` and everything else in `AgentConfigDto` uses. This
is deliberately a *separate*, cheap endpoint
(`GET /api/tuflow/{hostname}/pending`) rather than just lowering
`ConfigRefreshSeconds` globally — the full config resolution is a heavier
query (tracking configs, app lists, metric policies, fleet enrollment) that
you don't want running four times a minute for every machine just to speed
up TUFLOW specifically. Same pattern your codebase already uses for live
resource sampling's own independent 10s poll. See the "Why a separate fast
poll" note in `Worker.cs.patch.md` for the full reasoning, including why
this is an extra tick in the same Agent process rather than a literal
separate subprocess.

**Resume.** Add the block in `tcf/resume-block-snippet.tcf.txt` to your
`.tcf`. It periodically writes timestamped restart files
(`Write Restart File Interval`) so a stop never loses more than that
interval of progress, and conditionally reads the latest one back in when
you start a new run with the scenario token `RESUME`. `TuflowLauncher`
tracks the newest `.trf`/`.erf` file it sees and reports it as
`LastCheckpointFile` — read that off the Runs page before queuing the
resume.

**Progress, crashes, and errors.** `TuflowLauncher` reads two files TUFLOW
already writes itself, rather than estimating anything or scraping console
text (uncertain whether `-nc` even redirects meaningful text — see the
grounded/unverified list below):

- **Progress** comes from TUFLOW's own `.tsf` (TUFLOW Summary File — manual
  Section 14.4.2), which TUFLOW updates periodically *while running* and
  which already contains `Percentage Complete (%)`, `Simulation Time (h)`,
  `Simulation End Time (h)`, and `Approximate Clock Time Remaining (h)` —
  TUFLOW computes all of these itself, so the launcher just reads and
  passes them through rather than calculating a percentage from the `.tcf`
  (which would need reliably parsing total run duration — fragile across
  scenario/event configurations, so this wasn't attempted).
- **Crashes/errors** are read from the tail of the `.tlf` log (manual
  Section 14.4.1) when the process exits non-zero and no stop was
  requested — the first few lines starting with `ERROR` (TUFLOW's own
  convention for "unrecoverable, simulation stopped": `ERROR <code> - ...`).
  Falls back to the redirected stderr tail if no `.tlf` is found at all
  (e.g. a licence/dongle failure severe enough that TUFLOW never got to
  open one).
- Both flow through unchanged: `TuflowLauncher` → `status.json` →
  `Heimdall.Agent` (`TuflowRunHelper.ReadCurrentStatus`, pass-through, no
  remapping) → heartbeat → `TuflowRunService.ApplyHeartbeatAsync`, which
  updates both `Machine.TuflowRunStatusJson` (live "what's happening now")
  and a durable `TuflowRunRecord` row (see next paragraph).
- Shown on the **Machine page** (`/Machine?hostname=X` — your existing
  per-machine detail page), not just the fleet-wide Runs page: a "TUFLOW"
  panel with live progress/warnings/mass-error stats when something's
  running, plus a "Recent runs" history table with outcome and error detail
  for past runs, for whichever machine you're looking at.

**Why a durable history table, not just the live status field.**
`Machine.TuflowRunStatusJson` (used for the fleet-wide Runs page) only ever
holds the *latest* run — the moment someone queues a new run on a machine,
the previous run's crash detail is gone. So there's a second, additive
mechanism: `TuflowRunRecord`, one row per `RunId`, created when the run is
queued and updated in place as it progresses, but never overwritten by a
later run (which gets its own new row instead). Same shape as
`FleetMetricSnapshot`/`MachineIdentityEvent` elsewhere in your schema — a
history table for a POC feature, just keyed by `RunId` so an in-progress
run's row can be updated live rather than being purely append-only. This is
additive to what you already have wired up — the fleet-wide Runs page and
its `PendingCommandsJson`/`AgentConfigDto.PendingTuflowStart` mechanism are
completely unchanged.

**Why a payload needed a new field instead of reusing `PendingCommands`.**
Your existing command mechanism (`PendingCommandsJson` →
`AgentConfigDto.PendingCommands`, a bare `List<string>`) works for
zero-argument commands like `RestartTermService`. Starting TUFLOW needs real
data — exe path, tcf path, scenarios — which doesn't fit a string token, so
`PendingTuflowStart` is a first-class nullable field on `AgentConfigDto`,
the same way `FleetProcessNames`/`FleetSamplingEnabled` were added as
first-class fields rather than overloading the existing lists. Stop stayed
on the simple token mechanism because it genuinely needs no payload — the
agent resolves *which* run to stop from its own local pointer file, since
one modelling machine runs one TUFLOW job at a time (matching the
`FleetProcessNames: ["tuflow"]` assumption already built into your
Historical Dashboard fleet sampler).

## Fleet Sim Progress

A second page (`/FleetSimProgress`, linked from the top of TUFLOW Runs) —
one row per **currently active** run across every Flood-enrolled machine,
answering "who's running what, where, and how's it going" at a glance
rather than the per-machine start/stop controls on TUFLOW Runs.

**Who / what / where.**
- *Who* — `RequestedBy`, from a "Your name" field on the start form,
  prefilled (best-effort) from `User?.Identity?.Name` where Negotiate
  happens to populate it, but always left editable so it's never silently
  blank. See "Not verified" below — whether that prefill actually fires on
  your deployment isn't confirmed from source alone.
- *What* — `RunName`, from an optional "Run name" field on the same form.
  Left blank, `TuflowRunService.ResolveRunNameAsync` falls back to the
  `.tcf` filename (no extension), then `"Sim {N}"` (N = a count of prior
  runs on that machine) as a last resort. Resolved once at queue time and
  carried through every status update (`RunStatus.RunName` in
  `TuflowLauncher`, `TuflowRunStatusDto.RunName`, `TuflowRunRecord.RunName`)
  so it never needs re-deriving later.
- *Where* — the machine's `FriendlyName`/`Hostname`, same as TUFLOW Runs.

**How long / how long left.**
- *Elapsed* — wall-clock time since `TuflowRunRecord.StartedUtc` (or
  `RequestedUtc` if the agent hasn't confirmed the process actually started
  yet).
- *Est. remaining* — TUFLOW's own `.tsf`-reported "Approximate Clock Time
  Remaining (h)" (`ClockTimeRemainingHours`), the same self-reported figure
  the Machine page panel already shows, not a separately-estimated number.
  It's null until TUFLOW has written at least one `.tsf` update for that
  run (typically within the first minute), and stays null entirely on
  TUFLOW builds/configurations that don't populate that field.

**GPU/CPU/Disk/Network usage — "separate for each TUFLOW exe... plus an
aggregate, otherwise just the total".** Two sets of averages, both computed
from `FleetMetricSnapshot` rows sampled since the run started:
- **"TUFLOW process" columns** — `ProcessCpuPercent`/`ProcessGpuPercent`/
  `ProcessDiskReadMBps`/`ProcessDiskWriteMBps`. These were already being
  *computed* by the agent's fleet sampler (used transiently for the
  Active/Idle threshold check in `FleetDashboardService.IngestSnapshotAsync`)
  but never persisted — this feature is what finally saves them, via 4 new
  nullable columns on `FleetMetricSnapshot` (see `Entities.cs.patch.md`
  section 6, `FleetDashboardService.cs.patch.md`). Under Heimdall's
  one-run-per-machine model, "per TUFLOW exe" and "per machine" are the same
  thing — the sampler sums *all* processes matching `FleetProcessNames`
  (`["tuflow"]`) on that machine, so if you ever ran more than one TUFLOW
  instance on a single machine simultaneously (outside what this feature
  assumes), these figures would blend them rather than separating them.
  True multi-instance-per-machine isolation would need the agent to sample
  per-PID and Heimdall to track more than one run per machine — out of
  scope here, flagging it rather than silently pretending it's handled.
- **"Whole machine" columns** — the same `CpuPercent`/`GpuPercent`/
  `DiskReadMBps`/`DiskWriteMBps`/`NetworkInMBps`/`NetworkOutMBps` gauges
  used everywhere else in the fleet dashboard. This is the "aggregate"
  half of Chris's ask.
- **Network has no "TUFLOW process" column at all** — the agent's fleet
  sampler has only ever computed a *whole-machine* network figure, never a
  per-process one (`FleetSnapshotDto` has `ProcessCpuPercent`/
  `ProcessGpuPercent`/`ProcessDiskReadMBps`/`ProcessDiskWriteMBps` but no
  `ProcessNetwork*` equivalent). Rather than inventing a number, the page
  just shows the whole-machine total for Network — exactly the "otherwise
  just the total" fallback already built into the original request. Adding
  genuine per-process network sampling would be an Agent-side change (not
  just a DTO/column addition) — worth a follow-up if it turns out to matter.
- Each row also shows a **sample count** — how many 30-second snapshots the
  averages are built from, as a rough eyeball for "2 samples right after
  start" vs. "200+ samples on an hours-long run".

**On "over the 5 min polling lifetime".** Worth flagging directly: the
actual whole-machine/TUFLOW-process fleet sampling cadence is **30 seconds**
(`FleetSampleInterval` in `Worker.cs`), not 5 minutes — the 5-minute figure
is `ConfigRefreshSeconds` (default 300s), a *different* cadence for a
*different* thing (pulling the full `AgentConfigDto`), easy to conflate
since both live in the same Agent polling loop. `GetFleetProgressAsync`
averages over each run's **whole lifetime so far**, using the real 30s
samples — not a rolling 5-minute window. If a rolling window (e.g. "average
over just the last 5 minutes, so it reflects current load rather than the
whole run") is actually what was wanted, that's a small, contained change —
filter each run's snapshots to `now.AddMinutes(-5)` instead of `since` in
`TuflowRunService.GetFleetProgressAsync` — flagging the ambiguity rather
than guessing which one you meant.

## What's grounded vs. what needs verifying on your setup

Grounded directly in your source or the manual/wiki mirror in
`../tuflow-reference/`:
- `PendingCommandsJson` / `AgentConfigDto.PendingCommands` /
  `HeartbeatDto.AcknowledgedCommands` /
  `HeartbeatDto.CommandExecutionReports` round-trip — copied from
  `RemoteMachineService.cs`, `Worker.cs`, `IngestService.cs` as they exist
  today.
- `-nc` / `-nq` switches and Ctrl+C-as-clean-stop behaviour — from
  `tuflow-reference/manual/ConsoleDisplay-2.md` (Section 14.1.5) and the
  wiki batch-run pages.
- `Write Restart File Interval` / `Write Restart Filename` /
  `Read Restart File` / restart files living in a `trf`/`erf` folder under
  the results folder — from `tuflow-reference/manual/InitialConditions-2.md`
  (Section 8.8.3), pasted in full by you earlier in this conversation.
- SQLite `ALTER TABLE ... ADD COLUMN` schema-patch pattern — copied from
  `SeedData.EnsureSchemaPatchesAsync`, which already does this for other
  `Machines` columns.
- `.tsf` (TUFLOW Summary File) format and its `Percentage Complete (%)` /
  `Simulation Time (h)` / `Simulation End Time (h)` /
  `Approximate Clock Time Remaining (h)` / `Cumulative Mass Error [ME] (%)` /
  `WARNINGs Prior to Simulation` / `WARNINGs During Simulation` fields —
  from `tuflow-reference/manual/SimLogFiles-2.md` (Section 14.4.2, Table
  14.1), including the exact example row values.
- `.tlf` log file, `ERROR`/`WARNING`/`CHECK` message convention, and both
  files defaulting to the same folder the `.tcf` runs from (unless a `Log
  Folder` command is set) — from the same file, Sections 14.4.1/14.4.5, and
  `tuflow-reference/manual/Output-Folder-2.md`.
- `hd-panel` / `hd-section-title` / `hd-grid` / `hd-stat` / `badge-pill` /
  `hd-table` / `table-responsive` / `text-secondary small` CSS classes used
  in the `Machine.cshtml` patch — all confirmed by reading the real
  `Machine.cshtml` (and `hd-table`/`table-responsive` in sibling pages like
  `Cost.cshtml`) rather than guessed, unlike the standalone TUFLOW Runs
  page's markup (written before I'd seen any real `.cshtml` in this repo —
  worth restyling that one to match now that the real classes are known).
  `FleetSimProgress.cshtml` reuses the same confirmed classes.
- The `FleetSnapshotDto.ProcessCpuPercent`/`ProcessGpuPercent`/
  `ProcessDiskReadMBps`/`ProcessDiskWriteMBps` fields already existing (and
  already being read, just not persisted) in `FleetDashboardService.
  IngestSnapshotAsync` — confirmed from the real `FleetSnapshotContracts.cs`
  and `FleetDashboardService.cs`, not assumed. There is no
  `ProcessNetworkInMBps`/`ProcessNetworkOutMBps` equivalent in that same
  real file — confirmed absent, not just unused.

Not verified — flagged inline in code comments, worth a supervised test
run before relying on this tonight:
- **CTRL_BREAK_EVENT vs CTRL_C_EVENT.** The manual only documents Ctrl+C
  from an interactive console. `CTRL_BREAK_EVENT` is used here because
  Windows can target it at a specific process group non-interactively,
  which Ctrl+C cannot do without also hitting the launcher. Whether
  TUFLOW's runtime treats the two identically isn't documented — see the
  comment in `NativeMethods.cs`.
- **Scenario/event switch numbering** (`-s1`/`-e1` vs plain `-s`/`-e`) in
  `TuflowLauncher/Program.cs`'s `BuildCommandLine` — worth checking against
  your TUFLOW build's exact `-s`/`-e` syntax if you use scenarios/events.
- **Results-folder auto-detection.** `TuflowLauncher` searches
  `WorkingDirectory` for a folder literally named `trf` or `erf` if you
  don't pass `ResultsFolder` explicitly. If your `Output Folder ==`
  convention doesn't produce that, checkpoint tracking (`LastCheckpointFile`
  on the Runs page) silently won't populate — the stop itself still works
  either way, since that only depends on the process handle, not the
  results folder.
- **Log-folder auto-detection (same idea, for `.tsf`/`.tlf`).** Same
  best-effort search pattern as the results folder — checks
  `WorkingDirectory` directly first (the documented default when no `Log
  Folder` command is set), then a folder named `log`, then a recursive
  search. Pass `RunSpec.LogFolder` explicitly if your setup uses `Log
  Folder ==` to somewhere this search wouldn't find. If it can't find a
  `.tsf`, progress fields just stay null (no error, no crash); if it can't
  find a `.tlf` on a crash, `ErrorSummary` falls back to the stderr tail
  instead (see next item).
- **Whether `-nc` still writes anything useful to redirected stdout/stderr.**
  `-nc` hides the console window; whether TUFLOW still writes to the
  redirected stream handles underneath, or suppresses that output entirely
  since there's "no console", isn't documented either way. The stderr-tail
  fallback in `TryExtractStderrTail` (only used when no `.tlf` was found at
  all) may simply come back empty in practice — worth a quick supervised
  test run to check, since it matters most for exactly the scenario you
  care about (a licence-server disconnect crash so early that TUFLOW never
  opens a `.tlf`). If it turns out empty, the crash still shows as
  `Failed` with an exit code, just without an `ErrorSummary` string.
- **`.tsf` parsing assumes the exact key text from the manual's example**
  (e.g. `"Percentage Complete (%)"`, `"Simulation Time (h)"`). `TryParseTsf`
  matches on `key.StartsWith(...)` rather than an exact match specifically
  so minor formatting differences (extra spaces, a slightly different unit
  suffix) don't break the whole parse, but a genuinely different key string
  in your TUFLOW build/version would just leave that one field null rather
  than erroring — worth eyeballing a real `.tsf` from your setup once.
- **`RequestedBy` on the Razor page.** The start form has an editable "Your
  name" text field, prefilled (best-effort) from `User?.Identity?.Name`. I
  haven't seen how your staff pages resolve the signed-in person's identity
  elsewhere in the app — Negotiate is wired globally in `Program.cs`, but
  whether it actually populates `HttpContext.User` on this specific internal
  admin page in your deployment isn't verified. Keeping the field editable
  (rather than read-only or omitted) means "who kicked it off" is never
  silently blank even if the prefill doesn't fire — worth checking once
  whether the prefill works, but not blocking on it either way. Wire
  `StaffAccessGuard`/`WindowsStaffIdentityService` in instead if you want a
  verified identity rather than a self-reported/editable one.
- **Not build-verified.** No `dotnet` SDK available in this environment —
  checked instead by re-reading every new/patched file against the real
  source and a brace/paren balance check across all `.cs` files (all
  balanced). Build it once before deploying.

## For tonight specifically

Given the disconnection window: queue the stop from the Runs page *before*
you pull the network, not after. With the fast poll (edit 5/6 in
`Worker.cs.patch.md`), the agent should pick up the stop within ~20s and
send `CTRL_BREAK_EVENT`; the row confirming "Stopped" on the page can lag a
little further behind that (up to `UploadIntervalSeconds`, default 60s, for
the acknowledgement to round-trip back). Either way, it's safer to trigger
the stop with enough lead time to actually see it reach `Stopped` on the
page before you disconnect, rather than assuming it landed.
