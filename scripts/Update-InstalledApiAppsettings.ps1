#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Merge StaffAccess into the installed Heimdall API appsettings.json (Program Files).
.NOTES
  Double-click scripts\Update-InstalledApiAppsettings.cmd or run this script elevated.
  Stops HeimdallApi briefly if running so the file is not locked.
#>
$ErrorActionPreference = "Stop"

$appsettingsPath = Join-Path $env:ProgramFiles "Heimdall\Api\appsettings.json"
if (-not (Test-Path -LiteralPath $appsettingsPath)) {
    Write-Error "Not found: $appsettingsPath — run install-api.ps1 first."
}

$staffAccess = @{
    RequireWindowsAuth = $true
    EmailDomainSuffixes = @("arup.com")
    AllowDevBypass = $false
    AdminEmails = @("christopher.owen@arup.com")
    AdminPreviewMinutes = 30
}

$svc = Get-Service -Name "HeimdallApi" -ErrorAction SilentlyContinue
$wasRunning = $svc -and $svc.Status -eq "Running"
if ($wasRunning) {
    Write-Host "Stopping HeimdallApi..."
    Stop-Service HeimdallApi -Force
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Service HeimdallApi -ErrorAction SilentlyContinue).Status -ne "Stopped") {
        if ((Get-Date) -ge $deadline) { throw "Timed out waiting for HeimdallApi to stop." }
        Start-Sleep -Seconds 1
    }
}

try {
    $config = Get-Content -LiteralPath $appsettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $config.Heimdall) {
        $config | Add-Member -NotePropertyName Heimdall -NotePropertyValue ([pscustomobject]@{})
    }
    $config.Heimdall | Add-Member -NotePropertyName StaffAccess -NotePropertyValue $staffAccess -Force
    $json = $config | ConvertTo-Json -Depth 10
    Set-Content -LiteralPath $appsettingsPath -Value $json -Encoding UTF8
    Write-Host "Updated: $appsettingsPath"
    Write-Host "StaffAccess merged under Heimdall."
}
finally {
    if ($wasRunning) {
        Write-Host "Starting HeimdallApi..."
        Start-Service HeimdallApi
    }
}

Write-Host ""
Write-Host "Done. Restart HeimdallApi if you stopped it manually:  Restart-Service HeimdallApi"
