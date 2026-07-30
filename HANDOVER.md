# Heimdall handover

**Audience:** a fresh Cursor agent (or human) with **no prior chat history**.  
**Date of this handover:** 2026-07-24 (late)  
**Purpose:** continue POC work on another computer without losing product intent, paths, or gotchas.

---

## Active work (read this first)

| Item | Status |
|------|--------|
| **Branch** | `cursor/workstation-collector-pack-1eb8` (not merged to `main` yet) |
| **PR** | https://github.com/uberslaw/Heimdall/pull/1 — portable workstation collector pack |
| **Goal** | Pack a self-contained agent once → copy folder to other PCs → install vanilla SOE collectors without PowerShell / SDK / full repo on targets |
| **User blocker** | Pack PC NuGet only had **Visual Studio Offline Packages** (no nuget.org). First self-contained publish was slow / may still be running or failed. Confirm `dist\workstation-collector\payload\Heimdall.Agent.exe` exists. |

### What shipped on this branch

- `scripts/Heimdall-LaunchControl.lnk` (+ `.cmd` / `.ps1`) — guided WinForms setup; **prefer `.lnk`** for helmet icon in Explorer
- `assets/heimdall.ico` — helmet icon copied into pack; shortcuts created at pack time
- `scripts/Pack-WorkstationCollector.cmd` — publishes self-contained `win-x64` agent into `dist/workstation-collector/` (includes Launch Control + `VERSION.json` + icon shortcuts)
- `scripts/Install-WorkstationCollector.cmd` — **CMD-only** elevated installer (self-elevate + pause; opens Launch Control when double-clicked with no args)
- `scripts/workstation-collector/README.md` + `FILES.md` — files + dependencies
- `Directory.Build.props` — shared `productVersion` 0.1.0; `/api/health` returns it for pack matching
- Repo-root `NuGet.config` → nuget.org
- Pack / `Install-Agent` publish now **force** `--source https://api.nuget.org/v3/index.json`
- Fixed Windows PowerShell 5.1 parse error: UTF-8 em-dashes without BOM → ASCII dashes + UTF-8 BOM on `scripts/*.ps1`

### Folder confusion (user hit this)

| Path | What it is |
|------|------------|
| `scripts\workstation-collector\` | **Docs only** in git (`README.md` + `FILES.md`) — **not** installable |
| `dist\workstation-collector\` | Created by a **successful** pack — copy **this** to other PCs (`Install-WorkstationCollector.cmd` + `payload\`) |

If a folder only has README/FILES and no `payload\`, pack has not succeeded.

**Why pack before client install:** installers never compile — they only deploy `payload\Heimdall.Agent.exe` (self-contained) so vanilla SOE boxes need no SDK. See `INSTALL.md` § “Why packing is required before client install”.

### NuGet on the pack PC (critical)

User ran on **`C:\Heimdall`** (also uses Cursor path below):

```text
dotnet nuget list source
→ only "Microsoft Visual Studio Offline Packages"
```

That causes **NU1101** (cannot find Sqlite / runtime packs). Remediation already attempted / documented:

```powershell
cd C:\Heimdall   # or Cursor\Heimdall — must be on branch cursor/workstation-collector-pack-1eb8
dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org
dotnet nuget list source
curl.exe -I https://api.nuget.org/v3/index.json
.\scripts\Pack-WorkstationCollector.cmd
```

- First pack can take **many minutes** (download win-x64 runtime packs). OK if console still prints / `dotnet` is active in Task Manager.
- If `curl` to nuget.org fails → corporate proxy/firewall; need network allowlist or internal NuGet mirror.
- After SUCCESS, copy `dist\workstation-collector\` (or zip) to SOE PCs and run elevated:
  `Install-WorkstationCollector.cmd -ApiUrl http://SERVER:5080 -MachineGroup SOE`

### Install-Agent.ps1 parse error (fixed on branch)

```text
The Try statement is missing its Catch or Finally block.
```

Cause: Windows PowerShell 5.1 mis-decoded UTF-8 scripts with em-dashes and no BOM. Fixed on this branch — user must **sync/pull** before re-running `Install-Agent.cmd`.

Prefer **portable pack** for other machines; `Install-Agent.cmd` is for full-repo + SDK on that PC.

---

