<#
.SYNOPSIS
  Enumerate installed programs on a vanilla SOE / golden-image machine for Heimdall SOE excludes.
.DESCRIPTION
  Reads Uninstall registry keys (HKLM/HKCU, 64-bit + 32-bit Wow6432Node).
  Writes a CSV of DisplayName, Publisher, EstimateProcessName, SuggestedIgnore=true,
  and optionally compares to the in-repo SoeCatalog seed names.
  Run on a clean SOE image -> review CSV -> feed Config SOE excludes / Autogenerate.
.NOTES
  Prefer scripts\Inspect-SoeInstalledPrograms.cmd so the console stays open when double-clicked.
  Log + CSV under %LOCALAPPDATA%\Heimdall\ (or -OutDir).
#>
param(
    [string]$OutDir = "$env:LOCALAPPDATA\Heimdall",
    [string]$RepoRoot = "",
    [switch]$CompareCatalog
)

$ErrorActionPreference = "Continue"
$script:LogPath = $null

function Write-Step([string]$Message) {
    Write-Host "[*] $Message" -ForegroundColor Cyan
    if ($script:LogPath) { Add-Content -Path $script:LogPath -Value "[*] $Message" -Encoding UTF8 }
}

function Write-Note([string]$Message) {
    Write-Host "    $Message"
    if ($script:LogPath) { Add-Content -Path $script:LogPath -Value "    $Message" -Encoding UTF8 }
}

function Write-Warn([string]$Message) {
    Write-Host "[!] $Message" -ForegroundColor Yellow
    if ($script:LogPath) { Add-Content -Path $script:LogPath -Value "[!] $Message" -Encoding UTF8 }
}

function Get-EstimateProcessName {
    param([string]$DisplayName, [string]$DisplayIcon, [string]$InstallLocation)

    # Prefer basename of DisplayIcon / uninstall string when it looks like an exe
    foreach ($candidate in @($DisplayIcon, $InstallLocation)) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        $clean = $candidate.Trim().Trim('"')
        if ($clean -match '(?i)([A-Za-z0-9_\-\.]+)\.exe') {
            return $Matches[1]
        }
    }

    if ([string]::IsNullOrWhiteSpace($DisplayName)) { return "" }

    # Best-effort: strip version/parens, take first token-ish word run
    $s = $DisplayName -replace '\([^)]*\)', '' -replace '\[[^\]]*\]', ''
    $s = $s -replace '(?i)\b(v|version)?\s*\d+(\.\d+)+\b', ''
    $s = ($s -replace '[^\w\s\-\.]', ' ').Trim()
    if ([string]::IsNullOrWhiteSpace($s)) { return "" }
    $parts = $s -split '\s+' | Where-Object { $_.Length -ge 2 }
    if ($parts.Count -eq 0) { return "" }
    # Prefer a single camel/concatenated guess from first 1-2 words
    if ($parts.Count -eq 1) { return $parts[0] }
    return ($parts[0] + $parts[1])
}

function Get-UninstallEntries {
    $paths = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKCU:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"
    )

    $rows = @()
    foreach ($path in $paths) {
        try {
            $items = Get-ItemProperty -Path $path -ErrorAction SilentlyContinue
        }
        catch {
            continue
        }

        foreach ($item in $items) {
            $name = [string]$item.DisplayName
            if ([string]::IsNullOrWhiteSpace($name)) { continue }
            # Skip Windows Updates / KB-style noise
            if ($name -match '(?i)^Update for|^Security Update|^Hotfix') { continue }

            $publisher = [string]$item.Publisher
            $icon = [string]$item.DisplayIcon
            $loc = [string]$item.InstallLocation
            $est = Get-EstimateProcessName -DisplayName $name -DisplayIcon $icon -InstallLocation $loc

            $rows += [pscustomobject]@{
                DisplayName          = $name.Trim()
                Publisher            = if ($publisher) { $publisher.Trim() } else { "" }
                EstimateProcessName  = $est
                SuggestedIgnore      = "true"
                DisplayVersion       = [string]$item.DisplayVersion
                InstallLocation      = $loc
                UninstallString      = [string]$item.UninstallString
                RegistryHive         = $path
            }
        }
    }

    # Dedupe by DisplayName+Publisher
    $rows | Sort-Object DisplayName, Publisher -Unique
}

function Get-CatalogProcessNames {
    param([string]$Root)
    $catalog = Join-Path $Root "src\Heimdall.Api\Services\SoeCatalog.cs"
    if (-not (Test-Path $catalog)) {
        Write-Warn "SoeCatalog.cs not found at $catalog - skip compare"
        return @()
    }

    $text = Get-Content -Path $catalog -Raw -Encoding UTF8
    $names = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($m in [regex]::Matches($text, '"([^"]+)",\s*"([^"]+)"')) {
        # DisplayName, ProcessName pairs in seed tuples - take process name (2nd)
        [void]$names.Add($m.Groups[2].Value)
    }
    return @($names)
}

try {
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host "  Heimdall SOE installed-programs inspector" -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host ""

    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $script:LogPath = Join-Path $OutDir "soe-inspect-$stamp.log"
    $csvPath = Join-Path $OutDir "soe-installed-$stamp.csv"

    Write-Step "Hostname: $env:COMPUTERNAME"
    Write-Step "Log: $script:LogPath"
    Write-Step "CSV: $csvPath"

    Write-Step "Reading Uninstall registry keys (HKLM/HKCU 64+32)"
    $entries = @(Get-UninstallEntries)
    Write-Note "Found $($entries.Count) installed program entries (after dedupe / KB filter)"

    if ($CompareCatalog -or $RepoRoot) {
        if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
            # scripts\ -> repo root
            $RepoRoot = Split-Path $PSScriptRoot -Parent
        }
        Write-Step "Comparing EstimateProcessName to SoeCatalog under $RepoRoot"
        $catalogNames = Get-CatalogProcessNames -Root $RepoRoot
        Write-Note "Catalog process names: $($catalogNames.Count)"
        $entries = $entries | ForEach-Object {
            $inCat = $false
            if (-not [string]::IsNullOrWhiteSpace($_.EstimateProcessName)) {
                $inCat = $catalogNames -contains $_.EstimateProcessName
            }
            $_ | Add-Member -NotePropertyName InSoeCatalog -NotePropertyValue $inCat -PassThru
        }
    }

    $entries | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
    Write-Step "Wrote CSV ($($entries.Count) rows)"

    Write-Host ""
    Write-Host "Next steps:" -ForegroundColor Green
    Write-Note "1. Open $csvPath on the golden / vanilla SOE image"
    Write-Note "2. Review EstimateProcessName (best-effort) and SuggestedIgnore=true rows"
    Write-Note "3. Feed confirmed process names into Config -> SOE excludes / Autogenerate"
    Write-Note "4. Optionally merge into SoeCatalog.cs seed list for future installs"
    Write-Host ""
    Write-Step "Done. Log: $script:LogPath"
}
catch {
    Write-Host "[X] $($_.Exception.Message)" -ForegroundColor Red
    if ($script:LogPath) {
        Add-Content -Path $script:LogPath -Value "[X] $($_.Exception.Message)" -Encoding UTF8
    }
    exit 1
}
finally {
    Write-Host ""
    Write-Host "Press Enter to close..." -ForegroundColor DarkGray
    try { [void][Console]::ReadLine() } catch { Start-Sleep -Seconds 3 }
}
