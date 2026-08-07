# Heimdall

Continuing work? See **[HANDOVER.md](HANDOVER.md)** (paths, features, gotchas, next steps).

Lightweight workstation usage tracker for justifying remote / modelling machine cost.

**POC goal:** clearer session + app utilisation than CADFX, with server-side config and minimal agent overhead.

## What it tracks

- Local vs RDP logons
- Session start / end, active vs disconnected time
- Allowlisted applications (who, how often, how long)
- Machine heartbeat / in-use signal for utilisation %

## Stack

| Piece | Tech |
|-------|------|
| Agent | .NET 10 Windows Service |
| API + dashboard | ASP.NET Core Razor Pages |
| DB (POC) | SQLite (zero SQL Server install) |

## Install (Windows services)

See **[INSTALL.md](INSTALL.md)** for the full guide: prerequisites (.NET 10, admin, port 5080), server + agent install, verification, troubleshooting, and how to send diagnostics back for analysis.

**Prefer guided Setup** (helmet icon in Explorer):

```text
scripts\Heimdall-Setup.lnk
```

Same UI: `scripts\Heimdall-LaunchControl.lnk`. After pull, run `scripts\New-HeimdallShortcuts.cmd` once if icons/targets look stale.

Options: Install API → Create client pack → Push to PC → Install agent. Right-side Steps: Client (default) / Server.

**POC auth:** agent API key (`X-Heimdall-Key`). No site-wide Entra SSO for the admin dashboard yet (trusted LAN). Optional **Entra Graph** sync for Teams membership — see **Admin → Auth** and [INSTALL.md](INSTALL.md).

```text
# Build PC: create one folder, then copy it to clients
scripts\Heimdall-Setup.lnk
#   -> Create client pack  =>  dist\Heimdall-Client\

# Each client PC: double-click Install.lnk inside that folder
# Pack again only when the agent changes
```

See [docs/portable-client/README.md](docs/portable-client/README.md) and **[INSTALL.md](INSTALL.md)**.

Install logs: `%ProgramData%\Heimdall\logs\`  
Diagnostics zip: `%LOCALAPPDATA%\Heimdall\diagnostics-*.zip`

RepoSync users typically keep the clone at `C:\Users\christopher.owen\Cursor\Heimdall`.

## Quick start (dev)

```powershell
# Terminal 1 — API + dashboard
cd src\Heimdall.Api
dotnet run --urls http://localhost:5080

