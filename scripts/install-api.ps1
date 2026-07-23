#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Verbose Heimdall API + dashboard Windows service installer.
.NOTES
  Prefer scripts\Install-Api.cmd when double-clicking from Explorer (keeps console open).
  Log: %ProgramData%\Heimdall\logs\install-api-*.log
#>
param(
    [string]$InstallDir = "$env:ProgramFiles\Heimdall\Api",
    [int]$Port = 5080,
    [string]$ApiKey = "heimdall-poc-key"
)

$ErrorActionPreference = "Stop"
$script:LastError = $null
$script:LogPath = $null
$exitCode = 1

function Write-Log {
    param(
        [string]$Message,
        [ValidateSet("INFO", "WARN", "ERROR", "STEP", "OK")]
        [string]$Level = "INFO"
    )
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "[$ts] [$Level] $Message"
    switch ($Level) {
        "STEP"  { Write-Host $line -ForegroundColor Cyan }
        "OK"    { Write-Host $line -ForegroundColor Green }
        "WARN"  { Write-Host $line -ForegroundColor Yellow }
        "ERROR" { Write-Host $line -ForegroundColor Red }
        default { Write-Host $line }
    }
    if ($script:LogPath) {
        Add-Content -Path $script:LogPath -Value $line -Encoding UTF8
    }
}

function Write-Banner {
    param([string]$Title, [ConsoleColor]$Color = [ConsoleColor]::White)
    $bar = "=" * 64
    Write-Host ""
    Write-Host $bar -ForegroundColor $Color
    Write-Host "  $Title" -ForegroundColor $Color
    Write-Host $bar -ForegroundColor $Color
    Write-Host ""
    if ($script:LogPath) {
        Add-Content -Path $script:LogPath -Value "`n$bar`n  $Title`n$bar`n" -Encoding UTF8
    }
}

function Invoke-Logged {
    param(
        [string]$StepName,
        [scriptblock]$Action
    )
    Write-Log ">>> $StepName" -Level STEP
    try {
        & $Action
        Write-Log "<<< $StepName — done" -Level OK
    }
    catch {
        $script:LastError = $_
        Write-Log "<<< $StepName — FAILED: $($_.Exception.Message)" -Level ERROR
        if ($_.ScriptStackTrace) {
            Write-Log $_.ScriptStackTrace -Level ERROR
        }
        throw
    }
}

