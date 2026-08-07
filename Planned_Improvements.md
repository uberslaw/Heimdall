# Planned improvements

Parked work from the Machines level-1 redesign and related fleet UX. Not scheduled for the current pass.

## Machine detail (level 2)

- Edit **Friendly name** and **Team** on the machine page
- Show **Active app list** (what’s tracked) and **Ignored app list**; edit from that page
- **Last check-in** display: 1–59m, then 24h `HH:MM`, or previous-day `DD/MM/YY`
- **Type** (RDP / Local), open sessions
- Move Analyze / Socratize / pending-approval actions off the list onto this page
- **Manual full app discovery** — button on machine page (see section below)
- **Free space / folder tree-size** — on-demand panel on machine page (see section below)

## Manual full app discovery (machine page)

**Goal:** From `/Machine?hostname=…`, one button to ask that host’s agent for a full process inventory (same pipeline as App lists → Request Inventory), without leaving the machine page. Supports new-machine app tracking and Team apps workflow.

### What already exists

- API: `AppListService.RequestAgentInventoryAsync` sets `Machine.PendingAppAnalysis`
- Config DTO exposes `PendingAppAnalysis`; agent sets `_sendInventoryNextUpload` on config refresh (~5 min)
- Agent: `ProcessCollector.DiscoverInventory()` — **currently running processes only** (WMI paths + file version), uploaded on next heartbeat (~1 min)
- Stored as `DiscoveredInventoryJson`; catalog upsert; App lists Machine lookup / Team apps consume it
- UI today: App lists → Machine lookup only (`Request Inventory` + link “Analyze apps” from Machine page)

### Proposed UX (machine page)

Place under existing **App lists** panel (or a new **Discovery** strip in the meta line):

| Control | Behaviour |
|--------|-----------|
| **Request full inventory** (silver) | Calls same `RequestAgentInventoryAsync`; toast: pending until next config+upload cycle |
| Status chip | “Inventory pending…” while `PendingAppAnalysis`; else last inventory time if known |
| **Open in App lists** | Keep deep-link to `/AppLists?host=…#machine-focus` / `#team-apps` for classify / Team apps actions |
| Optional later | **Analyze** on this page (proposals approve/dismiss) — already parked under “Move Analyze… onto this page” |

### Scope phases

1. **Wire existing inventory to Machine page** (small) — POST handler → `RequestAgentInventoryAsync`; show pending state; no agent change
2. **Richer “full” discovery** (optional follow-up) — still on-demand, not always-on:
   - Uninstall registry / Start Menu / Program Files exe scan (installed apps, not only running)
   - Cap size / duration; filter with `DiscoveryCatalogFilter`
   - Distinct flag or snapshot type so UI can show “running inventory” vs “installed scan”
3. **New-machine workflow** — after inventory lands, highlight Spec/unlisted (reuse Team apps candidates API) on machine page or deep-link to Team apps

### Out of scope for phase 1

- Changing global catalog sync / system Spec list behaviour
- Always-on filesystem crawling for apps
- Fleet-wide “discover all machines now”

### Redeploy notes

- Phase 1: API-only (reuse flag)
- Phase 2: agent + shared DTO + API ingest

---

## Free space / folder tree-size view

Parked one-liner (expanded here). TreeSize-style ops need: free space at a glance + “what’s eating the disk” on demand.

### Goal

On-demand agent scan of **volume free space** and **large folder trees** — not always-on sampling. Surfaces on **machine detail** (primary) and optionally a compact chip on Fleet Computers later.

### Why it fits machine page

- Same on-demand pattern as app inventory / RDS restart / TUFLOW commands (`PendingCommands` or a dedicated pending flag)
- Expensive walks must never run every heartbeat
- Operators already open a host when investigating disk pressure

### Proposed model

**A. Cheap always-available (or with hardware refresh)**  
- Logical volumes: letter, label, size, free, % used (`Win32_LogicalDisk` DriveType=3)  
- Hardware inventory already collects total disk GB; extend to per-volume free/used  
- Show as a small **Storage** strip on machine page (bars or table)

