# Heimdall install guide (POC)

This guide installs the **Heimdall API + dashboard** (server) and the **Heimdall Agent** (workstation collector) as Windows services.

**Auth (POC):** API key only (`X-Heimdall-Key`). There is **no Entra / AD website login** yet — treat the dashboard as trusted-LAN / POC only.

**RepoSync users:** a typical local clone path is `C:\Users\christopher.owen\Cursor\Heimdall`. You can clone anywhere; installers resolve the repo root from the `scripts` folder.

Live repo: https://github.com/uberslaw/Heimdall

---

## Prerequisites

| Requirement | Notes |
|-------------|--------|
| **Windows** | Server or workstation; local **Administrator** for service install |
| **.NET 10 SDK** | Needed to `dotnet publish` during install ([download](https://dotnet.microsoft.com/download/dotnet/10.0)) |
| **.NET 10 runtime** | On machines that only run published binaries; SDK includes a compatible runtime |
| **Firewall / port** | API listens on **5080** by default (`http://0.0.0.0:5080`). Allow inbound TCP **5080** on the API host if agents are remote |
| **Outbound HTTPS/HTTP** | Agents must reach the API URL you configure |

Check SDK:

```powershell
dotnet --list-sdks
# expect a 10.x line, e.g. 10.0.301
```

Clone (or sync with RepoSync):

```powershell
git clone https://github.com/uberslaw/Heimdall.git
cd Heimdall
```

---

## 1. Install the server (API + dashboard)

Run **elevated** (Run as administrator). Prefer the `.cmd` wrapper so the console stays open when double-clicked from Explorer.

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
3. Creates/recreates Windows service **`HeimdallApi`** and starts it

**Install log:** `%ProgramData%\Heimdall\logs\install-api-YYYYMMDD-HHMMSS.log`  
(The installer prints the full path and pauses at the end.)

---

## 2. Install the agent

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

### Service won’t start

- Re-run the installer from an **elevated** prompt; read the SUCCESS/FAILURE banner and the log path it prints.
- `Get-WinEvent` / Event Viewer → Windows Logs → Application, filter by Heimdall / .NET Runtime.
- Confirm the exe exists: `%ProgramFiles%\Heimdall\Api\Heimdall.Api.exe` or `...\Agent\Heimdall.Agent.exe`.
- Confirm .NET 10 runtime/SDK is installed on that machine.

### Agent can’t reach API

- From the agent machine: `Invoke-WebRequest http://SERVER:5080/api/health`
- Firewall on the API host must allow TCP **5080**.
- `ApiBaseUrl` in agent `appsettings.json` must be reachable (no `localhost` if the agent is on another PC).
- API key mismatch → 401; keys on API and agent must match exactly.

### SQLite path issues

| File | Default path |
|------|----------------|
| API DB | `%ProgramData%\Heimdall\heimdall.db` |
| Agent offline queue | `%ProgramData%\Heimdall\queue.db` |

Ensure `%ProgramData%\Heimdall` exists and the service account (LocalSystem by default) can write there. Do not commit or overwrite these DBs into the git clone.

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
