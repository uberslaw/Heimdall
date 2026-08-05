# Planned improvements

Parked work from the Machines level-1 redesign and related fleet UX. Not scheduled for the current pass.

## Machine detail (level 2)

- Edit **Friendly name** and **Team** on the machine page
- Show **Active app list** (what’s tracked) and **Ignored app list**; edit from that page
- **Last check-in** display: 1–59m, then 24h `HH:MM`, or previous-day `DD/MM/YY`
- **Type** (RDP / Local), open sessions
- Move Analyze / Socratize / pending-approval actions off the list onto this page

## Level 3 drill-down

- Machine → Resource usage → dated raw samples / per-app share of hardware
- Same hierarchy for sessions and app usage over any range with stored data

## Fleet-wide sampling

- Always-on ~30s sampling for all Machines-list hosts (not only Historical Dashboard enrollment)
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

Parked **2026-08-05**. Modelling team need: long TUFLOW runs (often days) started from known batch files; hard to interrupt mid-run for emergency scenario changes without killing processes and wasting days of compute. Goal: Heimdall console can send simple **graceful** Start / Stop for **allowlisted** jobs only (modelling team + DT). Not in current implementation pass — design only.

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
