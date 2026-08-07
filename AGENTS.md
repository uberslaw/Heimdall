# AGENTS.md

## Cursor Cloud specific instructions

Heimdall is a **.NET 10** solution (`Heimdall.slnx`). Standard build/run/config commands live in `README.md` and `INSTALL.md`; this section only covers non-obvious things for running it inside the Linux cloud VM.

### What can run here
- **`src/Heimdall.Api`** (ASP.NET Core Razor Pages + SQLite) is the only service meant to run on Linux. It is the ingest API **and** the admin dashboard.
- **`src/Heimdall.Agent`** is the Windows workstation collector. It compiles on Linux (target is `net10.0`) but its runtime data collection (WMI, performance counters, session/RDP APIs) is Windows-only — do not expect it to produce data here. `Program.cs` calls `UseWindowsService()`, which is a harmless no-op on Linux.

### Running the API (dev)
- `cd src/Heimdall.Api && dotnet run --urls http://0.0.0.0:5080` (dashboard + API on port 5080).
- SQLite DBs are auto-created and seeded at startup in the API's working directory: `heimdall.db` (live) and `heimdall-dev.db` (sandbox). There are **no EF migrations** — `SeedData` / `EnsureCreated` runs on boot, so a fresh checkout just works. These `*.db` files are gitignored; do not commit them.
- A fresh DB is seeded with four `DEMO-*` machines for UX.

### Live vs sandbox database (important gotcha)
- In `Development` the **dashboard browses the sandbox DB by default** (`DatabaseMode: sandbox` in `appsettings.Development.json`), but **agent ingest via `/api/ingest` always writes to the live DB** (`heimdall.db`).
- So after posting a heartbeat you will **not** see the machine until you switch the dashboard to Live: visit `http://localhost:5080/database-mode?mode=live&returnUrl=/Fleet?tab=machines`, or use **Admin -> Database mode**. In the UI, Live is labelled "Local DB".

### Auth
- Agent-facing `/api/*` endpoints require header `X-Heimdall-Key: heimdall-poc-key` (POC default). Missing/wrong key returns 401.
- Staff Access uses Windows Negotiate; `appsettings.Development.json` sets `AllowDevBypass: true` so those pages work without a Windows identity here.

### Lint / test / build
- There is **no automated test suite** and no dedicated linter. "Lint" is effectively the build with nullable + analyzers: `dotnet build Heimdall.slnx -c Debug`.
- Expected, benign warnings on Linux: many `CA1416` "only supported on: windows" warnings from the Agent/API Windows code paths, and one `NU1903` advisory for the transitive `SQLitePCLRaw.lib.e_sqlite3` package. Neither blocks build or run.
- Quick health check while the API runs: `curl http://localhost:5080/api/health`.

### .NET SDK
- The .NET 10 SDK lives in `~/.dotnet` and is on `PATH` via `~/.bashrc`. If `dotnet` is not found in a non-login shell, use `~/.dotnet/dotnet` or `export PATH="$HOME/.dotnet:$PATH"`.
