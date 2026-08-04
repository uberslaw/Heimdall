# Heimdall handover

**Audience:** a fresh Cursor agent (or human) with **no prior chat history**.  
**Date of this handover:** 2026-08-04  
**Purpose:** continue POC work on another computer without losing product intent, paths, or gotchas.

---

## Active work (read this first)

| Item | Status |
|------|--------|
| **Branch** | `main` (canonical). Prefer `origin/main` for cross-machine continuity. |
| **PR #4** | **Merged** 2026-07-31 — unified Heimdall Setup UX is on `main`. |
| **Docs audit** | In-app **Help** is the most complete product guide. Repo markdown (README / this file) was lagging Remote + Historical features — keep them aligned when shipping UI. |

### Setup UX (shipped on main)

- `scripts/Heimdall-Setup.cmd` (+ `.lnk` after `New-HeimdallShortcuts.cmd`) — **primary** guided Setup UI (API / create client pack / **push to PC via C$** / install agent)
- Steps panel: **Client install** (default) and **Server install** branches with click-through details
- `scripts/Heimdall-LaunchControl.*` — compat wrappers → same Setup UI
- `scripts/Pack-WorkstationCollector.cmd` — publishes self-contained `win-x64` agent into `dist/Heimdall-Client/`
- `scripts/Install.lnk` / `Install.cmd` — **only** entry clients need (inside the pack)
- `docs/portable-client/` — docs only (copied into pack as README/FILES)
- `Directory.Build.props` — shared `productVersion` 0.1.0; `/api/health` returns it for pack matching
- Repo-root `NuGet.config` → nuget.org
- Pack / `Install-Agent` publish force `--source https://api.nuget.org/v3/index.json`

### Folder model (simplified)

| Path | What it is |
|------|------------|
| `docs\portable-client\` | Docs only in git — **not** installable |
| `dist\Heimdall-Client\` | **The one folder** to copy after pack (`Install.lnk` + `payload\`) |

“Workstation collector” and “client install” are the same pack — not two folders to combine.

### NuGet on the pack PC (critical)

If `dotnet nuget list source` shows only **"Microsoft Visual Studio Offline Packages"**, pack fails with **NU1101**. Fix:

```powershell
cd C:\Users\christopher.owen\Cursor\Heimdall   # or C:\Heimdall
dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org
dotnet nuget list source
curl.exe -I https://api.nuget.org/v3/index.json
.\scripts\Heimdall-Setup.lnk   # Create client pack
```

- First pack can take **many minutes** (download win-x64 runtime packs).
- If `curl` to nuget.org fails → corporate proxy/firewall; need network allowlist or internal NuGet mirror.
- After SUCCESS, copy `dist\Heimdall-Client\` (or zip) to SOE PCs and run `Install.lnk`.

Prefer **portable pack** for other machines; `Install-Agent.cmd` is for full-repo + SDK on that PC.

---

## Canonical paths / remotes

| Role | Path / URL |
|------|------------|
| **Canonical local clone (RepoSync)** | `C:\Users\christopher.owen\Cursor\Heimdall` |
| **Also used** | `C:\Heimdall` — keep in sync with GitHub `main` |
| **GitHub** | https://github.com/uberslaw/Heimdall |
| Older / stale copy (do not treat as source of truth) | `C:\Users\christopher.owen\Arup\Heimdall` |

**Always open the Cursor path** (or a synced `C:\Heimdall` clone from GitHub).  
User typically syncs with **RepoSync**. Feature work usually lands on **`origin/main`**.

---

## What Heimdall is

POC **workstation usage tracker** to justify modelling / remote machine cost versus **CADFX**.

Three pieces:

1. **Agent** — Windows Service that collects sessions, processes, heartbeats, hardware inventory, OS install signals, MachineGuid / SMBIOS UUID; optional live resource sampling (Staff Access viewers) and 30s fleet snapshots (Historical Dashboard enrollment)
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
| Auth (POC) | API key header `X-Heimdall-Key` for agent ingest; Staff Access can use Windows Negotiate (see INSTALL.md) |

**Default POC API key:** `heimdall-poc-key` — change for anything beyond trusted-LAN POC. No Entra / AD website login for the full admin dashboard yet.

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
docs/portable-client/           Docs only — not the payload
dist/Heimdall-Client/           Created by pack (gitignored) — copy to SOE PCs
NuGet.config                    nuget.org (needed for pack/publish)
docs/BACKLOG.md                 Parked product ideas (Flight Recorder, etc.)
INSTALL.md                      Full install / verify / troubleshoot guide
HANDOVER.md                     This file
README.md                       Product overview + quick start
Heimdall.slnx                   Solution
```

