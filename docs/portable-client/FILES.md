# Heimdall Client — pack contents

## Portable package (after pack)

```text
Heimdall-Client/
├── Install.lnk                        # PRIMARY on target PCs
├── Install.cmd
├── Install-Client.ps1
├── Heimdall-VersionCompare.ps1
├── Heimdall-CollectorInstall.ps1
├── Install-WorkstationCollector.cmd   # Silent/scripted install entry
├── Install-WorkstationCollector.ps1   # Lock/stages/1072 waits/LKG + optional heal register
├── Heimdall-AgentHeal.ps1             # Phase 3 self-heal watchdog (opt-in at install)
├── Set-ApiUrl.lnk                     # Point agent at API (IP set in .cmd)
├── Set-HeimdallAgentApiBaseUrl.cmd    # Edit API_IP at top; run as admin
├── Set-HeimdallAgentApiBaseUrl.ps1
├── pack-api.json                      # Optional bake from Create client pack
├── Heimdall-Setup.lnk / .cmd          # Advanced tools (health, logs)
├── Heimdall-LaunchControl.*           # Compat aliases → Setup
├── Heimdall-LaunchRdp.vbs             # One-click Connect (wscript, no PowerShell)
├── Register-HeimdallRdp.cmd           # Registers heimdall-rdp → wscript
├── heimdall.ico
├── README.md
├── FILES.md
├── VERSION.json
├── PACKED.txt
└── payload/                           # REQUIRED
    ├── Heimdall.Agent.exe
    └── (self-contained publish files)
```

## Maintainer docs

| Path | Purpose |
|------|---------|
| `docs/CLIENT.md` | Full agent architecture (also in Help → Client / agent) |
| `docs/portable-client/` | Docs copied into pack (not installable) |
| `scripts/Heimdall-Setup.cmd` / `.ps1` | Guided Setup UI (pack, API, tools) |
| `scripts/Heimdall-LaunchControl.*` | Compat wrappers → Setup |
| `scripts/Pack-WorkstationCollector.cmd` | Builds `dist/Heimdall-Client` |
| `scripts/Install-WorkstationCollector.cmd` | Silent service install entry |
| `scripts/Install-WorkstationCollector.ps1` | Resilient silent install (lock, stages, heal add-on) |
| `scripts/Heimdall-AgentHeal.ps1` | Optional SYSTEM heal watchdog (install checkbox) |
| `docs/portable-client/` | Docs only (copied into pack) |
| `src/Heimdall.Agent/**` | Published into `payload/` |

## Runtime (target PC)

| Dependency | Bundled? |
|------------|----------|
| .NET 10 runtime | Yes (self-contained) |
| Admin rights | Required for service install |
| Reachable Heimdall API | Configure during install |
