# Heimdall Client — pack contents

## Portable package (after pack)

```text
Heimdall-Client/
├── Install.lnk                        # PRIMARY on target PCs
├── Install.cmd
├── Install-Client.ps1
├── Heimdall-VersionCompare.ps1
├── Heimdall-CollectorInstall.ps1
├── Install-WorkstationCollector.cmd   # Silent/scripted install
├── Heimdall-Setup.lnk / .cmd          # Advanced tools (health, logs)
├── Heimdall-LaunchControl.*           # Compat aliases → Setup
├── heimdall.ico
├── README.md
├── FILES.md
├── VERSION.json
├── PACKED.txt
└── payload/                           # REQUIRED
    ├── Heimdall.Agent.exe
    └── (self-contained publish files)
```

## Repo sources (maintainers)

| Path | Purpose |
|------|---------|
| `scripts/Install.cmd` / `Install-Client.ps1` | Guided client install |
| `scripts/Heimdall-Setup.cmd` / `.ps1` | Guided Setup UI (pack, API, tools) |
| `scripts/Heimdall-LaunchControl.*` | Compat wrappers → Setup |
| `scripts/Pack-WorkstationCollector.cmd` | Builds `dist/Heimdall-Client` |
| `scripts/Install-WorkstationCollector.cmd` | Silent service install |
| `docs/portable-client/` | Docs only (copied into pack) |
| `src/Heimdall.Agent/**` | Published into `payload/` |

## Runtime (target PC)

| Dependency | Bundled? |
|------------|----------|
| .NET 10 runtime | Yes (self-contained) |
| Admin rights | Required for service install |
| Reachable Heimdall API | Configure during install |