**B. On-demand tree-size scan (TreeSize-like)**  
- Button: **Scan folder sizes** → queues agent job (new `RemoteMachineCommands.DiskTreeScan` or `PendingDiskTreeScan` + options JSON: roots `C:\`, `D:\`, max depth, top-N, exclude junctions)
- Agent: throttled walk (or MFT enumeration later); respect timeouts / CPU budget; cancel if host busy optional
- Upload: tree summary JSON (path, bytes, file count, child rollups) stored on machine (`DiskTreeScanJson` + `DiskTreeScanUtc`) or side table
- UI: expandable tree or top-N folders by size; “Scanned at …” + re-scan

### Security / load

- No free-form remote shell; allowlisted roots only (fixed drives + optional admin-configured paths)
- Cap depth / total nodes / runtime; never block sampling loop (background task)
- Audit: who requested scan, host, when, success/failure

### Placement

| Surface | What |
|--------|------|
| **Machine detail** | Primary: Storage strip (A) + Scan button + results panel (B) |
| **Fleet Computers** | Optional later: free-% warning chip only (needs A cached) |
| **Admin → Data and Retention** | Document that tree scans are on-demand and not retained forever (TTL / replace previous scan) |
| **Planned “Capture settings”** | Unrelated — tree scan is not continuous capture |

### Phasing

1. **Volume free/used on machine page** from agent hardware/logical-disk payload (low risk)
2. **On-demand top-level folder sizes** for `C:\` (and other fixed drives) — depth 1–2 first
3. **Deeper TreeSize UX** + excludes + export CSV

### Open questions

- Service account vs interactive user view of network drives (`P:\`) — same issue as TUFLOW paths
- Retain last scan only vs history
- MFT vs recursive `Directory.Enumerate` for v1 (Enumerate is simpler; MFT later if too slow)

## Level 3 drill-down

- Machine → Resource usage → dated raw samples / per-app share of hardware
- Same hierarchy for sessions and app usage over any range with stored data

## Fleet-wide sampling

- ~~Always-on ~30s sampling for all Machines-list hosts (not only Historical Dashboard enrollment)~~ **Done** — sampling is always-on for known Machines; `FleetDashboardMachines` is Flood/TUFLOW allowlist only. Fleet → Live shows the estate; Flood hub keeps enrolled-scoped Live/Historical + Sims + Enrollment.
- So **Passive** use and **GPU/CPU hours + Dr/Dw/NTx/NRx** fill for every machine over selected windows

## Idle status (full rules)

- Idle = disconnected ≥15m **and** CPU/GPU &lt;10% each **and** no moderate disk/network
- Requires continuous samples + tuned thresholds

## Baseline page (under Machines)

- Select machines and a recording period
- Sample overall hardware usage (~30s, tunable) across all programs
- Export/upload for analysis to set Idle / Passive thresholds (SOE and Windows overnight noise)

## Min-busy gate

- Exclude Core Windows and SOE security processes when deciding “busy” / Passive

## DT (Desktop Team / named accounts)

- Named accounts in a **DT group**; their active session time (RDP + Local; not disconnected) apportioned out of major fleet stats
- Separate **DT page** showing time spent on machines

## Business hours utilisation

- Toggle **core business hours** (08:30–17:00)
- Show % use in office hours vs outside

## Fleet overview pages

- High-level views: machines & status, resource & app usage, sessions
- Filter by team; click through to machine detail then level 3

## New-machine app tracking

- Track which apps a new machine uses
- Ignore SOE and Windows Core processes

## Admin settings → Data and Retention

- Show breakdown of what is captured on clients
- How long each data point is kept
- What gets transmitted to the database and how often (if ever)
- What is cleaned up or compacted
- Consider DB size alerts
- Show how much data is stored on each client
- Ability to trim/purge old data remotely
- Show current DB size and expected growth based on current input trends

## Capture settings fine-tuning

- Increase/decrease sampling frequency
- Global and Team level set via Data and Retention page
- Show projected growth of local and DB storage based on new settings **before** applying them
- Allow time-based settings: capture more data for X days/weeks/months then revert to default
- Individual machine page: local-level tuning with the same functionality

## TUFLOW modelling run control (remote start / graceful stop)

**POC wired into Heimdall (2026-08-06)** from `tuflow-automation/` — console pages, agent fast poll, and `TuflowLauncher` (CTRL_BREAK graceful stop). Follow-ups below remain open.

### What shipped (POC)

- **TUFLOW Runs** (`/TuflowRuns`) and **Fleet Sim Progress** (`/FleetSimProgress`) — nav under Remote Machines
- Queue start via free-form exe + `.tcf` (+ optional scenarios/events/run name); stop via `TuflowStopGraceful` pending command
- Agent ~20s poll `GET /api/tuflow/{hostname}/pending`; launcher under `%ProgramFiles%\Heimdall\Agent\TuflowLauncher\`
- Machine page TUFLOW panel + `TuflowRunRecord` history; fleet process CPU/GPU/disk metrics persisted
- Machines must be enrolled on Historical Dashboard (TUFLOW fleet); start blocked if TUFLOW already detected running
- Client pack (`Pack-WorkstationCollector.cmd`) and `install-agent.ps1` publish the launcher beside the agent

### Still follow-ups (not in POC)

- **RBAC:** Modelling + DT only (pages are open admin like the rest of the console today)
- **Allowlisted jobs:** replace free-form paths with curated batch/job definitions (closer to original parked design)
- Supervised verify: CTRL_BREAK vs Ctrl+C on your TUFLOW build; `.tsf`/`.tlf` folder discovery; `-s1`/`-e1` syntax
- Per-process network metrics; optional rolling 5-minute fleet averages
- **Local TUFLOW log folder** (`C:\ProgramData\TUFLOW\Log`): monitor for **stop times** and **error output**; build a start / stop / failures list matched to **run names, machines, people**; look for patterns. More analytics later.

### Original parked design notes (reference)

Parked **2026-08-05**. Modelling team need: long TUFLOW runs (often days) started from known batch files; hard to interrupt mid-run for emergency scenario changes without killing processes and wasting days of compute. Goal: Heimdall console can send simple **graceful** Start / Stop for **allowlisted** jobs only (modelling team + DT).

**Feasibility vs current agent:** Fits the existing allowlisted remote-command pattern (`PendingCommands` / ack + `CommandExecutionReportDto`, today used for `RestartTermService`) plus process-count visibility from inventory/sampling — extend with job-scoped Start/Stop tokens, not a free-form remote shell.

### Problem / goal

- Runs launched via batch calling `TUFLOW_iSP_w64.exe` (high priority, minimized window); concurrent instances capped in-batch by counting that exe
- Need remote **start** of a known batch and **graceful stop** without arbitrary remote cmd.exe
- Console restricted to **Modelling** + **DT** roles

### Scope

- **Allowlisted jobs only** — no arbitrary remote shell, no user-supplied command strings at runtime
- Job definitions curated by admins/DT; agents execute only matching Start/Stop actions for those definitions
- Stop command details TBD from modelling / TUFLOW docs (placeholder until provided)

### Example start pattern (ops sample)

Typical batch (document as-is; sample contains `@echo offf`):

- Sets `TUFLOWEXE` to the `TUFLOW_iSP_w64.exe` path
- Starts instances with `start "TUFLOW TEST MODEL" /high /min %TUFLOWEXE% -x -b` plus scenario flags (`-s1`, `-s2`, `-e1`, …) and `.tcf` files
- Caps concurrency via `:limit_tuflow_instances` counting running `TUFLOW_iSP_w64.exe`

Heimdall should treat the **batch path** (and optional stop script) as the allowlisted unit of work, not re-implement scenario arg assembly in the console unless a later phase needs it.

### Proposed model

**Job definitions** (API / config store):

- Display name
- Target machine or pool
- Working directory
- Start script path (known `.bat` / `.cmd`)
- Stop command or script path (**TBD** — user will supply official graceful stop)
- Exe identity for status: `TUFLOW_iSP_w64.exe`
- Max concurrent instances (align with batch limiter where possible)

**Agent capability:**

- Execute only allowlisted **Start** / **Stop** actions for configured jobs (same delivery style as today’s pending commands + execution reports)
- Report process count / running status for the job’s exe back to the API (reuse process sampling / inventory patterns)

**Graceful stop:**

- Placeholder for official TUFLOW stop once modelling provides the command/script
- Hard kill of `TUFLOW_iSP_w64.exe` is last resort only (wastes multi-day runs); do not default UI Stop to kill

**Console UI:**

- Modelling / **Run Control** page: Start / Stop / status per job
- RBAC: **Modelling** + **DT** only

### Security

- No free-form command entry from the browser
- Signed or otherwise allowlisted absolute paths for start/stop scripts and working directories
- Audit log: who started/stopped which job, when, on which host, success/failure detail

### Open questions

- Exact **graceful stop** command/script for TUFLOW (blocking for safe Stop UX)
- Multi-host farms / pools: one job → one host vs enqueue across a pool
- Does **Start** mean “run the whole batch as today” or “enqueue the next scenario / one instance under the cap”?
- Credentials and network drives (`P:\` paths): agent service account must see the same shares the interactive modelling sessions use

## Free space / folder tree-size view

- Expanded under **Machine detail** sections above (volume free space + on-demand TreeSize-style scan). Keep this stub for backlog scanning:
- On-demand agent scan of free disk space and large folder trees (MFT-style or throttled walk) — not always-on sampling
- Surfaces in Fleet / machine detail when requested; avoid continuous filesystem walking on every heartbeat

---

## Specialization → team software review

**Canonical requirements:** [`docs/spec-team-review-requirements.md`](docs/spec-team-review-requirements.md)

Parked summary: auto-add newly discovered/classified Spec apps (path + exe) to the machine’s team **primary** AppList; Applications review page for Continue vs Ignore; per-team/per-machine ignore lists; idle weekly agent inventory (CPU+GPU &lt; 50%); presence cleanup with UNC/non-`C:\` sticky paths; 12‑month inactive flag.

**Also park:** Applications back channel to find ignored path+exe and re-track (after review Ignore).

---

## Fleet Apps (Applications nav)

**Goal:** Fleet-wide software catalogue under **Applications → Fleet Apps**, app-primary (not per-machine).

### List page

- Same **name** and **location** (path) presentation as Discovery.
- Column: **number of machines** that have the software.
- That count is **clickable**.

### Drill-down page (one app / path+exe)

- Software name (and path) as header.
- Table of **all machines** that have it, with:
  - First detected
  - Last run
  - How often it is run (frequency)
  - Average run time
  - Average hardware resource usage (CPU / GPU / memory as available from samples)

### Dependencies / notes

- Benefits from path+exe identity and app↔machine presence work in `docs/spec-team-review-requirements.md`.
- Usage stats come from existing `ProcessRuns` (and related metrics); may need path-aware aggregation if multiple paths share an exe name.
- 12‑month inactive flag can surface here as a badge/filter.