Read first on a new machine: **this file**, then **[INSTALL.md](INSTALL.md)**, then dashboard **Admin → Help**, then **[docs/BACKLOG.md](docs/BACKLOG.md)**. For agent deploy: `docs/portable-client/README.md`.

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

Or use **Heimdall Setup** (`scripts\Heimdall-Setup.lnk`) for guided Client/Server steps.

### Other workstations / vanilla SOE (portable client)

```text
# Build PC (.NET 10 SDK + NuGet access to nuget.org or mirror):
scripts\Heimdall-Setup.lnk   # Create client pack
# or: scripts\Pack-WorkstationCollector.cmd

# Copy dist\Heimdall-Client\ (or zip) to each PC, then:
Install.lnk
```

- Install / service logs: `%ProgramData%\Heimdall\logs\`
- SQLite DB (typical): `%ProgramData%\Heimdall\heimdall.db`
- Agent offline queue: `%ProgramData%\Heimdall\queue.db`
- API default listen: `http://0.0.0.0:5080` (allow inbound TCP 5080 if agents are remote)

Full install detail: [INSTALL.md](INSTALL.md).

---

## Features shipped (summarize)

**Dashboard nav (main):**

| Menu | Pages |
|------|--------|
| **Machines** | All machines, Sessions |
| **Applications** | Applications, App lists, Discovery, Socratize, Track Software |
| **Remote** | Remote Machines, Historical Dashboard, Staff Access |
| **Admin** | Tracking config, Teams, Utilization criteria, Cost, Stats, Clients, Remote Access Groups, Help, Database mode (Live/Sandbox), Theme |

In-app **Admin → Help** has page-by-page operator docs (preferred over README for UI detail).

| Area | What it does |
|------|----------------|
| **Machines** | Fleet view; utilisation period; **Reimaged** badge when MachineGuid changed |
| **Sessions** | Local vs RDP logons; start/end; active vs disconnected; Team column when mapped; session drilldown |
| **Apps / Track Software** | Allowlisted / known / discovered / custom titles; scoped tracking |
| **App lists + Analyze** | Approval-gated Analyze — **no silent auto-track**; inventory request; classification CSV AI workflow |
| **Discovery** | Full process catalog (name+path); edit friendly name/version/category; installs + frequency |
| **Config** | Sampling, known apps, SOE autogenerate, metric thresholds, pause |
| **Teams** | CSV upload + CRUD; username → team |
| **Stats** | Scoped analytics (logons, apps, RDP disconnected, patterns) |
| **Socratize** | Per-machine cost-justification Q&A from collected data |
| **Utilization** | Utilisation weights; **app license $/yr** (secondary to hardware cost) |
| **Cost** | **Hardware purchase cost** focus; user vs **`ops.` support hours** (30d); optional SupportHourlyRate; WMI hardware autodetection + manual; **PSU watts manual only**; BIOS vs hostname asset serial; OS install + Windows folder created dates; reimage identity history |
| **Remote Machines** | RDP/RDS health, ping from API host, Connect `.rdp`, Restart RDS via agent queue |
| **Historical Dashboard** | Enroll hosts → always-on **30s** fleet snapshots; Live Fleet + historical analytics (TUFLOW-oriented POC) |
| **Staff Access** | Restricted live metrics for staff in Remote Access Groups; optional Windows Negotiate |
| **Remote Access Groups** | Admin: staff email ↔ machine membership (+ favourite processes) |
| **Clients** | Agent version per host vs published pack version |
| **Theme / DB mode** | Custom themes; Live vs Sandbox (`heimdall-dev.db`) browse toggle — ingest always Live |
| **Metric thresholds** | Config → agents; per-process ProcessRun GPU/disk columns still often empty; **live/fleet sampling** populates Historical + Staff views |

### Hardware / identity

| Signal | Auto (agent) | Manual | Notes |
|--------|--------------|--------|-------|
| Brand, Model, CPU, GPU, RAM, Disk | Yes (WMI) | Yes | Manual override blocks agent fills |
| Serial | BIOS + **hostname parse** | Yes | Pattern: 3-letter city + optional DT/LT + serial. Config: `Heimdall:HostnameSerialPattern` |
| **PSU rated W** | No | Yes (`PsuWatts`) | Not in WMI |
| **Power draw W** | No | Stub field only | Not reliable via agent POC |
| OS install date | WMI/registry | — | May move on feature update |
| Windows folder created | `%SystemRoot%` created | — | Often closer to original image |
| MachineGuid | Registry | — | Changes on **reimage** |
| SmbiosUuid | WMI | — | Usually survives reimage |
| Support hours | From sessions | Rate optional | Username `ops.*` / domain `OPS` |