## Canonical paths / remotes

| Role | Path / URL |
|------|------------|
| **Canonical local clone (RepoSync)** | `C:\Users\christopher.owen\Cursor\Heimdall` |
| **Also used this session** | `C:\Heimdall` — same repo intent; keep in sync with GitHub branch |
| **GitHub** | https://github.com/uberslaw/Heimdall |
| Older / stale copy (do not treat as source of truth) | `C:\Users\christopher.owen\Arup\Heimdall` |

**Always open the Cursor path** (or a synced `C:\Heimdall` clone from GitHub). Prefer Cursor\Heimdall when unsure.

User typically syncs with **RepoSync**, not necessarily GitHub Desktop. **Feature work usually lands on `origin/main`** for cross-machine continuity — **this slice is still on the PR branch** until merged.

---

## What Heimdall is

POC **workstation usage tracker** to justify modelling / remote machine cost versus **CADFX**.

Three pieces:

1. **Agent** — Windows Service that collects sessions, processes, heartbeats, hardware inventory, OS install signals, MachineGuid / SMBIOS UUID
2. **ASP.NET Core Razor Pages API + dashboard** — ingest, config, analytics UI
3. **SQLite** — POC database (zero SQL Server install)

Goal: clearer session + app utilisation than CADFX, with **hardware purchase cost** and support-time context — not just app license $/yr.

---

## Stack

| Piece | Tech |
|------|------|
| Runtime / SDK | **.NET 10** |
| Agent | .NET 10 **Windows Service** |
| API + dashboard | ASP.NET Core **Razor Pages** |
| DB (POC) | **SQLite** |
| Auth (POC) | API key header `X-Heimdall-Key` |

**Default POC API key:** `heimdall-poc-key` — change for anything beyond trusted-LAN POC. No Entra / AD website login yet.

Live repo: https://github.com/uberslaw/Heimdall

---

## Repo layout

```
src/Heimdall.Agent              Windows service collector
src/Heimdall.Api                Ingest API + Razor dashboard
src/Heimdall.Shared             DTOs / contracts / hostname serial + ops. helpers
scripts/                        Installers, diagnostics, SOE inspect, repair tools
scripts/Pack-WorkstationCollector.cmd
scripts/Install-WorkstationCollector.cmd
scripts/workstation-collector/  Docs only (README + FILES) — not the payload
dist/workstation-collector/     Created by pack (gitignored) — copy to SOE PCs
NuGet.config                    nuget.org (needed for pack/publish)
docs/BACKLOG.md                 Parked product ideas (Flight Recorder, etc.)
INSTALL.md                      Full install / verify / troubleshoot guide
HANDOVER.md                     This file
README.md                       Product overview + quick start
Heimdall.slnx                   Solution
```

Read first on a new machine: **this file**, then **[INSTALL.md](INSTALL.md)**, then **[docs/BACKLOG.md](docs/BACKLOG.md)**, then `scripts/workstation-collector/README.md` if deploying agents to other PCs.

---

## How to run / install

### Dev

```powershell
# Terminal 1 — API + dashboard
cd src\Heimdall.Api
dotnet run --urls http://localhost:5080

# Terminal 2 — Agent (console)
cd src\Heimdall.Agent
dotnet run
```

Open http://localhost:5080  
Default key: `heimdall-poc-key` (`X-Heimdall-Key`).

### Prod-ish (Windows services)

Run **elevated** (prefer `.cmd` so the console stays open when double-clicked):

```text
scripts\Install-Api.cmd
scripts\Install-Agent.cmd
```

### Other workstations / vanilla SOE (portable collector)

```text
# Build PC (.NET 10 SDK + NuGet access to nuget.org or mirror):
scripts\Pack-WorkstationCollector.cmd

# Copy dist\workstation-collector\ (or zip) to each PC, then elevated:
Install-WorkstationCollector.cmd -ApiUrl http://SERVER:5080 -MachineGroup SOE
```

- Installer on target is **CMD only** (no PowerShell).
- Payload is **self-contained win-x64** — no SDK/repo on target.
- Files + deps: `scripts\workstation-collector\README.md` and `FILES.md`.

