# Heimdall handover

**Audience:** a fresh Cursor agent (or human) with **no prior chat history**.  
**Date of this handover:** 2026-07-23  
**Purpose:** continue POC work tomorrow on another computer without losing product intent, paths, or gotchas.

---

## Canonical paths / remotes

| Role | Path / URL |
|------|------------|
| **Canonical local clone (RepoSync)** | `C:\Users\christopher.owen\Cursor\Heimdall` |
| **GitHub** | https://github.com/uberslaw/Heimdall |
| Older / stale copy (do not treat as source of truth) | `C:\Users\christopher.owen\Arup\Heimdall` |

**Always open the Cursor path** (`C:\Users\christopher.owen\Cursor\Heimdall`), or `git clone` / RepoSync from GitHub. Chat workspaces may still point at the Arup folder — that copy has historically been incomplete (e.g. no commits / older tree). Prefer Cursor\Heimdall.

User typically syncs with **RepoSync**, not necessarily GitHub Desktop.

---

## What Heimdall is

POC **workstation usage tracker** to justify modelling / remote machine cost versus **CADFX**.

Three pieces:

1. **Agent** — Windows Service that collects sessions, processes, heartbeats (and hardware inventory where implemented)
2. **ASP.NET Core Razor Pages API + dashboard** — ingest, config, analytics UI
3. **SQLite** — POC database (zero SQL Server install)

Goal: clearer session + app utilisation than CADFX, with server-side config and minimal agent overhead.

---

## Stack

| Piece | Tech |
|-------|------|
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
src/Heimdall.Shared   DTOs / contracts
scripts/              Installers, diagnostics, repair tools
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

