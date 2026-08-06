#Requires -Version 5.1
<#
.SYNOPSIS
  Resolves the next simple integer client productVersion for a pack.

.DESCRIPTION
  next = max(csproj InformationalVersion/Version, existing pack VERSION.json, published) + 1
  unless -ForceVersion / HEIMDALL_CLIENT_PRODUCT_VERSION is set (exact value).

  Version bump is independent of source fingerprint / Ready status — every normal pack
  run advances N+1 even when fingerprints already match (Ready only unlocks Deploy).

  Prints only the integer to stdout (for capture by Pack-WorkstationCollector.cmd).
#>
param(
    [Parameter(Mandatory)][string]$RepoRoot,
    [string]$PackFolder = "",
    [string]$PublishedVersion = "",
    [string]$ForceVersion = ""
)

$ErrorActionPreference = "Stop"
$RepoRoot = [IO.Path]::GetFullPath($RepoRoot)

function Get-SimpleInt([string]$raw) {
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    $core = $raw.Trim()
    $plus = $core.IndexOf('+')
    if ($plus -ge 0) { $core = $core.Substring(0, $plus).Trim() }
    if ($core -match '^\d+$') { return [int]$core }
    return $null
}

if (-not [string]::IsNullOrWhiteSpace($ForceVersion)) {
    $forced = Get-SimpleInt $ForceVersion
    if ($null -eq $forced -or $forced -lt 1) {
        throw "ForceVersion must be a positive integer, got: $ForceVersion"
    }
    Write-Output $forced
    exit 0
}

$candidates = New-Object System.Collections.Generic.List[int]

$csproj = Join-Path $RepoRoot "src\Heimdall.Agent\Heimdall.Agent.csproj"
if (Test-Path -LiteralPath $csproj) {
    $xml = [xml](Get-Content -LiteralPath $csproj -Raw)
    $info = $xml.SelectSingleNode("//InformationalVersion")
    $ver = $xml.SelectSingleNode("//Version")
    foreach ($node in @($info, $ver)) {
        if ($null -eq $node) { continue }
        $n = Get-SimpleInt $node.InnerText
        if ($null -ne $n) { $candidates.Add($n) }
    }
}

if ([string]::IsNullOrWhiteSpace($PackFolder)) {
    $PackFolder = Join-Path $RepoRoot "dist\Heimdall-Client"
}
$versionJson = Join-Path $PackFolder "VERSION.json"
if (Test-Path -LiteralPath $versionJson) {
    try {
        $obj = Get-Content -LiteralPath $versionJson -Raw | ConvertFrom-Json
        $n = Get-SimpleInt ([string]$obj.productVersion)
        if ($null -ne $n) { $candidates.Add($n) }
    }
    catch { /* ignore corrupt VERSION.json */ }
}

if (-not [string]::IsNullOrWhiteSpace($PublishedVersion)) {
    $n = Get-SimpleInt $PublishedVersion
    if ($null -ne $n) { $candidates.Add($n) }
}
elseif (-not [string]::IsNullOrWhiteSpace($env:HEIMDALL_PUBLISHED_CLIENT_VERSION)) {
    $n = Get-SimpleInt $env:HEIMDALL_PUBLISHED_CLIENT_VERSION
    if ($null -ne $n) { $candidates.Add($n) }
}

$max = 0
foreach ($c in $candidates) {
    if ($c -gt $max) { $max = $c }
}
$next = $max + 1
if ($next -lt 1) { $next = 1 }

Write-Output $next
