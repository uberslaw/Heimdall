# Heimdall install guide (POC)

This guide installs the **Heimdall API + dashboard** (server) and the **Heimdall Agent** (workstation collector) as Windows services.

**Auth (POC):** Agent ingest uses API key only (`X-Heimdall-Key`). There is **no Entra / AD website login** for the general dashboard yet — treat it as trusted-LAN / POC only. **Teams membership** can optionally use Entra Graph (see **Admin → Auth** and [Entra ID (Graph) membership](#entra-id-graph-membership--secrets) below); that is directory sync, not SSO.

**RepoSync users:** a typical local clone path is `C:\Users\christopher.owen\Cursor\Heimdall`. You can clone anywhere; installers resolve the repo root from the `scripts` folder.

Live repo: https://github.com/uberslaw/Heimdall

---

## Preferred: Heimdall Setup (guided)

Double-click the **helmet icon** shortcut:

```text
scripts\Heimdall-Setup.lnk
```

(`Heimdall-LaunchControl.lnk` is the same Setup UI — kept for older bookmarks.)

Left buttons run actions. Right side has **two Steps branches** (click a step for full instructions):

1. **Client install** (default) — prepare → create pack → push/copy → `Install.lnk` on target → verify on dashboard  
2. **Server install** — prepare → install API → verify dashboard → then switch to Client branch for agents  

Left actions include: Install API, Create client pack, **Push client pack to PC…** (hostname → `\\HOST\C$\Temp\Heimdall-Client-v{version}`), Install agent, health/logs/backup/diagnostics.

On client PCs you only need **`Install.lnk`** (from the pushed/copied pack). Logs: `%ProgramData%\Heimdall\logs\`.

**Push requirements:** your account needs admin rights on the target (C$ / SMB). After push, on the target PC run `C:\Temp\Heimdall-Client-v{version}\Install.lnk` elevated (version from pack `VERSION.json`).

**If you still see “Pack collector” / old Steps text:** pull latest `main`, then run `scripts\New-HeimdallShortcuts.cmd` and reopen `scripts\Heimdall-Setup.lnk` (or `Heimdall-LaunchControl.lnk` — same UI).

---

## Prerequisites

| Requirement | Notes |
|-------------|--------|
| **Windows** | Server or workstation; local **Administrator** for service install |
| **.NET 10 SDK** | Needed to `dotnet publish` during **API install** or **pack** ([download](https://dotnet.microsoft.com/download/dotnet/10.0)) |
| **.NET 10 runtime** | Bundled in the portable Heimdall-Client pack (self-contained). Repo-based agent install still needs SDK/runtime. |
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

Prefer Heimdall Setup → **Install API on this PC**, or run **elevated** (`.cmd` keeps the console open):

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

`GET /api/health` returns `productVersion` (Install wizard and Setup compare core version before `+`; e.g. `0.1.0` matches `0.1.0+gitsha`).

---

## 2. Install the agent (one client folder)

### Simple model

| Role | What you run | Folder involved |
|------|--------------|-----------------|
| Build / server PC | `scripts\Heimdall-Setup.lnk` | Creates `dist\Heimdall-Client\` |
| Every client PC | `Install.lnk` inside that folder | Copy **only** `dist\Heimdall-Client\` |

There is no separate “workstation collector” folder vs “client install” folder — same pack. Docs live under `docs\portable-client\` in git (not installable).

Create the pack **once** per agent change; reuse it on every PC. Setup prompts you through API URL / key / group, tests the connection, installs, and verifies.

### Option A — Portable pack (recommended for other PCs / vanilla SOE)

On a build machine (repo + **.NET 10 SDK** + NuGet / nuget.org):

```text
scripts\Heimdall-Setup.lnk
```

Choose **Create client pack** (or run `scripts\Pack-WorkstationCollector.cmd`). After success, Setup can offer **Install agent on this PC now**.

Output:

```text
dist\Heimdall-Client\
  Install.lnk          ← only entry clients need
  Install.cmd + wizard scripts
  payload\Heimdall.Agent.exe   ← required
  VERSION.json, …
```

Copy **that one folder** (or `dist\heimdall-client.zip`) to each target. On the target:

```text
Install.lnk
```

Silent/scripted (advanced):

```text
Install-WorkstationCollector.cmd -ApiUrl http://SERVER:5080 -MachineGroup SOE
```

**Install log:** `%ProgramData%\Heimdall\logs\install-client-*.log` (wizard) and `install-agent-*.log` (service install)  
**Setup log:** `%ProgramData%\Heimdall\logs\launch-control-*.log`

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

See **[docs/CLIENT.md](docs/CLIENT.md)** for how the agent collects data, fleet snapshots, TUFLOW, and silent `UpdateClient` deploy.

### Option C — Silent client update (after bootstrap)

Once agents report version **≥ 3** (and one manual install has been done for older agents):

1. On the API host: **Fleet → Client version** → **Pack client** (when Ready)
2. Select hosts → **Deploy Client** — queues download + service restart
3. Updates **defer** while an interactive session is Active

Agents below version 3 need **Install.lnk** or Setup push once before silent Deploy works.

---

## 3. Verify

| Check | How |
|-------|-----|
| **Health** | Browser or `curl http://localhost:5080/api/health` (use the API host/port) |
| **Dashboard** | http://SERVER:5080 |
| **API service** | `Get-Service HeimdallApi` → Running |
| **Agent service** | `Get-Service HeimdallAgent` → Running |
| **First heartbeat** | Dashboard → **Fleet → Computers** — hostname after first successful heartbeat |

Quick PowerShell checks:

```powershell
Get-Service HeimdallApi, HeimdallAgent
Invoke-RestMethod http://localhost:5080/api/health
```

---

## 4. Troubleshooting

### Window closed too fast / no message

- Prefer **`Install.lnk`** on target PCs — wizard stays open until you close it; logs always under ProgramData.
- **`Heimdall-Setup.lnk`** on build/server PCs (API install, create client pack, tools).
- `.cmd` installers call `pause` at the end. If a window vanished, check `%ProgramData%\Heimdall\logs\` for the newest `install-*.log` or `launch-control-*.log`.
- Missing `payload\Heimdall.Agent.exe` means you do not have a successful pack — copy `dist\Heimdall-Client\` (not `docs\portable-client\`).

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
| API DB (live) | `%ProgramData%\Heimdall\heimdall.db` |
| API DB (sandbox / dev) | `%ProgramData%\Heimdall\heimdall-dev.db` |
| Agent offline queue | `%ProgramData%\Heimdall\queue.db` |

Ensure `%ProgramData%\Heimdall\` exists and the service account (LocalSystem by default) can write there. Do not commit or overwrite these DBs into the git clone.

**Live vs sandbox dashboard:** The installed API service uses the **live** database (real agents). Use **Admin → Database mode → Switch to Dev sandbox** to browse a separate sandbox DB (`heimdall-dev.db`) with demo machines — agent ingest always stays on live. The nav shows a large **DEV** badge and teal/amber header in sandbox mode. Repo `dotnet run` (Development) defaults to sandbox automatically.

**Do not run two dashboards by accident:** If you `dotnet run` the API on `localhost:5080` while the **HeimdallApi** Windows service is also running, you had two separate processes — not a UI toggle. Prefer one process and use **Admin → Database mode** to switch, or use **Open Dev/Live dashboard (other URL)** when both are intentionally running. Never edit live production data while you think you are in sandbox.

**Demo machines:** A fresh empty API database gets four `DEMO-*` placeholder hosts (`AgentVersion=seed`) once for UX. They are **not** re-added after you delete them (`SystemFlags.DemoMachinesOffered`). Heimdall Setup → **Remove seed/demo machines** (repo layout) or `scripts\Remove-SeedDemoMachines.ps1` — stop `HeimdallApi` first if the DB is locked. Requires `sqlite3` on PATH (`winget install SQLite.SQLite`).

**Backup API DB:** Heimdall Setup → **Backup API database** copies `\\HOST\C$\ProgramData\Heimdall\heimdall.db` locally to `%LOCALAPPDATA%\Heimdall\backups\` (and tries `...\backups\` on the API PC). Same SMB/admin-share access as **Open remote logs**.

### Entra ID (Graph) membership — secrets and toggles

Team membership sync uses an Entra **app registration** (client credentials). **Do not** put the client secret in git or in `appsettings.json`. This is **not** site-wide SSO.

**Admin → Auth** toggles (stored in SQLite `SystemFlags`):

| Source | Default | Role |
|--------|---------|------|
| **Manual / CSV** | On | Backup — add people on team detail or Import CSV |
| **Entra Graph sync** | Off | Enable after Graph admin consent; unlocks Sync from Entra |

Use **Test Entra connection** on Auth: token OK + group-read fail usually means Application permissions still lack admin consent.

On the API host (elevated):

```powershell
cd C:\Heimdall   # or your repo / scripts folder
.\scripts\Protect-HeimdallEntraSecret.ps1 -TenantId '<tenant-guid>' -ClientId '<app-guid>'
# prompts securely for the client secret
```

That writes `%ProgramData%\Heimdall\secrets\entra.json` with a **DPAPI LocalMachine**–encrypted secret (readable by LocalSystem / this machine only) and ACL limited to SYSTEM + Administrators. Then:

```powershell
Restart-Service HeimdallApi
```

No redirect URI is required for this flow. App permissions: Application `Group.Read.All` + `User.Read.All` (or `GroupMember.Read.All`) with admin consent. Then Admin → Auth → Probe → enable Entra Graph → Edit team (group Object ID) → Sync from Entra.

### API key mismatch

POC default: `heimdall-poc-key`. Set the same value in:

- API install (`-ApiKey`) / `%ProgramFiles%\Heimdall\Api\appsettings.json` → `Heimdall:ApiKey`
- Agent install (`-ApiKey`) / `%ProgramFiles%\Heimdall\Agent\appsettings.json` → `Heimdall:ApiKey`

Restart the affected service after changing keys.

### Staff Access — Windows login verification

Staff Access can tie sign-in to the user's **Windows account** (Negotiate/NTLM/Kerberos) so staff cannot impersonate a colleague by typing their email. Agent ingest still uses **`X-Heimdall-Key`** only — unchanged.

**API config** (`appsettings.json` under the install dir, default `%ProgramFiles%\Heimdall\Api\`):

```json
"Heimdall": {
  "StaffAccess": {
    "RequireWindowsAuth": true,
    "EmailDomainSuffixes": [ "yourcompany.com" ],
    "AllowDevBypass": false
  }
}
```

| Setting | Purpose |
|---------|---------|
| `RequireWindowsAuth` | When `true`, Staff Access and `/api/staff/*` require a Windows identity that matches the registered email. |
| `EmailDomainSuffixes` | Derives email from `DOMAIN\user` → `user@yourcompany.com`. Also set staff emails in Remote Access Groups to the same UPN/mailbox. |
| `AllowDevBypass` | **`true` only for local `dotnet run` dev.** Skips Windows checks; shows a warning. Never on production installs. |

**Admin / browser setup (intranet POC):**

1. Browse the dashboard by **hostname** (e.g. `http://heimdall-server:5080`), not raw IP, so Negotiate works reliably.
2. Add the site to the **Local intranet** zone (IE settings / Group Policy: *Site to Zone Assignment*), or ensure Edge/Chrome treats it as intranet so Windows credentials are sent automatically.
3. Register staff in **Admin → Remote Access Groups** using emails that match AD: UPN (`user@domain.com`) or `user@` one of `EmailDomainSuffixes`.
4. Restart **`HeimdallApi`** after changing `appsettings.json`.

**IIS alternative:** If you reverse-proxy with IIS instead of direct Kestrel, enable **Windows Authentication** on the IIS site and disable Anonymous for `/StaffAccess`, `/Staff`, and `/api/staff/*`; forward `X-Forwarded-*` headers. Kestrel-as-service (default installer) supports Negotiate directly — no IIS required.

**Limitations:** Non–domain-joined PCs, macOS/Linux browsers, Safari, and some VPN clients may not send Windows credentials; staff on those machines cannot use Staff Access until they use a domain-joined Windows PC and intranet browser. This is intentional — it prevents email impersonation without passwords.

---

## 5. Send diagnostics back for analysis

If install or runtime fails, collect a bundle and paste/upload it in Cursor chat (or email the zip).

**Known locations on the API host**

| Path | Purpose |
|------|---------|
| `%ProgramData%\Heimdall\logs\api\` | Rolling API runtime logs (`heimdall-api-yyyyMMdd.log`) — use when the service will not start |
| `%ProgramData%\Heimdall\logs\ops\` | Admin/ops action log (pack, deploy, deposit, restart, …) |
| `%ProgramData%\Heimdall\logs\` | Install / pack / Launch Control script logs |
| `C:\Temp\Heimdall.API\Logs\` | Grab-and-go diagnostic dumps (Admin Collect + offline script) |
| Windows Application Event Log | `HeimdallApi` / .NET Runtime / Service Control Manager |

**While the API is running:** Admin → **Diagnostics** → **Collect diagnostics** (writes under `C:\Temp\Heimdall.API\Logs`).

**When the service will not start** (elevated optional — service queries work better as admin):

```text
scripts\Collect-Diagnostics.cmd
```

Or:

```powershell
.\scripts\collect-diagnostics.ps1
```

Default output root is `C:\Temp\Heimdall.API\Logs\diagnostics-{stamp}\` (+ sibling `.zip`). The script still gathers ProgramData logs, services, Event Log, and redacted appsettings even if `/api/health` is unreachable.

The script gathers:

- Recent logs under `%ProgramData%\Heimdall\logs\` (including `api\` / `ops\` when present)
- Service status for `HeimdallApi` / `HeimdallAgent`
- `GET /api/health` if reachable
- Hostname, OS info
- Redacted agent/API appsettings (API key → last 4 chars only)
- Recent Application Event Log entries mentioning Heimdall / .NET / SCM

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
