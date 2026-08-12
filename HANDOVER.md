# Heimdall handover

**Audience:** a fresh Cursor agent (or human) with **no prior chat history**.  
**Date of this handover:** 2026-08-12  
**Purpose:** continue POC work on another machine without losing product intent, paths, or gotchas.

---

## Start here (documentation map)

| Read first | Contents |
|------------|----------|
| **This file** | Product intent, paths, nav, gotchas, what shipped |
| **[INSTALL.md](INSTALL.md)** | Server + client install, verify, troubleshoot, Staff auth, Entra secrets |
| **[docs/CLIENT.md](docs/CLIENT.md)** | How the agent works — loops, endpoints, fleet sampling, silent deploy |
| **Dashboard → Admin → Help** | Page-by-page operator guide (most up to date for UI) |
| **[README.md](README.md)** | Short product overview + dev quick start |
| **[docs/BACKLOG.md](docs/BACKLOG.md)** | Parked ideas (Flight Recorder, etc.) |
| **[AGENTS.md](AGENTS.md)** | Cursor Cloud VM constraints (Linux API-only) |

---

## Active work (read this first)

| Item | Status |
|------|--------|
| **Branch** | `main` (canonical). Prefer `origin/main` for cross-machine continuity. |
| **Historical Dashboard / Flood hub** | **Shipped** — always-on 30s fleet snapshots for all clients; Flood enrollment gates TUFLOW control + Flood-scoped analytics only |
| **Fleet console** | **Shipped** — `/Fleet` shell with lazy tabs (Computers, Live, Sessions, Online status, Client version, Cost, Stats) |
| **Client silent deploy** | **Shipped** — Fleet → Client version; baseline integer version **3** = first `UpdateClient`-capable build |
| **Docs audit** | In-app **Help** + `docs/CLIENT.md` are the operator references; keep repo markdown aligned when shipping UI |

### Setup UX (on main)

- `scripts/Heimdall-Setup.cmd` (+ `.lnk` after `New-HeimdallShortcuts.cmd`) — **primary** guided Setup UI (API / create client pack / **push to PC via C$** / install agent)
- Steps panel: **Client install** (default) and **Server install** branches
- `scripts/Heimdall-LaunchControl.*` — compat wrappers → same Setup UI
- `scripts/Pack-WorkstationCollector.cmd` — publishes self-contained `win-x64` agent into `dist/Heimdall-Client/`
- `scripts/Install.lnk` / `Install.cmd` — **only** entry clients need (inside the pack)
- `docs/portable-client/` — docs only (copied into pack as README/FILES)
- `docs/CLIENT.md` — full agent architecture (also summarized in Help → Client / agent)

### Folder model

| Path | What it is |
|------|------------|
| `docs\portable-client\` | Docs only in git — **not** installable |
| `dist\Heimdall-Client\` | **The one folder** to copy after pack (`Install.lnk` + `payload\`) |

“Workstation collector” and “client install” are the same pack.

### NuGet on the pack PC (critical)

If `dotnet nuget list source` shows only **"Microsoft Visual Studio Offline Packages"**, pack fails with **NU1101**. Fix:

```powershell
cd C:\Heimdall   # or C:\Users\christopher.owen\Cursor\Heimdall
dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org
.\scripts\Heimdall-Setup.lnk   # Create client pack
```

---

## Canonical paths / remotes

| Role | Path / URL |
|------|------------|
| **Canonical local clone (RepoSync)** | `C:\Users\christopher.owen\Cursor\Heimdall` |
| **Also used** | `C:\Heimdall` — keep in sync with GitHub `main` |
| **GitHub** | https://github.com/uberslaw/Heimdall |
| **Typical prod API** | `http://BNELT5CG5152D8R:5080` (hostname, not localhost when service runs) |
| **Prod SQLite** | `%ProgramData%\Heimdall\heimdall.db` |

**Always open the Cursor path** (or a synced `C:\Heimdall` clone from GitHub).

---

## What Heimdall is

POC **workstation usage tracker** to justify modelling / remote machine cost versus **CADFX**.

Three pieces:

