#requires -Version 5.1
<#
.SYNOPSIS
  Create a Windows shortcut (.lnk) with a custom icon.
.DESCRIPTION
  Used by Pack-WorkstationCollector and maintainers to ship helmet-icon shortcuts
  that point at .cmd launchers (Explorer shows the .ico, not the generic CMD icon).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ShortcutPath,

    [Parameter(Mandatory = $true)]
    [string]$TargetPath,

    [Parameter(Mandatory = $true)]
    [string]$IconPath,

    [string]$WorkingDirectory = (Split-Path -Parent $TargetPath),
    [string]$Description = ''
)

$ErrorActionPreference = 'Stop'

foreach ($path in @($TargetPath, $IconPath)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Path not found: $path"
    }
}

$dir = Split-Path -Parent $ShortcutPath
if ($dir -and -not (Test-Path -LiteralPath $dir)) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}

$target = (Resolve-Path -LiteralPath $TargetPath).ProviderPath
$workDir = (Resolve-Path -LiteralPath $WorkingDirectory).ProviderPath
$icon = (Resolve-Path -LiteralPath $IconPath).ProviderPath

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($ShortcutPath)
$shortcut.TargetPath = $target
$shortcut.WorkingDirectory = $workDir
$shortcut.IconLocation = "$icon,0"
if ($Description) {
    $shortcut.Description = $Description
}
$shortcut.Save()

Write-Output "Created shortcut: $ShortcutPath"