try {
    $logRoot = Join-Path $env:ProgramData "Heimdall\logs"
    New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $script:LogPath = Join-Path $logRoot "install-api-$stamp.log"

    Write-Banner "Heimdall API installer" Cyan
    Write-Log "Log file: $script:LogPath"
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Write-Log "User: $env:USERNAME | Elevated: $isAdmin"
    Write-Log "Machine: $env:COMPUTERNAME | OS: $([System.Environment]::OSVersion.VersionString)"
    Write-Log "Params: InstallDir=$InstallDir Port=$Port ApiKey=****$($ApiKey.Substring([Math]::Max(0, $ApiKey.Length - 4)))"

    $root = Split-Path -Parent $PSScriptRoot
    $project = Join-Path $root "src\Heimdall.Api\Heimdall.Api.csproj"
    Write-Log "Repo root: $root"
    Write-Log "Project: $project"

    Invoke-Logged "Check .NET SDK" {
        $dotnet = Get-Command dotnet -ErrorAction Stop
        Write-Log "dotnet: $($dotnet.Source)"
        $sdks = & dotnet --list-sdks 2>&1
        $sdks | ForEach-Object { Write-Log "  SDK: $_" }
        if (-not ($sdks | Where-Object { $_ -match '^10\.' })) {
            Write-Log "No .NET 10 SDK listed — publish may fail. Install .NET 10 SDK." -Level WARN
        }
    }

    Invoke-Logged "Ensure project exists" {
        if (-not (Test-Path $project)) {
            throw "Project not found: $project — run from a full Heimdall clone (scripts next to src)."
        }
        Write-Log "Project OK"
    }

    Invoke-Logged "Ensure ProgramData\Heimdall" {
        $dataDir = Join-Path $env:ProgramData "Heimdall"
        New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
        Write-Log "Data dir: $dataDir"
    }

    Invoke-Logged "Ensure install directory" {
        New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
        Write-Log "InstallDir: $InstallDir"
    }

    Invoke-Logged "dotnet publish (verbose)" {
        Write-Log "Command: dotnet publish `"$project`" -c Release -o `"$InstallDir`" --self-contained false -v detailed"
        & dotnet publish $project -c Release -o $InstallDir --self-contained false -v detailed 2>&1 | ForEach-Object {
            $line = "$_"
            Write-Host $line
            Add-Content -Path $script:LogPath -Value $line -Encoding UTF8
        }
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish exited with code $LASTEXITCODE"
        }
        $exe = Join-Path $InstallDir "Heimdall.Api.exe"
        if (-not (Test-Path $exe)) {
            throw "Expected exe missing after publish: $exe"
        }
        Write-Log "Published: $exe"
    }

    Invoke-Logged "Write appsettings.json" {
        $appsettings = Join-Path $InstallDir "appsettings.json"
        $dbPath = Join-Path $env:ProgramData "Heimdall\heimdall.db"
        $json = @{
            ConnectionStrings = @{
                Heimdall = "Data Source=$dbPath"
            }
            Heimdall = @{
                ApiKey = $ApiKey
            }
            Logging = @{
                LogLevel = @{
                    Default = "Information"
                    "Microsoft.AspNetCore" = "Warning"
                }
            }
            AllowedHosts = "*"
            Urls = "http://0.0.0.0:$Port"
        } | ConvertTo-Json -Depth 5
        Set-Content -Path $appsettings -Value $json -Encoding UTF8
        Write-Log "Wrote $appsettings"
        Write-Log "SQLite: $dbPath"
        Write-Log "Urls: http://0.0.0.0:$Port"
        Write-Log "ApiKey (last 4): ****$($ApiKey.Substring([Math]::Max(0, $ApiKey.Length - 4)))"
    }

    Invoke-Logged "Stop/remove existing HeimdallApi service" {
        $svc = Get-Service -Name "HeimdallApi" -ErrorAction SilentlyContinue
        if ($svc) {
            Write-Log "Existing service Status=$($svc.Status)"
            if ($svc.Status -eq "Running") {
                Write-Log "Stopping HeimdallApi..."
                Stop-Service HeimdallApi -Force -ErrorAction SilentlyContinue
                Start-Sleep -Seconds 2
            }
            Write-Log "sc.exe delete HeimdallApi"
            $del = & sc.exe delete HeimdallApi 2>&1
            $del | ForEach-Object { Write-Log "  $_" }
            Start-Sleep -Seconds 2
        }
        else {
            Write-Log "No existing HeimdallApi service"
        }
    }

    Invoke-Logged "Create HeimdallApi service" {
        $exe = Join-Path $InstallDir "Heimdall.Api.exe"
        Write-Log "sc.exe create HeimdallApi binPath= `"$exe`" start= auto"
        $create = & sc.exe create HeimdallApi binPath= "`"$exe`"" start= auto DisplayName= "Heimdall API" 2>&1
        $create | ForEach-Object { Write-Log "  $_" }
        if ($LASTEXITCODE -ne 0) {
            throw "sc.exe create failed with exit $LASTEXITCODE"
        }
        $desc = & sc.exe description HeimdallApi "Heimdall ingest API and dashboard" 2>&1
        $desc | ForEach-Object { Write-Log "  $_" }
    }

    Invoke-Logged "Start HeimdallApi" {
        try {
            Start-Service HeimdallApi -ErrorAction Stop
        }
        catch {
            Write-Log "Start-Service error: $($_.Exception.Message)" -Level ERROR
            $svc = Get-Service HeimdallApi -ErrorAction SilentlyContinue
            if ($svc) { Write-Log "Service Status=$($svc.Status)" -Level ERROR }
            throw
        }
        Start-Sleep -Seconds 2
        $svc = Get-Service HeimdallApi
        Write-Log "Service Status=$($svc.Status)"
        if ($svc.Status -ne "Running") {
            throw "HeimdallApi did not reach Running (Status=$($svc.Status))"
        }
    }

    Invoke-Logged "Probe health endpoint" {
        $url = "http://localhost:$Port/api/health"
        Write-Log "GET $url"
        Start-Sleep -Seconds 1
        try {
            $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 10
            Write-Log "HTTP $($r.StatusCode) — $($r.Content)"
        }
        catch {
            Write-Log "Health probe failed (service may still be starting): $($_.Exception.Message)" -Level WARN
        }
    }

    Write-Banner "SUCCESS — Heimdall API installed" Green
    Write-Log "Dashboard: http://localhost:$Port"
    Write-Log "Health:    http://localhost:$Port/api/health"
    Write-Log "Service:   HeimdallApi"
    Write-Log "Log file:  $script:LogPath"
    $exitCode = 0
}
catch {
    $script:LastError = $_
    Write-Banner "FAILURE — Heimdall API install did not complete" Red
    Write-Log "Last error: $($_.Exception.Message)" -Level ERROR
    if ($_.ScriptStackTrace) { Write-Log $_.ScriptStackTrace -Level ERROR }
    Write-Log "Send this log for analysis: $script:LogPath" -Level WARN
    $exitCode = 1
}
finally {
    Write-Host ""
    Write-Host "Full log path (copy this for Cursor / support):" -ForegroundColor Yellow
    Write-Host "  $script:LogPath" -ForegroundColor Yellow
    if ($script:LastError -and $exitCode -ne 0) {
        Write-Host ""
        Write-Host "Last error:" -ForegroundColor Red
        Write-Host "  $($script:LastError.Exception.Message)" -ForegroundColor Red
    }
    Write-Host ""
    Read-Host "Press Enter to close"
    exit $exitCode
}