1. **Agent** — Windows Service: sessions, processes, heartbeats, hardware, **30s fleet snapshots** (all clients), optional Staff live sampling, TUFLOW launcher on Flood-enrolled hosts
2. **ASP.NET Core Razor Pages API + dashboard** — ingest, config, analytics UI
3. **SQLite** — POC database (no SQL Server)

Goal: clearer session + app utilisation than CADFX, with **hardware purchase cost** and support-time context.

---

## Stack

| Piece | Tech |
|------|------|
| Runtime / SDK | **.NET 10** |
| Agent | .NET 10 **Windows Service** |
| API + dashboard | ASP.NET Core **Razor Pages** |
| DB (POC) | **SQLite** (`EnsureCreated` + additive schema patches — **no EF migrations**) |
| Auth (POC) | API key `X-Heimdall-Key` for agent; Staff Access optional Windows Negotiate; Entra Graph optional for **Teams membership** (not site SSO) |

**Default POC API key:** `heimdall-poc-key`

---

## Repo layout

```
src/Heimdall.Agent              Windows service collector
src/Heimdall.Api                Ingest API + Razor dashboard
src/Heimdall.Shared             DTOs / contracts
scripts/                        Installers, Setup, pack, diagnostics
docs/CLIENT.md                  Agent architecture (for agents + humans)
docs/portable-client/           Pack docs only
dist/Heimdall-Client/           Created by pack (gitignored)
INSTALL.md                      Install guide
HANDOVER.md                     This file
Heimdall.slnx                   Solution
```

---

## Dashboard navigation (current)

| Menu | Pages / tabs |
|------|----------------|
| **Fleet** | Computers, Live, Sessions, Online status, Client version, Cost, Stats (`/Fleet?tab=…`) |
| **Applications** | App lists, Application Usage, Discovery, Socratize, Teams |
| **Staff** | Staff Access (RDP pool + bookings) |
| **Flood** (gated) | Flood hub: Live, Historical, Fleet Sims, Enrollment; plus **TUFLOW Runs** |
| **Admin** | Tracking config, Utilization, Finance, Auth, Remote Access Groups, Help, Theme, Database mode |

Legacy URLs (`/`, `/Ops`, `/Clients`, `/HistoricalDashboard`, …) redirect into Fleet or Flood tabs.

---

## Data model highlights (for agents editing code)

| Concept | Table / service | Notes |
|---------|-----------------|-------|
| Machines, sessions, process runs | `Machines`, `UserSessions`, `ProcessRuns` | Core ingest |
| Fleet snapshots | `FleetMetricSnapshots` | Append-only 30s rows; `FleetDashboardService`, retention hosted service |
| Flood allowlist | `FleetDashboardMachines` | **Not** required for sampling — gates TUFLOW + Flood UI |
| App tracking | App lists + assignments | Primary include source; Tracking Config legacy includes may remain |
| Live staff metrics | `MachineResourceMetrics` | Latest-only row per machine; viewer-gated sampling |
| Client deploy | `PendingClientUpdateJson` on Machine | `UpdateClient` command + version **3+** agents |

**Fleet sampling:** `IngestService.ResolveForHostAsync` sets `FleetSamplingEnabled = true` for any known machine. Agent `Worker.RunFleetSamplingTickAsync` posts to `POST /api/fleet/snapshot` every 30s.

**Active/Idle (TUFLOW):** while `tuflow*` process running — Active if process GPU > 5%, CPU > 10%, or disk R/W > 5 MB/s.

---

## Features shipped (summary)

