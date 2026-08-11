# Elevated one-shot: publish API (exclude appsettings), copy TuflowLauncher beside agent, restart HeimdallApi.
# Shows the same WinForms progress window as install-api (step X of Y + ETA).
# Exit 0 = publish + deploy + /api/health OK. Flood UI pages are NOT used as a gate (they return 403
# without an interactive Windows identity / flood membership).
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $repoRoot 'src\Heimdall.Api\Heimdall.Api.csproj'))) {
    # Fallback when script is invoked from an unexpected layout
    $repoRoot = 'C:\Heimdall'
}

$project = Join-Path $repoRoot 'src\Heimdall.Api\Heimdall.Api.csproj'
$publish = Join-Path $repoRoot 'dist\_publish\Api'
$dest = Join-Path $env:ProgramFiles 'Heimdall\Api'
$agentLauncher = Join-Path $env:ProgramFiles 'Heimdall\Agent\TuflowLauncher'
$launcherSrc = Join-Path $repoRoot 'dist\TuflowLauncher-publish'
$logDir = Join-Path $env:ProgramData 'Heimdall\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir ("republish-api-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
$deployLog = Join-Path $logDir 'republish-api-deploy.log'

$timingHelper = Join-Path $PSScriptRoot 'Heimdall-InstallApiTiming.ps1'
if (Test-Path -LiteralPath $timingHelper) {
    . $timingHelper
    # Separate baseline from full installs (redeploy is usually shorter).
    $script:InstallApiTimingFile = Join-Path $env:LOCALAPPDATA 'Heimdall\republish-api-timing.json'
    $script:InstallApiTimingDefaultColdSec = 90
}

$script:RepublishTotalSteps = 6
$script:RepublishStartedAt = $null
$script:RepublishExitCode = 1
$script:RepublishTimingEstimate = $null

function Write-RepublishLog([string]$Message, [string]$Level = 'INFO') {
    $line = "[{0:HH:mm:ss}] [{1}] {2}" -f (Get-Date), $Level, $Message
    if (Get-Command Test-InstallApiProgressActive -ErrorAction SilentlyContinue) {
        if (Test-InstallApiProgressActive) {
            Add-Content -LiteralPath $log -Value $line -ErrorAction SilentlyContinue
            Add-Content -LiteralPath $deployLog -Value $line -ErrorAction SilentlyContinue
            if ($Level -in @('STEP', 'OK', 'ERROR', 'WARN') -and (Get-Command Set-InstallApiProgressStatus -ErrorAction SilentlyContinue)) {
                Set-InstallApiProgressStatus -StatusLine $Message
            }
            return
        }
    }
    Write-Host $line
    Add-Content -LiteralPath $log -Value $line -ErrorAction SilentlyContinue
    Add-Content -LiteralPath $deployLog -Value $line -ErrorAction SilentlyContinue
}

function Set-RepublishStep([int]$Index, [string]$Name) {
    if (Get-Command Set-InstallApiProgressStep -ErrorAction SilentlyContinue) {
        Set-InstallApiProgressStep -StepIndex $Index -TotalSteps $script:RepublishTotalSteps -StepName $Name
    }
    Write-RepublishLog $Name -Level STEP
}

function Wait-RepublishSeconds([int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (Get-Command Update-InstallApiProgressDisplay -ErrorAction SilentlyContinue) {
            if (Test-InstallApiProgressActive) { Update-InstallApiProgressDisplay }
        }
        Start-Sleep -Milliseconds 250
    }
}