---

## Known issues / gotchas

| Topic | Detail |
|-------|--------|
| **NuGet offline-only** | Pack/publish fails NU1101 if only VS Offline Packages. Need nuget.org (or mirror). |
| **Docs vs pack folder** | `docs\portable-client\` = docs; `dist\Heimdall-Client\` = real pack after SUCCESS. |
| **PS 5.1 + UTF-8** | Em-dashes without BOM broke install scripts historically; pack scripts use BOM/ASCII-safe text. |
| **Mojibake usernames** | Encoding fix shipped; **restart agent**. Repair DB with `scripts\Repair-SessionMojibake.ps1`. |
| **RDP vs local** | Classify by **protocol** first (then RDP-/ICA- WinStation, then ClientName/Address). Console alone must not force Local. |
| **PSU / power draw** | Rated wattage = manual. Live draw = **not** collected. |
| **OS InstallDate** | Feature updates often rewrite WMI/registry InstallDate — show both signals on Cost. |
| **ProcessRun GPU/disk** | Stats ranking columns for ProcessRun peaks may stay empty; use Historical Dashboard / Staff live sampling for GPU/disk util. |
| **Dell / HP warranty API** | Not wired — official APIs later; **no scraping**. |
| **SQLite + DateTimeOffset** | Prefer filter/order in memory where EF translation is unreliable. |
| **Wrong folder** | Prefer **`C:\Users\christopher.owen\Cursor\Heimdall`**, not Arup. |
| **POC auth** | Agent = API key; admin dashboard = open on LAN; Staff Access optional Windows auth (INSTALL.md). |
| **Live vs sandbox** | Do not run `dotnet run` on :5080 while `HeimdallApi` service also listens — two processes, not a toggle. Use Admin → Database mode. |

---

## Product decisions / naming to keep

1. **Socratize** = retrospective interrogation of *already collected* data for **one machine**.
2. **Flight Recorder / Deep Observe** = future high-cardinality *incident* capture — **backlog / not built**. Distinct from shipped **Historical Dashboard** (30s fleet snapshots). See [docs/BACKLOG.md](docs/BACKLOG.md).
3. **App analysis requires approval** — never silently auto-track.
4. **No scraping HP/Dell** for warranty.
5. **Hardware cost** is the primary Cost-page story; app license $/yr lives on Utilization.

---

## Suggested next steps

1. Deploy/refresh portable pack on SOE boxes; confirm Machines + Clients versions.
2. Enroll modelling hosts on **Historical Dashboard** if TUFLOW fleet visibility is needed.
3. Configure **Remote Access Groups** + Staff Access Windows auth for non-admin viewers (INSTALL.md).
4. Run `Inspect-SoeInstalledPrograms` on a golden image for program-list excludes.
5. Later backlog: Dell warranty API (if keys); Flight Recorder spike; CADFX demo with purchase cost + support hours + Socratize.

---

## For the next Cursor agent

- Open **`C:\Users\christopher.owen\Cursor\Heimdall`** (or synced **`C:\Heimdall`**) on **`main`**
- Read **HANDOVER.md** → **INSTALL.md** → dashboard **Help** → **docs/BACKLOG.md**
- Unblock pack if still blocked: NuGet/network first — do not reinvent the pack scripts unless broken
- User often wants **commit + push** to `origin/main` for RepoSync continuity
- Never update git config; no force-push; do not commit secrets
- Preserve names: **Socratize**, **Flight Recorder / Deep Observe**
- Prefer **CMD** installers for SOE targets (avoid PowerShell on locked-down images)

---

## Quick reference commands

```powershell
cd C:\Users\christopher.owen\Cursor\Heimdall   # or C:\Heimdall
git status
dotnet nuget list source
.\scripts\New-HeimdallShortcuts.cmd
.\scripts\Heimdall-Setup.lnk

# Dev API / agent
cd src\Heimdall.Api;  dotnet run --urls http://localhost:5080
cd src\Heimdall.Agent; dotnet run

# Elevated installers
.\scripts\Install-Api.cmd
.\scripts\Install-Agent.cmd
.\scripts\Collect-Diagnostics.cmd
.\scripts\Inspect-SoeInstalledPrograms.cmd

# Packed client on another PC:
.\Install.lnk

# Mojibake repair
.\scripts\Repair-SessionMojibake.ps1
```

**GitHub:** https://github.com/uberslaw/Heimdall  
**This file on main:** https://github.com/uberslaw/Heimdall/blob/main/HANDOVER.md