- Install / service logs: `%ProgramData%\Heimdall\logs\`
- SQLite DB (typical): `%ProgramData%\Heimdall\heimdall.db`
- Agent offline queue: `%ProgramData%\Heimdall\queue.db`
- API default listen: `http://0.0.0.0:5080` (allow inbound TCP 5080 if agents are remote)

### Diagnostics zip

```text
scripts\Collect-Diagnostics.cmd
```

### SOE golden-image program list

```text
scripts\Inspect-SoeInstalledPrograms.cmd
```

CSV + log under `%LOCALAPPDATA%\Heimdall\`. Review → Config SOE excludes / Autogenerate. See [INSTALL.md](INSTALL.md). Separate from the live collector pack.

### Repair mojibake session usernames

```powershell
.\scripts\Repair-SessionMojibake.ps1
```

Also **restart the agent** after the encoding fix so new events are clean.

Full install detail: [INSTALL.md](INSTALL.md).

---

## Features shipped (summarize)

Dashboard nav (on `main`): Machines | Sessions | Applications | App lists | Cost | Stats | Teams | Utilization | Config | Socratize (from Machines).

| Area | What it does |
|------|----------------|
| **Machines** | Fleet view; utilisation period; **Reimaged** badge when MachineGuid changed |
| **Sessions** | Local vs RDP logons; start/end; active vs disconnected; Team column when mapped |
| **Apps / Track Software** | Allowlisted / known / discovered / custom titles; scoped tracking |
| **App lists + Analyze** | Approval-gated Analyze — **no silent auto-track** |
| **Config** | Sampling, known apps, SOE autogenerate, metric thresholds, pause |
| **Teams** | CSV upload + CRUD; username → team |
| **Stats** | Scoped analytics (logons, apps, RDP disconnected, patterns) |
| **Socratize** | Per-machine cost-justification Q&A from collected data |
| **Utilization** | Utilisation weights; **app license $/yr** (secondary to hardware cost) |
| **Cost** | **Hardware purchase cost** focus; user vs **`ops.` support hours** (30d); optional SupportHourlyRate; WMI hardware autodetection + manual; **PSU watts manual only**; BIOS vs hostname asset serial; OS install + Windows folder created dates; reimage identity history |
| **Metric thresholds** | Config → agents; some metric sampling still stubbed |

### Hardware / identity (2026-07-24)

| Signal | Auto (agent) | Manual | Notes |
|--------|--------------|--------|-------|
| Brand, Model, CPU, GPU, RAM, Disk | Yes (WMI) | Yes | Manual override blocks agent fills |
| Serial | BIOS + **hostname parse** | Yes | Pattern: 3-letter city + optional DT/LT + serial (`BNEDT…`). Config: `Heimdall:HostnameSerialPattern`. Prefer hostname when BIOS is OEM placeholder. BIOS kept separately |
| **PSU rated W** | No | Yes (`PsuWatts`) | Not in WMI |
| **Power draw W** | No | Stub field only | NVML/RAPL vendor-specific — **not reliable via agent** |
| OS install date | WMI/registry | — | May move on feature update |
| Windows folder created | `%SystemRoot%` created | — | Often closer to original image |
| MachineGuid | Registry | — | Changes on **reimage** |
| SmbiosUuid | WMI | — | Usually survives reimage |
| Support hours | From sessions | Rate optional | Username `ops.*` / domain `OPS` |

---

## Known issues / gotchas

| Topic | Detail |
|-------|--------|
| **NuGet offline-only** | Pack/publish fails NU1101 if only VS Offline Packages. Need nuget.org (or mirror) + HTTPS to `api.nuget.org`. Pack script forces `--source` nuget.org. |
| **Docs vs pack folder** | `scripts\workstation-collector\` = docs; `dist\workstation-collector\` = real pack after SUCCESS. |
| **PS 5.1 + UTF-8** | Em-dashes without BOM broke `install-agent.ps1` parse; fixed on this branch (BOM + ASCII). |
| **Mojibake usernames** | Encoding fix shipped; **restart agent**. Repair DB with `scripts\Repair-SessionMojibake.ps1`. |
| **RDP vs local** | Classify by **protocol** first (then RDP-/ICA- WinStation, then ClientName/Address). Console alone must not force Local. Session time splits into Local / Inbound RDP buckets. Outbound RDP = mstsc/msrdc/msrdcw process open time. RDP-to-self still **inbound RDP**. |
| **PSU / power draw** | Rated wattage = manual. Live draw = **not** collected (impossible reliably for desktops via agent POC). |
| **OS InstallDate** | Feature updates often rewrite WMI/registry InstallDate — show both signals on Cost. |
| **GPU / disk samples** | Thresholds exist; agent sampling often still stubbed. |
| **Dell / HP warranty API** | Not wired — official APIs later; **no scraping**. |
| **SQLite + DateTimeOffset** | Prefer filter/order in memory where EF translation is unreliable. |
| **Wrong folder** | Prefer **`C:\Users\christopher.owen\Cursor\Heimdall`**, not Arup. `C:\Heimdall` also used — keep synced. |
| **POC auth** | API key only; trusted-LAN / POC. |
| **PR not on main** | Workstation collector pack lives on `cursor/workstation-collector-pack-1eb8` until merged. |

---

## Product decisions / naming to keep

1. **Socratize** = retrospective interrogation of *already collected* data for **one machine**.
2. **Flight Recorder / Deep Observe** = future high-cardinality capture — **backlog / not built**. See [docs/BACKLOG.md](docs/BACKLOG.md).
3. **App analysis requires approval** — never silently auto-track.
4. **No scraping HP/Dell** for warranty.
5. **Hardware cost** is the primary Cost-page story; app license $/yr lives on Utilization.

---

## Suggested next steps

1. On pack PC: confirm nuget.org reachable; finish `Pack-WorkstationCollector.cmd`; verify `dist\workstation-collector\payload\Heimdall.Agent.exe`.
2. Deploy pack to one vanilla SOE box (`Install-WorkstationCollector.cmd -ApiUrl … -MachineGroup SOE`); confirm machine appears on dashboard after heartbeat.
3. Merge PR #1 to `main` (or push continuity to `main` per user preference) so RepoSync picks it up everywhere.
4. Run `Inspect-SoeInstalledPrograms` on a golden image for program-list excludes.
5. Redeploy API + agent so heartbeat fields (Guid, OS dates, hostname serial) populate on the server side.
6. Later backlog: Dell warranty API (if keys); Flight Recorder spike; inflated process durations if still an issue; CADFX demo with purchase cost + support hours + Socratize.

---

## For the next Cursor agent

- Checkout / open branch **`cursor/workstation-collector-pack-1eb8`** (or `main` after merge)
- Paths: **`C:\Users\christopher.owen\Cursor\Heimdall`** or synced **`C:\Heimdall`**
- Read **HANDOVER.md** → **INSTALL.md** → **`scripts/workstation-collector/README.md`** → **docs/BACKLOG.md**
- Unblock pack if still blocked: NuGet/network first — do not reinvent the pack scripts unless broken
- User wants **commit + push** for cross-machine continuity; merge to **`origin/main`** when the slice is ready
- Never update git config; no force-push; do not commit secrets
- Preserve names: **Socratize**, **Flight Recorder / Deep Observe**
- Prefer **CMD** installers for SOE targets (avoid PowerShell on locked-down images)

---

## Quick reference commands

```powershell
# Ensure branch + NuGet (pack PC)
cd C:\Heimdall   # or Cursor\Heimdall
git status
dotnet nuget list source
dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org
curl.exe -I https://api.nuget.org/v3/index.json
.\scripts\Pack-WorkstationCollector.cmd

# Dev API
cd src\Heimdall.Api
dotnet run --urls http://localhost:5080

# Dev agent
cd src\Heimdall.Agent
dotnet run

# Elevated installers (from repo root)
.\scripts\Install-Api.cmd
.\scripts\Install-Agent.cmd          # needs SDK on that PC
.\scripts\Collect-Diagnostics.cmd
.\scripts\Inspect-SoeInstalledPrograms.cmd

# On a packed folder copied to another PC (elevated):
.\Install-WorkstationCollector.cmd -ApiUrl http://SERVER:5080 -MachineGroup SOE

# Mojibake repair
.\scripts\Repair-SessionMojibake.ps1
```

**GitHub (branch file until merge):** https://github.com/uberslaw/Heimdall/blob/cursor/workstation-collector-pack-1eb8/HANDOVER.md  
**PR:** https://github.com/uberslaw/Heimdall/pull/1
