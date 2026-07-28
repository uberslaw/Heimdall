# Heimdall install guide (POC)

This guide installs the **Heimdall API + dashboard** (server) and the **Heimdall Agent** (workstation collector) as Windows services.

**Auth (POC):** API key only (`X-Heimdall-Key`). There is **no Entra / AD website login** yet — treat the dashboard as trusted-LAN / POC only.

**RepoSync users:** a typical local clone path is `C:\Users\christopher.owen\Cursor\Heimdall`. You can clone anywhere; installers resolve the repo root from the `scripts` folder.

Live repo: https://github.com/uberslaw/Heimdall

---

## Preferred: Launch Control (guided)

Double-click:

```text
scripts\Heimdall-LaunchControl.cmd
```

One form for: Install API, Pack collector, Install collector (prereqs → API URL → health/version → install → verify), **Client health check** (service, settings, API probes; logs to local + `logs\clients\<hostname>\` on API host), Open logs, **Open remote logs folder** (saved recent hosts; browse `clients\` subfolder for agent checks), Collect diagnostics, Open dashboard.

On-screen progress mirrors `%ProgramData%\Heimdall\logs\`. Installer consoles **pause until you press a key**. Portable packs include Launch Control — run it from `dist\workstation-collector\` on targets.

---

## Prerequisites

| Requirement | Notes |
|-------------|--------|
| **Windows** | Server or workstation; local **Administrator** for service install |
| **.NET 10 SDK** | Needed to `dotnet publish` during **API install** or **pack** ([download](https://dotnet.microsoft.com/download/dotnet/10.0)) |
| **.NET 10 runtime** | Bundled in the portable collector pack (self-contained). Repo-based agent install still needs SDK/runtime. |
| **Firewall / port** | API listens on **5080** by default (`http://0.0.0.0:5080`). The API installer creates an inbound Windows Firewall allow rule for the chosen port (or allow TCP manually if group policy blocks local rules) |
| **Outbound HTTPS/HTTP** | Agents must reach the API URL you configure |

Check SDK:

```powershell
dotnet --list-sdks
# expect a 10.x line, e.g. 10.0.301
```

**Important:** `Install-Agent.cmd` fails with **NETSDK1045** if only .NET 8 SDK is present. For test/SOE machines use the **portable pack** (no SDK on the target).

Clone (or sync with RepoSync):

```powershell
git clone https://github.com/uberslaw/Heimdall.git
cd Heimdall
```

---

## 1. Install the server (API + dashboard)

Prefer Launch Control → **Install API**, or run **elevated** (`.cmd` keeps the console open):

```text
scripts\Install-Api.cmd
```

Or from an elevated PowerShell prompt in the repo:

```powershell
.\scripts\install-api.ps1
# optional:
.\scripts\install-api.ps1 -Port 5080 -ApiKey "heimdall-poc-key" -InstallDir "$env:ProgramFiles\Heimdall\Api"
```

What it does:

1. Publishes `src\Heimdall.Api` to `%ProgramFiles%\Heimdall\Api` (verbose `dotnet publish`)
2. Writes `appsettings.json` with SQLite at `%ProgramData%\Heimdall\heimdall.db` and your API key
3. Creates/recreates Windows Service **`HeimdallApi`** and starts it
4. Ensures a Windows Firewall inbound allow rule for TCP on the chosen port (default **5080**; takes effect immediately — no service restart)

**Install log:** `%ProgramData%\Heimdall\logs\install-api-YYYYMMDD-HHMMSS.log`  
(The installer prints the full path and pauses at the end.)

`GET /api/health` returns `productVersion` (Launch Control compares this to the pack `VERSION.json`).

---

## 2. Install the agent (workstation collector)

### Option A — Portable pack (recommended for other PCs / vanilla SOE)

Pack **once** on a build machine that has the repo + **.NET 10 SDK** + NuGet access (repo `NuGet.config` → nuget.org; offline-only VS feeds cause **NU1101**):

```text
scripts\Heimdall-LaunchControl.cmd
# or:
scripts\Pack-WorkstationCollector.cmd
```

Copy `dist\workstation-collector\` (or the zip) to each target PC. On the target prefer:

```text
Heimdall-LaunchControl.cmd
```

Or elevated direct install:

```text
Install-WorkstationCollector.cmd -ApiUrl http://SERVER:5080 -MachineGroup SOE
```

- Payload is **self-contained win-x64** — target PCs do not need a separate .NET install.
- Copy the **whole folder** (`Heimdall-LaunchControl.*` + `Install-WorkstationCollector.cmd` + `payload\` + `VERSION.json`). Docs-only `scripts\workstation-collector\` is **not** installable.

**Install log:** `%ProgramData%\Heimdall\logs\install-workstation-collector-*.log`  
**Launch Control log:** `%ProgramData%\Heimdall\logs\launch-control-*.log`

### Option B — From a full repo clone (same machine / has SDK)

On each workstation (or the same machine for a local POC), run elevated:

```text
scripts\Install-Agent.cmd
```

Or:

```powershell
.\scripts\install-agent.ps1 -ApiUrl http://SERVER:5080 -ApiKey "heimdall-poc-key" -MachineGroup "POC"
```

Replace `SERVER` with the API host name or IP. Default API key for POC is `heimdall-poc-key` — it **must match** the key configured on the API.

What it does:

1. Publishes `src\Heimdall.Agent` to `%ProgramFiles%\Heimdall\Agent`
2. Writes `appsettings.json` with `ApiBaseUrl`, `ApiKey`, `MachineGroup`, and queue path `%ProgramData%\Heimdall\queue.db`
3. Creates/recreates Windows service **`HeimdallAgent`** and starts it

**Install log:** `%ProgramData%\Heimdall\logs\install-agent-YYYYMMDD-HHMMSS.log`

---

## 3. Verify

| Check | How |
|-------|-----|
| **Health** | Browser or `curl http://localhost:5080/api/health` (use the API host/port) |
| **Dashboard** | http://SERVER:5080 |
| **API service** | `Get-Service HeimdallApi` → Running |
| **Agent service** | `Get-Service HeimdallAgent` → Running |
| **First heartbeat** | Dashboard → **Machines** — your hostname should appear after the agent’s first successful heartbeat |

Quick PowerShell checks:

```powershell
Get-Service HeimdallApi, HeimdallAgent
Invoke-RestMethod http://localhost:5080/api/health
```

---

## 4. Troubleshooting

### Window closed too fast / no message

- Prefer **`Heimdall-LaunchControl.cmd`** — UI stays until you close it; logs always under ProgramData.
- `.cmd` installers call `pause` at the end. If a window vanished, check `%ProgramData%\Heimdall\logs\` for the newest `install-*.log` or `launch-control-*.log`.
- Missing `payload\Heimdall.Agent.exe` means you copied the wrong folder (docs-only `scripts\workstation-collector\` is not installable).

### NETSDK1045 / only .NET 8 SDK

Repo-based `Install-Agent` cannot publish `net10.0` with SDK 8. Use the portable pack, or install [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

### Service won’t start

- Re-run the installer from an **elevated** prompt; read the SUCCESS/FAILURE banner and the log path it prints.
- `Get-WinEvent` / Event Viewer → Windows Logs → Application, filter by Heimdall / .NET Runtime.
- Confirm the exe exists: `%ProgramFiles%\Heimdall\Api\Heimdall.Api.exe` or `...\Agent\Heimdall.Agent.exe`.
- Confirm .NET 10 runtime/SDK is installed on that machine.

### Agent can’t reach API

- From the agent machine: `Invoke-WebRequest http://SERVER:5080/api/health` (or `Test-NetConnection SERVER -Port 5080`)
- Firewall on the API host must allow inbound TCP on the API port (the installer creates **`Heimdall API (port N)`** when policy permits; no HeimdallApi restart is required after adding a rule)
- `ApiBaseUrl` in agent `appsettings.json` must be reachable (no `localhost` if the agent is on another PC).
- API key mismatch → 401; keys on API and agent must match exactly.

### SQLite path issues

| File | Default path |
|------|----------------|
| API DB | `%ProgramData%\Heimdall\heimdall.db` |
| Agent offline queue | `%ProgramData%\Heimdall\queue.db` |

Ensure `%ProgramData%\Heimdall\` exists and the service account (LocalSystem by default) can write there. Do not commit or overwrite these DBs into the git clone.

**Demo machines:** A fresh empty API database gets four `DEMO-*` placeholder hosts (`AgentVersion=seed`) once for UX. They are **not** re-added after you delete them (`SystemFlags.DemoMachinesOffered`). Launch Control → **Remove seed/demo machines** (repo layout) or `scripts\Remove-SeedDemoMachines.ps1` — stop `HeimdallApi` first if the DB is locked. Requires `sqlite3` on PATH (`winget install SQLite.SQLite`).

**Backup API DB:** Launch Control → **Backup API database** copies `\\HOST\C$\ProgramData\Heimdall\heimdall.db` locally to `%LOCALAPPDATA%\Heimdall\backups\` (and tries `...\backups\` on the API PC). Same SMB/admin-share access as **Open remote logs**.

### API key mismatch

POC default: `heimdall-poc-key`. Set the same value in:

- API install (`-ApiKey`) / `%ProgramFiles%\Heimdall\Api\appsettings.json` → `Heimdall:ApiKey`
- Agent install (`-ApiKey`) / `%ProgramFiles%\Heimdall\Agent\appsettings.json` → `Heimdall:ApiKey`

Restart the affected service after changing keys.

---

## 5. Send diagnostics back for analysis

If install or runtime fails, collect a bundle and paste/upload it in Cursor chat (or email the zip).

**Elevated optional** (service queries work better as admin):

```text
scripts\Collect-Diagnostics.cmd
```

Or:

```powershell
.\scripts\collect-diagnostics.ps1
```

The script gathers:

- Recent install logs under `%ProgramData%\Heimdall\logs\`
- Service status for `HeimdallApi` / `HeimdallAgent`
- `GET /api/health` if reachable
- Hostname, OS info
- Redacted agent/API appsettings (API key → last 4 chars only)
- Recent stdout/stderr-style service / log snippets when available

It prints a **folder or zip path** at the end and pauses. Upload that zip (or the folder contents) when asking for help.

---

## 5b. SOE golden-image program inventory

Continuous usage from vanilla SOE boxes comes from the **workstation collector** (section 2, Option A). Separately, on a **golden image** machine (before user apps), enumerate installed programs for SOE exclude review:

```text
scripts\Inspect-SoeInstalledPrograms.cmd
```

Or:

```powershell
.\scripts\Inspect-SoeInstalledPrograms.ps1 -CompareCatalog
```

Output CSV + log under `%LOCALAPPDATA%\Heimdall\` (`soe-installed-*.csv`). Columns: DisplayName, Publisher, EstimateProcessName (best-effort), SuggestedIgnore=true. With `-CompareCatalog`, also flags names already in `SoeCatalog.cs`.

**Workflow:** run on golden image → review CSV → feed confirmed process names into Config → SOE excludes / Autogenerate (and optionally merge into `SoeCatalog.cs`).

---

## 6. Credentials / config after clone

Nothing secret is required beyond what you choose for POC:

| Setting | Default / action |
|---------|------------------|
| **API key** | Change from `heimdall-poc-key` for anything beyond a throwaway POC |
| **Agent `ApiBaseUrl`** | Point at your real API host |
| **Port** | 5080 unless you pass `-Port` |
| **SQLite** | Created automatically under `%ProgramData%\Heimdall\` |

Committed `appsettings.json` files use the POC placeholder key for local `dotnet run` only. Service installs rewrite installed appsettings under Program Files.

---

## Dev run (no services)

See [README.md](README.md) — `dotnet run` on Api (port 5080) and Agent. Same POC key header: `X-Heimdall-Key: heimdall-poc-key`.