try {
    $script:RepublishStartedAt = Get-Date
    Write-RepublishLog "Log file: $log"

    if (Get-Command Get-InstallApiTimingEstimate -ErrorAction SilentlyContinue) {
        $script:RepublishTimingEstimate = Get-InstallApiTimingEstimate -StartedAt $script:RepublishStartedAt
        $estMmSs = Format-InstallApiDurationMmSs -TotalSec $script:RepublishTimingEstimate.EstimatedSec
        Write-RepublishLog ("Estimated redeploy time: ~{0} ({1}s; baseline {2}s from {3})" -f `
            $estMmSs,
            $script:RepublishTimingEstimate.EstimatedSec,
            $script:RepublishTimingEstimate.BaselineSec,
            $script:RepublishTimingEstimate.Source)
        Start-InstallApiConsoleCountdown `
            -FinishAt $script:RepublishTimingEstimate.FinishAt `
            -EstimatedSec $script:RepublishTimingEstimate.EstimatedSec `
            -LogPath $log `
            -TotalSteps $script:RepublishTotalSteps `
            -WindowTitle 'Heimdall API redeploy'
    }

    Set-RepublishStep 1 'Preparing'
    if (-not (Test-Path -LiteralPath $project)) {
        throw "Project not found: $project"
    }
    Write-RepublishLog "Project: $project"

    Set-RepublishStep 2 'Publishing API'
    Write-RepublishLog "Publishing API to $publish..."
    if (Test-Path -LiteralPath $publish) {
        Remove-Item -LiteralPath $publish -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $publish | Out-Null

    $finishAt = if ($script:RepublishTimingEstimate) {
        $script:RepublishTimingEstimate.FinishAt
    }
    else {
        $script:RepublishStartedAt.AddSeconds(90)
    }

    if (Get-Command Invoke-DotNetPublishWithProgress -ErrorAction SilentlyContinue) {
        $publishExit = Invoke-DotNetPublishWithProgress `
            -Project $project `
            -OutputDir $publish `
            -FinishAt $finishAt `
            -LogPath $log
        if ($publishExit -ne 0) {
            throw "dotnet publish failed: $publishExit (see $log)"
        }
    }
    else {
        $publishLog = Join-Path $logDir ("republish-api-publish-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
        & dotnet publish $project -c Release -o $publish --self-contained false -v minimal *>&1 |
            Tee-Object -FilePath $publishLog |
            ForEach-Object { $_ }
        if ($LASTEXITCODE -ne 0) {
            Write-RepublishLog "Publish log: $publishLog" -Level ERROR
            throw "dotnet publish failed: $LASTEXITCODE (see $publishLog)"
        }
    }

    Set-RepublishStep 3 'Stopping HeimdallApi'
    Write-RepublishLog 'Stopping HeimdallApi...'
    Stop-Service HeimdallApi -Force -ErrorAction SilentlyContinue
    Wait-RepublishSeconds 2

    Set-RepublishStep 4 'Deploying binaries'
    Write-RepublishLog 'Robocopy (exclude appsettings)...'
    & robocopy $publish $dest /E /XF appsettings.json appsettings.*.json /NFL /NDL /NJH /NJS /nc /ns /np
    # robocopy 0-7 = success
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed: $LASTEXITCODE" }

    Set-RepublishStep 5 'TuflowLauncher (optional)'
    if (Test-Path (Join-Path $env:ProgramFiles 'Heimdall\Agent')) {
        Write-RepublishLog 'Copying TuflowLauncher beside agent...'
        New-Item -ItemType Directory -Force -Path $agentLauncher | Out-Null
        if (Test-Path -LiteralPath $launcherSrc) {
            & robocopy $launcherSrc $agentLauncher /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
            if ($LASTEXITCODE -ge 8) {
                Write-RepublishLog "WARN: TuflowLauncher robocopy exit $LASTEXITCODE (continuing)" -Level WARN
            }
        }
        else {
            Write-RepublishLog "TuflowLauncher source missing ($launcherSrc) - skipped" -Level WARN
        }
    }
    else {
        Write-RepublishLog 'Agent folder not installed - skipping TuflowLauncher copy'
    }

    Set-RepublishStep 6 'Start service + health'
    Write-RepublishLog 'Starting HeimdallApi...'
    Start-Service HeimdallApi
    Wait-RepublishSeconds 4

    $svc = Get-Service HeimdallApi
    if ($svc.Status -ne 'Running') {
        throw "HeimdallApi status is $($svc.Status), expected Running"
    }

    # Public health only - do not probe Flood-gated Razor pages (403 without flood access).
    $health = Invoke-RestMethod 'http://127.0.0.1:5080/api/health' -TimeoutSec 15
    Write-RepublishLog ("Health: " + ($health | ConvertTo-Json -Compress)) -Level OK
    if (-not $health -or "$($health.status)" -notmatch '^(?i)ok$') {
        throw "Unexpected health payload: $($health | ConvertTo-Json -Compress)"
    }

    Write-RepublishLog "DONE (log: $log)" -Level OK
    if ($script:RepublishStartedAt -and (Get-Command Save-InstallApiTimingResult -ErrorAction SilentlyContinue)) {
        $actualSec = [int][Math]::Max(0, ((Get-Date) - $script:RepublishStartedAt).TotalSeconds)
        Save-InstallApiTimingResult -DurationSec $actualSec -Success $true
        Write-RepublishLog ("Redeploy duration: {0} ({1}s) - saved for next estimate" -f `
            (Format-InstallApiDurationMmSs -TotalSec $actualSec), $actualSec)
    }
    $script:RepublishExitCode = 0
}
catch {
    Write-RepublishLog "FAIL: $($_.Exception.Message)" -Level ERROR
    try { Start-Service HeimdallApi -ErrorAction SilentlyContinue } catch { }
    if ($script:RepublishStartedAt -and (Get-Command Save-InstallApiTimingResult -ErrorAction SilentlyContinue)) {
        $actualSec = [int][Math]::Max(0, ((Get-Date) - $script:RepublishStartedAt).TotalSeconds)
        Save-InstallApiTimingResult -DurationSec $actualSec -Success $false
    }
    Write-Host "Full log: $log"
    $script:RepublishExitCode = 1
}
finally {
    if (Get-Command Stop-InstallApiConsoleCountdown -ErrorAction SilentlyContinue) {
        if ($script:RepublishExitCode -eq 0) {
            if (Get-Command Set-InstallApiProgressStatus -ErrorAction SilentlyContinue) {
                Set-InstallApiProgressStatus -StatusLine 'Redeploy finished successfully'
            }
            if ($script:InstallApiProgressLabels.Eta) {
                $script:InstallApiProgressLabels.Eta.Text = 'Done'
            }
            # Brief pause so the window is readable when launched elevated without -NoExit.
            Wait-RepublishSeconds 3
            Stop-InstallApiConsoleCountdown
        }
        else {
            if (Get-Command Set-InstallApiProgressStatus -ErrorAction SilentlyContinue) {
                Set-InstallApiProgressStatus -StatusLine 'Redeploy failed - see Open logs folder'
            }
            Stop-InstallApiConsoleCountdown -KeepWindowOpen
            Wait-RepublishSeconds 8
            Stop-InstallApiConsoleCountdown
        }
    }
}

exit $script:RepublishExitCode
