# Heimdall handover

**Audience:** a fresh Cursor agent (or human) with **no prior chat history**.  
**Date of this handover:** 2026-07-24  
**Purpose:** continue POC work on another computer without losing product intent, paths, or gotchas.

---

## Canonical paths / remotes

| Role | Path / URL |
|------|------------|
| **Canonical local clone (RepoSync)** | `C:\Users\christopher.owen\Cursor\Heimdall` |
| **GitHub** | https://github.com/uberslaw/Heimdall |
| Older / stale copy (do not treat as source of truth) | `C:\Users\christopher.owen\Arup\Heimdall` |

**Always open the Cursor path** (`C:\Users\christopher.owen\Cursor\Heimdall`), or `git clone` / RepoSync from GitHub. Chat workspaces may still point at the Arup folder — that copy has historically been incomplete. Prefer Cursor\Heimdall.

User typically syncs with **RepoSync**, not necessarily GitHub Desktop. **Feature work lands on `origin/main`** for cross-machine continuity.

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
src/Heimdall.Agent    Windows service collector
src/Heimdall.Api      Ingest API + Razor dashboard
src/Heimdall.Shared   DTOs / contracts / hostname serial + ops. helpers
scripts/              Installers, diagnostics, SOE inspect, repair tools
docs/BACKLOG.md       Parked product ideas (Flight Recorder, etc.)
INSTALL.md            Full install / verify / troubleshoot guide
HANDOVER.md           This file
README.md             Product overview + quick start
Heimdall.slnx         Solution
```

Read first on a new machine: **this file**, then **[INSTALL.md](INSTALL.md)**, then **[docs/BACKLOG.md](docs/BACKLOG.md)**.

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

- Install / service logs: `%ProgramData%\Heimdall\logs\`
- SQLite DB (typical): `%ProgramData%\Heimdall\heimdall.db`
- API default listen: `http://0.0.0.0:5080` (allow inbound TCP 5080 if agents are remote)

### Diagnostics zip

```text
scripts\Collect-Diagnostics.cmd
```

### SOE golden-image program list

```text
scripts\Inspect-SoeInstalledPrograms.cmd
```

CSV + log under `%LOCALAPPDATA%\Heimdall\`. Review → Config SOE excludes / Autogenerate. See [INSTALL.md](INSTALL.md).

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
| **Mojibake usernames** | Encoding fix shipped; **restart agent**. Repair DB with `scripts\Repair-SessionMojibake.ps1`. |
| **RDP vs local** | Classification by **protocol**. RDP-to-self still **RDP**. |
| **PSU / power draw** | Rated wattage = manual. Live draw = **not** collected (impossible reliably for desktops via agent POC). |
| **OS InstallDate** | Feature updates often rewrite WMI/registry InstallDate — show both signals on Cost. |
| **GPU / disk samples** | Thresholds exist; agent sampling often still stubbed. |
| **Dell / HP warranty API** | Not wired — official APIs later; **no scraping**. |
| **SQLite + DateTimeOffset** | Prefer filter/order in memory where EF translation is unreliable. |
| **Wrong folder** | Use **`C:\Users\christopher.owen\Cursor\Heimdall`**, not Arup. |
| **POC auth** | API key only; trusted-LAN / POC. |

---

## Product decisions / naming to keep

1. **Socratize** = retrospective interrogation of *already collected* data for **one machine**.
2. **Flight Recorder / Deep Observe** = future high-cardinality capture — **backlog / not built**. See [docs/BACKLOG.md](docs/BACKLOG.md).
3. **App analysis requires approval** — never silently auto-track.
4. **No scraping HP/Dell** for warranty.
5. **Hardware cost** is the primary Cost-page story; app license $/yr lives on Utilization.

---

## Suggested next steps

1. Pull / RepoSync `origin/main` on the next PC; open Cursor\Heimdall.
2. Redeploy API + agent so new heartbeat fields (Guid, OS dates, hostname serial) populate.
3. Run `Inspect-SoeInstalledPrograms` on a golden image; feed SOE excludes.
4. If keys available: Dell warranty API; Flight Recorder spike; fix inflated process durations if still an issue.
5. Demo vs CADFX with real purchase cost + support hours + Socratize.

---

## For the next Cursor agent

- Open **`C:\Users\christopher.owen\Cursor\Heimdall`** (or clone from GitHub)
- Read **HANDOVER.md** → **INSTALL.md** → **docs/BACKLOG.md**
- User wants **commit + push to `origin/main`** for cross-machine continuity when finishing a slice
- Never update git config; no force-push; do not commit secrets
- Preserve names: **Socratize**, **Flight Recorder / Deep Observe**

---

## Quick reference commands

```powershell
# Dev API
cd C:\Users\christopher.owen\Cursor\Heimdall\src\Heimdall.Api
dotnet run --urls http://localhost:5080

# Dev agent
cd C:\Users\christopher.owen\Cursor\Heimdall\src\Heimdall.Agent
dotnet run

# Elevated installers (from repo root)
.\scripts\Install-Api.cmd
.\scripts\Install-Agent.cmd
.\scripts\Collect-Diagnostics.cmd
.\scripts\Inspect-SoeInstalledPrograms.cmd

# Mojibake repair
.\scripts\Repair-SessionMojibake.ps1
```

**GitHub file (after push):** https://github.com/uberslaw/Heimdall/blob/main/HANDOVER.md