# Terminal 2 — Agent (console)
cd src\Heimdall.Agent
dotnet run
```

Open http://localhost:5080

Default API key: `heimdall-poc-key` (header `X-Heimdall-Key`)

## Config

Dashboard → **Config**: sample/upload intervals, CPU floor, known apps, include/exclude lists, and **metric threshold policies** (high RAM / GPU / disk) scoped to All, Region, Office, Group, or Machine.  
Agents refresh config every ~5 minutes (configurable) via `GET /api/config/{hostname}` — includes merged process allowlists and effective metric thresholds (most-specific scope wins).

### Track Software

Dashboard → **Applications** → **Track Software**: pick Known / Discovered / Custom software and a Region → Office → Machine tree. Creates scoped tracking config so agents only track that title on the selected scope.

Machine hierarchy is derived from `Machine.Region` / `Machine.Office`. Heartbeat `MachineGroup` values like `APAC/Sydney` or plain `POC` are mapped automatically (`POC` → Region POC / Office Local). `Machine.Country` is a POC field derived from region (e.g. APAC → Australia) for Stats scoping.

### Stats

Dashboard → **Stats**: pick machine scope (All / Region / Country / Office / Group / Machine) and a date range, then browse filterable sortable analytics — user logon counts & durations, avg session / avg use per day, app time & CPU rankings, RDP disconnected time, and day-of-week usage patterns. GPU/disk ranking columns are wired on `ProcessRun` but show empty until agents report samples.

### Machines utilisation period

Dashboard → **Machines**: choose **Utilisation period** (1 day, 7 day default, 2 week, 4 week, quarter ~90 days, 6 month, year). Selection is kept in the query string (`?range=7d`, `?range=2w`, `?range=quarter`, etc.) so refresh preserves it. Avg util and per-machine utilisation % recalculate for that window.

### Socratize

Dashboard → **Machines** → **Socratize** (per row, or select host at top) opens `/Socratize?host=HOSTNAME` — a one-machine **cost-justification Q&A** built from already-collected Heimdall data (default last 30 days): who uses it (and Teams if mapped), local vs RDP, occupancy %, RDP disconnected waste, dominant apps, MetricPolicy thresholds in scope, and a short heuristic POC verdict (underused / healthy / RDP-idle-heavy / app-concentrated). Apt vs CADFX: one-click “is this box earning its keep?”

**Keep the name Socratize** for this retrospective machine deep-dive. A related future arm — **Flight Recorder / Deep Observe** (high-cardinality capture while a watched process like `tuflow.exe` runs, for AI incident analysis) — is parked in [`docs/BACKLOG.md`](docs/BACKLOG.md) and teased on the Socratize page; it is not the same as today’s brief.

### App lists & Discovery

Dashboard → **Applications** → **App lists**: named process schemas, machine Analyze (approval-gated — never silent auto-track), Request Inventory, classification CSV export/import.  
Dashboard → **Discovery**: full central process catalog (ProcessName + ExecutablePath), editable labels/version/category, installs + usage frequency.

In-app **Admin → Help** is the page-by-page operator guide (preferred for UI detail).

### Remote

| Page | Purpose |
|------|---------|
| **Remote Machines** | RDP/RDS health, ping from API host, Connect `.rdp`, Restart RDS via agent command queue |
| **Historical Dashboard** | Enroll machines → always-on **30s** resource snapshots; Live Fleet + historical analytics (TUFLOW-oriented POC). Distinct from backlog **Flight Recorder**. |
| **Staff Access** | Restricted live metrics for staff in a Remote Access Group (optional Windows Negotiate — see [INSTALL.md](INSTALL.md)) |

Admin → **Remote Access Groups** maps staff email ↔ machines. Admin → **Clients** compares agent heartbeat version vs published pack version. Admin → **Theme** / **Database mode** (Live vs Sandbox).

### Teams

Dashboard → **Teams**: maintain business units / teams (optional parent hierarchy + code) and map people by Windows username (optional domain, display name, email). Primary POC path is **CSV upload**; create/edit/delete teams and assign users manually as well.

**CSV columns** (header required):

| Column | Required | Notes |
|--------|----------|--------|
| `Username` | yes | SAM or `DOMAIN\user` |
| `Team` | yes | Created if missing |
| `Domain` | no | Used when Username has no `DOMAIN\` |
| `DisplayName` | no | |
| `Email` | no | |
| `ParentTeam` | no | Parent team name (created if missing) |
| `Code` | no | Optional team code |

Simpler format: `Username,Team`. Session usernames match case-insensitively with or without `DOMAIN\`. Full org-chart file upload can come later — CSV is the supported import for POC. Template: `/templates/heimdall-teams-template.csv` or **Download CSV template** on the Teams page. Sessions table shows a Team column when a match exists.

## Solution layout

```
src/Heimdall.Agent   Windows service collector
src/Heimdall.Api     Ingest API + dashboard
src/Heimdall.Shared  DTOs / contracts
scripts/             Local-admin installers
```

## POC limits

- No Entra/AD **login** on the admin dashboard yet (trusted LAN). Staff Access can use Windows Negotiate separately.
- **Teams membership** can optionally sync from Entra Graph (app-only; DPAPI secret on API host; toggles under Admin → Auth). Manual/CSV remains the backup until Graph admin consent is granted. Site-wide SSO is still later.
- Process→user via session id (best-effort)
- Per-process `ProcessRun` GPU/disk peaks are often still empty (Stats ranking columns). **Live** RAM/GPU/disk sampling runs for Staff Access viewers and for **Flood / Historical** enrollment (30s fleet snapshots) — thresholds in Config still apply via `/api/config/{hostname}`.
- SQLite for POC; SQL Server later if needed
