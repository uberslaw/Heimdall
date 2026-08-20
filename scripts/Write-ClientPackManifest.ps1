#Requires -Version 5.1
<#
.SYNOPSIS
  Writes MANIFEST.sha256 and patches VERSION.json with sourceFingerprint for a Heimdall client pack.
  Must match Heimdall.Api.Services.ClientPackFingerprint algorithm.
#>
param(
    [Parameter(Mandatory)][string]$RepoRoot,
    [Parameter(Mandatory)][string]$PackFolder
)

$ErrorActionPreference = "Stop"
$RepoRoot = [IO.Path]::GetFullPath($RepoRoot)
$PackFolder = [IO.Path]::GetFullPath($PackFolder)

$SourceRoots = @(
    "src/Heimdall.Agent",
    "src/Heimdall.Shared",
    "tuflow-automation/TuflowLauncher",
    "Directory.Build.props",
    "scripts/Pack-WorkstationCollector.cmd",
    "scripts/Write-ClientPackManifest.ps1",
    "scripts/Install.cmd",
    "scripts/Install-Client.ps1",
    "scripts/Heimdall-VersionCompare.ps1",
    "scripts/Heimdall-CollectorInstall.ps1",
    "scripts/Install-WorkstationCollector.cmd",
    "scripts/Install-WorkstationCollector.ps1",
    "scripts/Heimdall-AgentHeal.ps1",
    "scripts/Set-HeimdallAgentApiBaseUrl.cmd",
    "scripts/Set-HeimdallAgentApiBaseUrl.ps1",
    "scripts/Write-PackApiUrl.ps1",
    "scripts/Heimdall-Setup.cmd",
    "scripts/Heimdall-LaunchControl.cmd",
    "scripts/Heimdall-LaunchControl.ps1",
    "scripts/Heimdall-LaunchRdp.vbs",
    "scripts/Register-HeimdallRdp.cmd",
    "scripts/New-HeimdallShortcut.ps1",
    "docs/portable-client/README.md",
    "docs/portable-client/FILES.md",
    "assets/heimdall.ico"
)

$SkipDirs = @("bin", "obj", ".git", "node_modules")
$OrdinalIgnoreCase = [StringComparer]::OrdinalIgnoreCase

function Test-SkippedPath([string]$rel) {
    $parts = $rel -split '[\\/]'
    foreach ($p in $parts) {
        if ($SkipDirs -contains $p) { return $true }
    }
    return $false
}

function Get-RelPath([string]$root, [string]$full) {
    $r = $root.TrimEnd('\', '/') + '\'
    $f = [IO.Path]::GetFullPath($full)
    if ($f.StartsWith($r, [StringComparison]::OrdinalIgnoreCase)) {
        return ($f.Substring($r.Length) -replace '\\', '/')
    }
    return ($f -replace '\\', '/')
}

function Get-FileSha256([string]$path) {
    $hash = Get-FileHash -LiteralPath $path -Algorithm SHA256
    return $hash.Hash.ToLowerInvariant()
}

# --- Source fingerprint (ordered relative paths + file bytes) ---
# Sort MUST use OrdinalIgnoreCase to match ClientPackFingerprint.ComputeSourceFingerprint
# (PowerShell Sort-Object default is culture-aware and reorders hyphenated names like Install-Client).
$files = New-Object System.Collections.Generic.List[object]
foreach ($entry in $SourceRoots) {
    $full = [IO.Path]::GetFullPath((Join-Path $RepoRoot ($entry -replace '/', '\')))
    if (Test-Path -LiteralPath $full -PathType Leaf) {
        $files.Add([pscustomobject]@{ Rel = (Get-RelPath $RepoRoot $full); Full = $full })
        continue
    }
    if (-not (Test-Path -LiteralPath $full -PathType Container)) { continue }
    Get-ChildItem -LiteralPath $full -File -Recurse -Force | ForEach-Object {
        $rel = Get-RelPath $RepoRoot $_.FullName
        if (Test-SkippedPath $rel) { return }
        $files.Add([pscustomobject]@{ Rel = $rel; Full = $_.FullName })
    }
}

$ordered = New-Object System.Collections.Generic.List[object]
foreach ($f in [Linq.Enumerable]::OrderBy($files, [Func[object, string]] { param($x) $x.Rel }, $OrdinalIgnoreCase)) {
    $ordered.Add($f)
}

$inc = [System.Security.Cryptography.IncrementalHash]::CreateHash([System.Security.Cryptography.HashAlgorithmName]::SHA256)
foreach ($f in $ordered) {
    $relNorm = ($f.Rel -replace '\\', '/').ToLowerInvariant()
    $line = [Text.Encoding]::UTF8.GetBytes($relNorm + "`n")
    $inc.AppendData($line)
    $bytes = [IO.File]::ReadAllBytes($f.Full)
    if ($bytes.Length -gt 0) {
        $inc.AppendData($bytes)
    }
}
$sourceFingerprint = ([BitConverter]::ToString($inc.GetHashAndReset()) -replace '-', '').ToLowerInvariant()
$inc.Dispose()


# --- Pack MANIFEST.sha256 ---
$manifestMap = New-Object 'System.Collections.Generic.SortedDictionary[string,string]' ($OrdinalIgnoreCase)
Get-ChildItem -LiteralPath $PackFolder -File -Recurse -Force | ForEach-Object {
    $rel = Get-RelPath $PackFolder $_.FullName
    $relNorm = $rel -replace '\\', '/'
    if ($relNorm -eq "MANIFEST.sha256") { return }
    $manifestMap[$relNorm] = Get-FileSha256 $_.FullName
}
$manifestLines = New-Object System.Collections.Generic.List[string]
foreach ($kv in $manifestMap.GetEnumerator()) {
    $manifestLines.Add(("{0}  {1}" -f $kv.Value, $kv.Key))
}
$manifestPath = Join-Path $PackFolder "MANIFEST.sha256"
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[IO.File]::WriteAllLines($manifestPath, $manifestLines, $utf8NoBom)

# --- Patch VERSION.json ---
$versionPath = Join-Path $PackFolder "VERSION.json"
if (Test-Path -LiteralPath $versionPath) {
    $raw = Get-Content -LiteralPath $versionPath -Raw
    $obj = $raw | ConvertFrom-Json
    $obj | Add-Member -NotePropertyName sourceFingerprint -NotePropertyValue $sourceFingerprint -Force
    $json = $obj | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($versionPath, $json + "`n", $utf8NoBom)
}
else {
    Write-Warning "VERSION.json missing at $versionPath - fingerprint computed but not written."
}

Write-Host ("[OK] sourceFingerprint={0}" -f $sourceFingerprint)
Write-Host ("[OK] MANIFEST.sha256 written ({0} files)" -f $manifestLines.Count)
