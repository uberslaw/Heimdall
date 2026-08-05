#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Deploy Heimdall.Api Release build to Program Files and restart the service.
  Run elevated after `dotnet build -c Release` so Discovery dropdowns/sort ship.
#>
$ErrorActionPreference = 'Stop'
$src = 'C:\Heimdall\src\Heimdall.Api\bin\Release\net10.0'
$dest = 'C:\Program Files\Heimdall\Api'
if (-not (Test-Path (Join-Path $src 'Heimdall.Api.dll'))) {
  throw "Build output missing. Run: dotnet build C:\Heimdall\src\Heimdall.Api\Heimdall.Api.csproj -c Release"
}

Write-Host "Stopping HeimdallApi..."
Stop-Service HeimdallApi -Force
Start-Sleep -Seconds 2

$files = @(
  'Heimdall.Api.dll','Heimdall.Api.exe','Heimdall.Api.pdb',
  'Heimdall.Shared.dll','Heimdall.Shared.pdb',
  'Heimdall.Api.deps.json','Heimdall.Api.runtimeconfig.json'
)
foreach ($f in $files) {
  $from = Join-Path $src $f
  if (Test-Path $from) {
    Copy-Item $from (Join-Path $dest $f) -Force
    Write-Host "Copied $f"
  }
}
if (Test-Path (Join-Path $src 'wwwroot')) {
  Copy-Item (Join-Path $src 'wwwroot\*') (Join-Path $dest 'wwwroot') -Recurse -Force
  Write-Host "Copied wwwroot"
}

Write-Host "Starting HeimdallApi..."
Start-Service HeimdallApi
Start-Sleep -Seconds 2
Get-Service HeimdallApi | Format-Table Name, Status
Write-Host "Done. Open Discovery (Show classified) to see Category/Subcategory dropdowns."