Output under `%LOCALAPPDATA%\Heimdall\` (e.g. `diagnostics-*.zip`).

### Repair mojibake session usernames

If old session rows show garbled Windows account names:

```powershell
.\scripts\Repair-SessionMojibake.ps1
```

Also **restart the agent** after the encoding fix so new events are clean.

Full install detail: [INSTALL.md](INSTALL.md).

---

## Features shipped (summarize)

Dashboard nav (local working tree as of handover): Machines · Sessions · Applications · App lists · Cost · Stats · Teams · Utilization · Config · Socratize (from Machines).

| Area | What it does |
|------|----------------|
| **Machines** | Fleet view; **utilisation period** selectable **1 day → 1 year** (`?range=…`; default often 7d). Avg util / per-machine % for that window. |
| **Sessions** | Local vs RDP logons; start/end; active vs disconnected; Team column when mapped. |
| **Apps / Track Software** | Allowlisted / known / discovered / custom titles; scoped tracking (Region → Office → Machine tree). |
| **App lists + Analyze** | App lists with **approval-gated Analyze** (all / selected / team list). **No silent auto-track.** |
| **Config** | Scoped sampling / upload intervals, CPU floor, known apps, include/exclude, **SOE autogenerate**, **Browse…**, **Pause**, **metric threshold policies** (high RAM / GPU / disk) by All / Region / Office / Group / Machine. Agents refresh config ~every 5 minutes via `GET /api/config/{hostname}`. |
| **Teams** | CSV upload (+ manual CRUD); map Windows usernames → teams. Template on Teams page / `/templates/heimdall-teams-template.csv`. |
| **Stats** | Scoped analytics: logons, durations, app time/CPU rankings, RDP disconnected time, day-of-week patterns. GPU/disk columns wait on agent samples. |
| **Socratize** | Per-machine retrospective cost-justification Q&A from collected data (default ~30 days). **Flight Recorder** teaser parked (not built). |
| **Utilization** | Utilisation weights / related util configuration UI. |
| **Cost** | Purchase / warranty / hardware inventory; **WMI serial & hardware autodetection** fills blanks; manual edit wins. Dell/HP **warranty API** not wired (official APIs later — no scraping). |
| **Metric thresholds** | Defined in Config, delivered to agents; collection of some metrics still stubbed on agent. |

### Critical: local vs GitHub as of 2026-07-23

Much of the later surface (Cost, Utilization, App lists, hardware inventory collector, mojibake encoding helper, repair script, related API/agent/Shared edits) may still be **uncommitted in the Cursor working tree** when this handover was written. GitHub `main` may lag.

**Before relying on another PC:** commit + push all intended local work (see next steps), then `git pull` / RepoSync on the target machine. Do not assume `origin/main` already contains every “shipped” row above until verified.

---

## Known issues / gotchas

| Topic | Detail |
|-------|--------|
| **Mojibake usernames** | Encoding fix shipped in agent/Shared; **restart agent**. Repair historical DB rows with `scripts\Repair-SessionMojibake.ps1`. |
| **RDP vs local** | Classification is by **protocol**. RDP-to-self still counts as **RDP**. |
| **Browse…** | Browser-local file picker; typically **basename only** (not a full server-side path browser). |
| **GPU / disk samples** | Thresholds exist; agent sampling often still stubbed → Stats GPU/disk rankings empty until populated. |
| **Flight Recorder / Deep Observe** | Named + teased on Socratize; **backlog / not built**. |
| **Dell / HP warranty API** | Cost page + hardware autodetection; **official APIs later**. Do **not** scrape vendor sites. |
| **Process open / duration** | Times can look **huge** if duration is derived from `StartTime` of long-lived processes — verify/fix if still inflated. |
| **SQLite + `DateTimeOffset`** | Prefer **filter/order in memory** where EF/SQLite translation is unreliable. |
| **Wrong folder** | Cursor chat may open **Arup\Heimdall** — use **`C:\Users\christopher.owen\Cursor\Heimdall`**. |
| **POC auth** | API key only; dashboard is trusted-LAN / POC, not production identity. |

---

## Product decisions / naming to keep

1. **Socratize** = retrospective interrogation of *already collected* data for **one machine** (cost-justification brief). Keep the name.
2. **Flight Recorder / Deep Observe** = future **high-cardinality capture** while a watched process runs (e.g. `tuflow.exe` + network context), ring buffer → later AI / incident analysis. **Do not lose the name**; it is **not** the same as today’s Socratize. See [docs/BACKLOG.md](docs/BACKLOG.md).
3. **App analysis requires approval** (all / selected / team list) — **never** silently auto-track new software.
4. **No scraping HP/Dell** for warranty — use **official APIs** when keys/access exist.

---

## Suggested next steps for tomorrow

1. **Clone/pull on target PC** via RepoSync or `git clone https://github.com/uberslaw/Heimdall.git` — prefer path under Cursor or document the clone location.
2. **Install API + agent** on candidate machines (`scripts\Install-Api.cmd` / `Install-Agent.cmd`); use `Collect-Diagnostics.cmd` if anything fails.
3. **Push any uncommitted local work** from Cursor\Heimdall; verify **GitHub `main` is complete** (`git status`, compare to remote, spot-check Cost / App lists / Utilization / repair script).
4. If keys available: **wire Dell warranty API**; spike **Flight Recorder**; **fix process duration** if still inflated from long-lived `StartTime`.
5. **Demo vs CADFX** with real collected data (sessions, util period, Socratize brief, tracked apps).

---

## For the next Cursor agent

- Open folder **`C:\Users\christopher.owen\Cursor\Heimdall`**, or clone from https://github.com/uberslaw/Heimdall
- Read **HANDOVER.md** → **INSTALL.md** → **docs/BACKLOG.md** before coding
- Prefer **multitask / background agents** for large changes
- User uses **RepoSync**; do not assume GitHub Desktop
- Never update git config; no force-push; do not commit secrets
- Preserve product names: **Socratize**, **Flight Recorder / Deep Observe**
- Approval-gated Analyze; no HP/Dell scraping

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

# Mojibake repair
.\scripts\Repair-SessionMojibake.ps1
```

**GitHub file (after push):** https://github.com/uberslaw/Heimdall/blob/main/HANDOVER.md