| Area | What it does |
|------|----------------|
| **Fleet / Computers** | Team sections; Active/Idle/Off status; period util; clickable metric drill-down; fleet-snapshot-backed GPU/CPU/IO columns |
| **Fleet / Live** | Estate-wide 30s gauges; auto-refresh; TUFLOW Active/Idle when detected |
| **Flood hub** | Enrollment allowlist; Live/Historical for enrolled hosts; Fleet Sims; machine Chart.js detail |
| **TUFLOW Runs** | Queue start/stop on enrolled hosts; ~20s agent poll; TuflowLauncher in client pack |
| **Sessions** | Period + location filter; drill-down; duration display |
| **App lists + Discovery** | Track/ignore via lists; catalog; changelog; Analyze PC (approval-gated) |
| **Teams** | People/machines; app-list links; optional Entra sync |
| **Staff Access** | Public-facing teams; bookings; `.rdp` Connect; optional Negotiate |
| **Remote Access Groups** | Staff ↔ machine membership |
| **Client version** | Pack + silent Deploy; bootstrap pre-v3 via Install.lnk |
| **Finance** | Hardware catalog + software license costs + $/hour metrics |
| **Cost / Socratize / Stats** | Hardware cost focus; machine brief; scoped analytics |
| **Theme / DB mode** | Custom themes; Live vs Sandbox browse — **ingest always Live** |

In-app **Admin → Help** has the full page-by-page guide.

---

## Known issues / gotchas

| Topic | Detail |
|-------|--------|
| **Live vs sandbox** | Agent ingest **always** writes live DB. Sandbox is browse-only. **DEV** badge = sandbox. Do not confuse with “agents missing”. |
| **Two APIs on :5080** | `dotnet run` + `HeimdallApi` service = two processes — use Admin → Database mode or one process only. |
| **localhost vs hostname** | Prod service DB is `%ProgramData%\Heimdall\heimdall.db`. Dev `dotnet run` often uses sandbox in repo folder. Use hostname URL for prod. |
| **Flood enrollment ≠ sampling** | All heartbeating agents get fleet snapshots. Enrollment only for TUFLOW ops + Flood analytics scope. |
| **Pre-v3 agents** | Silent Deploy fails until one manual Install.lnk / Setup push. |
| **NuGet offline-only** | Pack fails NU1101 — need nuget.org or mirror. |
| **PSU / power draw** | Rated W manual only; live draw not collected. |
| **ProcessRun GPU/disk peaks** | Often empty in Stats; use fleet snapshots / Flood views. |
| **SQLite DateTimeOffset** | Some queries filter/order in memory when EF translation fails. |
| **Redeploy API** | `install-api.ps1` can overwrite Program Files appsettings — preserve StaffAccess / AdminEmails intentionally. |

---

## Product decisions / naming

1. **Socratize** — retrospective one-machine cost brief from collected data.
2. **Flight Recorder / Deep Observe** — **backlog** (incident capture). Distinct from **fleet snapshots** (30s util history).
3. **App analysis requires approval** — never silent auto-track.
4. **Flood** — gated nav for modelling team (config: `AdminEmails` ∪ `FloodTeamEmails`).

---

## Suggested next steps

1. Redeploy API + refresh client pack on modelling hosts; verify Fleet → Live and Flood hub after agent v3+.
2. Enroll TUFLOW machines on **Flood → Enrollment** before using TUFLOW Runs.
3. Configure Remote Access Groups + Staff Access Negotiate for production staff pool.
4. Tune Finance / Cost for Socratize hardware + license context.
5. Backlog: Flight Recorder spike; Dell warranty API; CADFX comparison demo.

---

## For the next Cursor agent

1. Open **`C:\Heimdall`** (or RepoSync clone) on **`main`**
2. Read **HANDOVER.md** → **INSTALL.md** → **docs/CLIENT.md** → dashboard **Help**
3. Build: `dotnet build Heimdall.slnx -c Debug` (expect CA1416 / NU1903 warnings on Linux)
4. Do not commit secrets; user often wants **commit + push** when asked
5. Preserve names: **Socratize**, **Flight Recorder**, **Flood** (not “Historical Dashboard” in nav — that’s the Flood hub)
6. Prefer **CMD** installers on locked-down SOE targets

---

## Quick reference commands

```powershell
cd C:\Heimdall
git status
dotnet build Heimdall.slnx -c Debug

# Dev
cd src\Heimdall.Api;  dotnet run --urls http://localhost:5080
cd src\Heimdall.Agent; dotnet run

# Elevated
.\scripts\Install-Api.cmd
.\scripts\Install-Agent.cmd
.\scripts\Heimdall-Setup.lnk

# Client on target PC
.\Install.lnk
```

**GitHub:** https://github.com/uberslaw/Heimdall
