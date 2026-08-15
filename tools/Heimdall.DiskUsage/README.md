# Heimdall Disk Usage (desktop)

Standalone Windows tool to scan a **local** drive and explore folders by size. Network drives are intentionally omitted (parked — high risk of taxing the link).

Inspired by the Heimdall agent `DiskUsageScanner`, but built for interactive clean-up on the machine you are sitting at.

## Features

- Pick any **fixed** or **removable** local drive
- **NTFS MFT scan** (WizTree-style) — reads the Master File Table for near-instant full-drive trees
- Falls back to a directory walk if the volume is not NTFS or raw volume access fails
- Folders sorted **largest → smallest**
- Expandable tree; size shown as `[12.4 GB]` on the right
- **Double-click** or **Enter** — open that folder in Explorer
- **Delete** key or **Delete…** button — send folder to the **Recycle Bin** (with confirm)
- **Min / Max size** filters (B / KB / MB / GB / TB); leave blank for no filter
- **Exclude system folders** — skips `Windows`, `Program Files`, and `Program Files (x86)`, but still scans and shows `Windows\ccmcache`

## Requirements

- Windows (x64)
- [.NET 10 desktop runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (framework-dependent publish)
- **Run as Administrator** — the app requests elevation via its manifest. Raw volume access (`\\.\C:`) is required for MFT scanning.

## Build / run

```powershell
cd C:\Heimdall\tools\Heimdall.DiskUsage
dotnet run -c Release
```

Framework-dependent publish (small folder — needs .NET 10 desktop runtime on the PC):

```powershell
cd C:\Heimdall\tools\Heimdall.DiskUsage
dotnet publish -c Release -r win-x64 --self-contained false -o ..\..\dist\Disk-Clean-Tool
```

Then run `dist\Disk-Clean-Tool\Heimdall.DiskUsage.exe` (UAC prompt expected).

## How scanning works

1. **MFT (preferred on NTFS):** open the volume, parse the boot sector, stream `$MFT` FILE records, collect `$FILE_NAME` + unnamed `$DATA` sizes, build the folder tree in memory, roll up sizes, sort.
2. **Fallback:** if the drive is FAT/exFAT/ReFS, or opening `\\.\X:` fails (permissions), the tool walks with `Directory.EnumerateFiles` using correct drive-root paths (`C:\`, never bare `C:`) and reports that MFT was unavailable.

## Notes / limitations

- **Admin required** for MFT. Without elevation the UI still starts only if the OS allows it; MFT open will fail and the directory-walk fallback is used (slower).
- **NTFS only** for the fast path. FAT, exFAT, and other formats use the walk fallback.
- Hard links: one preferred Win32 name is kept (size counted once).
- Extension MFT records (`ATTRIBUTE_LIST`) are merged onto their base for sizes/names.
- Non-resident `ATTRIBUTE_LIST` bodies and named alternate data streams are not fully followed (rare under-count possible).
- Access-denied and reparse-point folders are skipped on the walk fallback (no crash).
- Deleting system-critical folders can break Windows — the confirm dialog is there for a reason.
- Not part of the Heimdall agent/API service install; this is a separate utility under `tools/`.
