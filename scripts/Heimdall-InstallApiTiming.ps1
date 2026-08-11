#Requires -Version 5.1
<#
.SYNOPSIS
  Estimate and display Heimdall API install duration / countdown.

.DESCRIPTION
  Dot-source from install-api.ps1 and Heimdall-LaunchControl.ps1.
  Baseline: last successful run in install-api-timing.json, else median of
  successful install-api-*.log durations, else InstallApiTimingDefaultColdSec.
  Estimate adds InstallApiTimingBufferRatio (20%) so countdown rarely hits zero early.
  Install progress: small WinForms window (step X of Y, ETA, status line).
  Verbose publish output goes only to the log file.
  ASCII-only; PS 5.1; UTF-8 BOM.
#>

# Successful install on 2026-07-24 measured ~110s; use 120s when no history exists.
$script:InstallApiTimingDefaultColdSec = 120
$script:InstallApiTimingBufferRatio = 0.20
$script:InstallApiTimingFile = Join-Path $env:LOCALAPPDATA "Heimdall\install-api-timing.json"
$script:InstallApiLogRoot = Join-Path $env:ProgramData "Heimdall\logs"

$script:InstallApiProgressActive = $false
$script:InstallApiProgressFinishAt = $null
$script:InstallApiProgressLogPath = $null
$script:InstallApiProgressForm = $null
$script:InstallApiProgressLabels = @{}
$script:InstallApiProgressStepIndex = 0
$script:InstallApiProgressStepTotal = 0
$script:InstallApiProgressStepName = ""
$script:InstallApiProgressStatusLine = ""
$script:InstallApiProgressPublishActions = 0
$script:InstallApiProgressWinFormsLoaded = $false
$script:InstallApiProgressWindowTitle = "Heimdall API install"

function Get-InstallApiLogDurations {
    $results = @()
    $pattern = Join-Path $script:InstallApiLogRoot "install-api-*.log"
    $logs = @(Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
    foreach ($log in $logs) {
        try {
            $success = Select-String -Path $log.FullName -Pattern "SUCCESS - Heimdall API installed" -Quiet
            $first = Select-String -Path $log.FullName -Pattern '\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]' | Select-Object -First 1
            $last = Select-String -Path $log.FullName -Pattern '\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]' | Select-Object -Last 1
            if (-not $first -or -not $last) { continue }
            $start = [datetime]::ParseExact($first.Matches.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", $null)
            $end = [datetime]::ParseExact($last.Matches.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", $null)
            $sec = [int][Math]::Max(0, ($end - $start).TotalSeconds)
            $results += [PSCustomObject]@{
                DurationSec = $sec
                Success     = [bool]$success
                LogPath     = $log.FullName
            }
        }
        catch {
            continue
        }
    }
    return $results
}

function Get-InstallApiBaselineSeconds {
    if (Test-Path -LiteralPath $script:InstallApiTimingFile) {
        try {
            $raw = Get-Content -LiteralPath $script:InstallApiTimingFile -Raw -Encoding UTF8
            if ($raw) {
                $saved = $raw | ConvertFrom-Json
                if ($saved.lastSuccessDurationSec -and [int]$saved.lastSuccessDurationSec -gt 0) {
                    return [int]$saved.lastSuccessDurationSec
                }
            }
        }
        catch {
        }
    }

    $successDurations = @(Get-InstallApiLogDurations | Where-Object { $_.Success } | ForEach-Object { $_.DurationSec })
    if ($successDurations.Count -gt 0) {
        $sorted = $successDurations | Sort-Object
        $mid = [int][Math]::Floor($sorted.Count / 2)
        if ($sorted.Count % 2 -eq 1) {
            return [int]$sorted[$mid]
        }
        return [int][Math]::Round(($sorted[$mid - 1] + $sorted[$mid]) / 2)
    }

    return $script:InstallApiTimingDefaultColdSec
}

function Get-InstallApiEstimatedSeconds {
    param([int]$BaselineSec = 0)
    if ($BaselineSec -le 0) {
        $BaselineSec = Get-InstallApiBaselineSeconds
    }
    return [int][Math]::Ceiling($BaselineSec * (1 + $script:InstallApiTimingBufferRatio))
}

function Get-InstallApiTimingEstimate {
    param([datetime]$StartedAt = $(Get-Date))

    $baseline = Get-InstallApiBaselineSeconds
    $estimated = Get-InstallApiEstimatedSeconds -BaselineSec $baseline
    $source = "default"
    if (Test-Path -LiteralPath $script:InstallApiTimingFile) {
        try {
            $saved = Get-Content -LiteralPath $script:InstallApiTimingFile -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($saved.lastSuccessDurationSec -and [int]$saved.lastSuccessDurationSec -gt 0) {
                $source = "last-success-file"
            }
        }
        catch {
        }
    }
    if ($source -eq "default") {
        $logHits = @(Get-InstallApiLogDurations | Where-Object { $_.Success })
        if ($logHits.Count -gt 0) {
            $source = "successful-logs"
        }
    }

    return [PSCustomObject]@{
        BaselineSec   = $baseline
        EstimatedSec  = $estimated
        BufferPercent = [int]($script:InstallApiTimingBufferRatio * 100)
        Source        = $source
        StartedAt     = $StartedAt
        FinishAt      = $StartedAt.AddSeconds($estimated)
    }
}

function Format-InstallApiDurationMmSs {
    param([int]$TotalSec)
    $sec = [Math]::Abs($TotalSec)
    $mm = [int][Math]::Floor($sec / 60)
    $ss = $sec % 60
    return "{0}:{1}" -f $mm, ($ss.ToString("00"))
}

function Format-InstallApiCountdownRemaining {
    param(
        [Parameter(Mandatory = $true)][datetime]$FinishAt,
        [datetime]$Now = $(Get-Date)
    )

    $remainingSec = [int][Math]::Ceiling(($FinishAt - $Now).TotalSeconds)
    if ($remainingSec -gt 0) {
        return [PSCustomObject]@{
            RemainingSec = $remainingSec
            Overtime     = $false
            Text         = (Format-InstallApiDurationMmSs -TotalSec $remainingSec)
        }
    }

    $overSec = [int][Math]::Ceiling(($Now - $FinishAt).TotalSeconds)
    return [PSCustomObject]@{
        RemainingSec = 0
        Overtime     = $true
        Text         = "Taking longer than estimated... +$(Format-InstallApiDurationMmSs -TotalSec $overSec)"
    }
}

function Format-InstallApiCountdownStatus {
    param(
        [Parameter(Mandatory = $true)][datetime]$FinishAt,
        [string]$Prefix = "Installing API",
        [datetime]$Now = $(Get-Date)
    )

    $cd = Format-InstallApiCountdownRemaining -FinishAt $FinishAt -Now $Now
    $doneBy = $FinishAt.ToString("HH:mm:ss")
    if ($cd.Overtime) {
        return "$Prefix - $($cd.Text) (estimated done by $doneBy)"
    }
    return "$Prefix - $($cd.Text) remaining (done by $doneBy)"
}

function Save-InstallApiTimingResult {
    param(
        [Parameter(Mandatory = $true)][int]$DurationSec,
        [Parameter(Mandatory = $true)][bool]$Success
    )

    $dir = Split-Path -Parent $script:InstallApiTimingFile
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }

    $payload = [ordered]@{
        lastRunAt           = (Get-Date).ToString("o")
        lastRunDurationSec  = $DurationSec
        lastRunSuccess      = $Success
    }

    if (Test-Path -LiteralPath $script:InstallApiTimingFile) {
        try {
            $existing = Get-Content -LiteralPath $script:InstallApiTimingFile -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($existing.lastSuccessDurationSec) {
                $payload.lastSuccessDurationSec = [int]$existing.lastSuccessDurationSec
            }
            if ($existing.lastSuccessAt) {
                $payload.lastSuccessAt = [string]$existing.lastSuccessAt
            }
        }
        catch {
        }
    }

    if ($Success) {
        $payload.lastSuccessDurationSec = $DurationSec
        $payload.lastSuccessAt = (Get-Date).ToString("o")
    }

    ($payload | ConvertTo-Json) | Set-Content -LiteralPath $script:InstallApiTimingFile -Encoding UTF8
}

function Ensure-InstallApiWinFormsLoaded {
    if ($script:InstallApiProgressWinFormsLoaded) { return $true }
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        Add-Type -AssemblyName System.Drawing -ErrorAction Stop
        $script:InstallApiProgressWinFormsLoaded = $true
        return $true
    }
    catch {
        return $false
    }
}

function Test-InstallApiProgressActive {
    return [bool]$script:InstallApiProgressActive
}

function Test-InstallApiLiveViewportActive {
    return Test-InstallApiProgressActive
}

function Write-InstallApiProgressLogLine {
    param(
        [AllowEmptyString()][string]$Line,
        [switch]$SkipLogFile
    )

    if ($null -eq $Line) { return }
    if (-not $SkipLogFile -and $script:InstallApiProgressLogPath) {
        Add-Content -LiteralPath $script:InstallApiProgressLogPath -Value $Line -Encoding UTF8
    }
}

function Open-InstallApiLogsFolder {
    param([string]$LogPath = $null)

    if (-not (Test-Path -LiteralPath $script:InstallApiLogRoot)) {
        New-Item -ItemType Directory -Force -Path $script:InstallApiLogRoot | Out-Null
    }

    $targetLog = $LogPath
    if (-not $targetLog) {
        $targetLog = $script:InstallApiProgressLogPath
    }

    if ($targetLog -and (Test-Path -LiteralPath $targetLog)) {
        Start-Process explorer.exe -ArgumentList "/select,`"$targetLog`""
    }
    else {
        Start-Process explorer.exe $script:InstallApiLogRoot
    }
}

function Update-InstallApiProgressDisplay {
    if (-not $script:InstallApiProgressActive) { return }

    $finishAt = $script:InstallApiProgressFinishAt
    if ($finishAt) {
        $cd = Format-InstallApiCountdownRemaining -FinishAt $finishAt
        $doneBy = $finishAt.ToString("HH:mm:ss")
        $etaText = if ($cd.Overtime) {
            "$($cd.Text) (done by $doneBy)"
        }
        else {
            "$($cd.Text) remaining (done by $doneBy)"
        }
        if ($script:InstallApiProgressLabels.Eta) {
            $script:InstallApiProgressLabels.Eta.Text = "ETA: $etaText"
        }
    }

    if ($script:InstallApiProgressLabels.Step -and $script:InstallApiProgressStepTotal -gt 0) {
        $script:InstallApiProgressLabels.Step.Text = "Step $($script:InstallApiProgressStepIndex) of $($script:InstallApiProgressStepTotal): $($script:InstallApiProgressStepName)"
    }

    if ($script:InstallApiProgressLabels.SubProgress) {
        if ($script:InstallApiProgressPublishActions -gt 0) {
            $script:InstallApiProgressLabels.SubProgress.Text = "Build actions logged: $($script:InstallApiProgressPublishActions)"
            $script:InstallApiProgressLabels.SubProgress.Visible = $true
        }
        else {
            $script:InstallApiProgressLabels.SubProgress.Visible = $false
        }
    }

    if ($script:InstallApiProgressLabels.Status) {
        $status = $script:InstallApiProgressStatusLine
        if ([string]::IsNullOrWhiteSpace($status)) {
            $status = "Working..."
        }
        if ($status.Length -gt 120) {
            $status = $status.Substring(0, 117) + "..."
        }
        $script:InstallApiProgressLabels.Status.Text = $status
    }

    if ($script:InstallApiProgressForm -and -not $script:InstallApiProgressForm.IsDisposed) {
        try {
            [System.Windows.Forms.Application]::DoEvents()
        }
        catch {
        }
    }

    try {
        $hostUi = $Host.UI
        if ($hostUi -and $hostUi.RawUI -and $finishAt) {
            $prefix = $script:InstallApiProgressWindowTitle
            if (-not $prefix) { $prefix = "Heimdall API install" }
            $hostUi.RawUI.WindowTitle = (Format-InstallApiCountdownStatus -FinishAt $finishAt -Prefix $prefix)
        }
    }
    catch {
    }
}

function Start-InstallApiProgressWindow {
    param(
        [Parameter(Mandatory = $true)][datetime]$FinishAt,
        [Parameter(Mandatory = $true)][int]$TotalSteps,
        [string]$LogPath = $null,
        [string]$InitialStepName = "Starting",
        [string]$WindowTitle = "Heimdall API install"
    )

    Stop-InstallApiProgressWindow
    $script:InstallApiProgressActive = $true
    $script:InstallApiProgressFinishAt = $FinishAt
    $script:InstallApiProgressLogPath = $LogPath
    $script:InstallApiProgressStepIndex = 1
    $script:InstallApiProgressStepTotal = $TotalSteps
    $script:InstallApiProgressStepName = $InitialStepName
    $script:InstallApiProgressStatusLine = "Started"
    $script:InstallApiProgressPublishActions = 0
    $script:InstallApiProgressWindowTitle = if ([string]::IsNullOrWhiteSpace($WindowTitle)) { "Heimdall API install" } else { $WindowTitle }

    if (-not (Ensure-InstallApiWinFormsLoaded)) {
        Write-Host "$($script:InstallApiProgressWindowTitle) progress (WinForms unavailable; see log file)." -ForegroundColor Yellow
        if ($LogPath) {
            Write-Host "  Log: $LogPath" -ForegroundColor DarkGray
        }
        Update-InstallApiProgressDisplay
        return
    }

    $form = New-Object System.Windows.Forms.Form
    $form.Text = $script:InstallApiProgressWindowTitle
    $form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
    $form.ClientSize = New-Object System.Drawing.Size(480, 236)
    $form.TopMost = $true

    $y = 12
    $lblTitle = New-Object System.Windows.Forms.Label
    $lblTitle.Text = $script:InstallApiProgressWindowTitle
    $lblTitle.Font = New-Object System.Drawing.Font("Segoe UI", 11, [System.Drawing.FontStyle]::Bold)
    $lblTitle.AutoSize = $true
    $lblTitle.Location = New-Object System.Drawing.Point(16, $y)
    $form.Controls.Add($lblTitle)

    $y += 32
    $lblEta = New-Object System.Windows.Forms.Label
    $lblEta.AutoSize = $true
    $lblEta.Location = New-Object System.Drawing.Point(16, $y)
    $form.Controls.Add($lblEta)

    $y += 28
    $lblStep = New-Object System.Windows.Forms.Label
    $lblStep.AutoSize = $true
    $lblStep.Font = New-Object System.Drawing.Font("Segoe UI", 9, [System.Drawing.FontStyle]::Bold)
    $lblStep.Location = New-Object System.Drawing.Point(16, $y)
    $form.Controls.Add($lblStep)

    $y += 24
    $lblSub = New-Object System.Windows.Forms.Label
    $lblSub.AutoSize = $true
    $lblSub.ForeColor = [System.Drawing.Color]::DarkSlateGray
    $lblSub.Location = New-Object System.Drawing.Point(16, $y)
    $form.Controls.Add($lblSub)

    $y += 24
    $lblStatus = New-Object System.Windows.Forms.Label
    $lblStatus.AutoSize = $false
    $lblStatus.Size = New-Object System.Drawing.Size(448, 40)
    $lblStatus.Location = New-Object System.Drawing.Point(16, $y)
    $form.Controls.Add($lblStatus)

    $btnLogs = New-Object System.Windows.Forms.Button
    $btnLogs.Text = "Open logs folder"
    $btnLogs.Width = 130
    $btnLogs.Height = 28
    $btnLogs.Location = New-Object System.Drawing.Point(16, 168)
    $capturedLogPath = $LogPath
    $btnLogs.Add_Click({
        Open-InstallApiLogsFolder -LogPath $capturedLogPath
    })
    $form.Controls.Add($btnLogs)

    $script:InstallApiProgressForm = $form
    $script:InstallApiProgressLabels = @{
        Eta         = $lblEta
        Step        = $lblStep
        SubProgress = $lblSub
        Status      = $lblStatus
    }

    Update-InstallApiProgressDisplay
    $form.Show() | Out-Null
    [System.Windows.Forms.Application]::DoEvents()
}

function Set-InstallApiProgressStep {
    param(
        [Parameter(Mandatory = $true)][int]$StepIndex,
        [Parameter(Mandatory = $true)][int]$TotalSteps,
        [Parameter(Mandatory = $true)][string]$StepName,
        [string]$StatusLine = $null
    )

    $script:InstallApiProgressStepIndex = $StepIndex
    $script:InstallApiProgressStepTotal = $TotalSteps
    $script:InstallApiProgressStepName = $StepName
    if ($StatusLine) {
        $script:InstallApiProgressStatusLine = $StatusLine
    }
    else {
        $script:InstallApiProgressStatusLine = $StepName
    }
    Update-InstallApiProgressDisplay
}

function Set-InstallApiProgressStatus {
    param([Parameter(Mandatory = $true)][string]$StatusLine)

    $script:InstallApiProgressStatusLine = $StatusLine
    Update-InstallApiProgressDisplay
}

function Add-InstallApiPublishActionCount {
    param([int]$Increment = 1)

    $script:InstallApiProgressPublishActions += $Increment
    Update-InstallApiProgressDisplay
}

function Stop-InstallApiProgressWindow {
    param([switch]$KeepWindowOpen)

    $script:InstallApiProgressActive = $false
    $script:InstallApiProgressFinishAt = $null

    if ($KeepWindowOpen -and $script:InstallApiProgressForm -and -not $script:InstallApiProgressForm.IsDisposed) {
        if ($script:InstallApiProgressLabels.Eta) {
            $script:InstallApiProgressLabels.Eta.Text = "Finished"
        }
        if ($script:InstallApiProgressLabels.Status) {
            $script:InstallApiProgressLabels.Status.Text = "Use Open logs folder for the log."
        }
        try {
            [System.Windows.Forms.Application]::DoEvents()
        }
        catch {
        }
        return
    }

    $script:InstallApiProgressLogPath = $null
    $script:InstallApiProgressLabels = @{}

    if ($script:InstallApiProgressForm -and -not $script:InstallApiProgressForm.IsDisposed) {
        try {
            $script:InstallApiProgressForm.Close()
            $script:InstallApiProgressForm.Dispose()
        }
        catch {
        }
    }
    $script:InstallApiProgressForm = $null

    try {
        $hostUi = $Host.UI
        if ($hostUi -and $hostUi.RawUI) {
            $title = $script:InstallApiProgressWindowTitle
            if (-not $title) { $title = "Heimdall API install" }
            $hostUi.RawUI.WindowTitle = $title
        }
    }
    catch {
    }
}

function Start-InstallApiConsoleCountdown {
    param(
        [Parameter(Mandatory = $true)][datetime]$FinishAt,
        [Parameter(Mandatory = $true)][int]$EstimatedSec,
        [string]$LogPath = $null,
        [int]$TotalSteps = 9,
        [string]$WindowTitle = "Heimdall API install"
    )

    Start-InstallApiProgressWindow -FinishAt $FinishAt -TotalSteps $TotalSteps -LogPath $LogPath -InitialStepName "Starting" -WindowTitle $WindowTitle
}

function Stop-InstallApiConsoleCountdown {
    param([switch]$KeepWindowOpen)

    Stop-InstallApiProgressWindow -KeepWindowOpen:$KeepWindowOpen
}

function Invoke-DotNetPublishWithProgress {
    param(
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][string]$OutputDir,
        [Parameter(Mandatory = $true)][datetime]$FinishAt,
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    $cmdLine = "dotnet publish `"$Project`" -c Release -o `"$OutputDir`" --self-contained false -v detailed"
    Write-InstallApiProgressLogLine -Line ">>> $cmdLine"
    Set-InstallApiProgressStatus -StatusLine "Publishing API (verbose output in log only)"

    $queueState = @{
        Sync  = New-Object object
        Queue = New-Object System.Collections.Queue
    }

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo.FileName = "dotnet"
    $proc.StartInfo.Arguments = "publish `"$Project`" -c Release -o `"$OutputDir`" --self-contained false -v detailed"
    $proc.StartInfo.RedirectStandardOutput = $true
    $proc.StartInfo.RedirectStandardError = $true
    $proc.StartInfo.UseShellExecute = $false
    $proc.StartInfo.CreateNoWindow = $true
    $proc.StartInfo.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $proc.StartInfo.StandardErrorEncoding = [System.Text.Encoding]::UTF8

    $stdoutSub = Register-ObjectEvent -InputObject $proc -EventName OutputDataReceived -MessageData $queueState -Action {
        $line = $Event.SourceEventArgs.Data
        if ($null -eq $line) { return }
        [void][System.Threading.Monitor]::Enter($Event.MessageData.Sync)
        try {
            [void]$Event.MessageData.Queue.Enqueue($line)
        }
        finally {
            [void][System.Threading.Monitor]::Exit($Event.MessageData.Sync)
        }
    }

    $stderrSub = Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived -MessageData $queueState -Action {
        $line = $Event.SourceEventArgs.Data
        if ($null -eq $line) { return }
        [void][System.Threading.Monitor]::Enter($Event.MessageData.Sync)
        try {
            [void]$Event.MessageData.Queue.Enqueue($line)
        }
        finally {
            [void][System.Threading.Monitor]::Exit($Event.MessageData.Sync)
        }
    }

    $subs = @($stdoutSub, $stderrSub)

    function Drain-PublishQueue {
        param([bool]$UpdateStatus)

        [void][System.Threading.Monitor]::Enter($queueState.Sync)
        try {
            while ($queueState.Queue.Count -gt 0) {
                $ln = [string]$queueState.Queue.Dequeue()
                Write-InstallApiProgressLogLine -Line $ln

                if ($ln -match '^\s*\d+:\d+>Target "([^"]+)"') {
                    Add-InstallApiPublishActionCount -Increment 1
                    if ($UpdateStatus) {
                        Set-InstallApiProgressStatus -StatusLine ("Publishing: " + $Matches[1])
                    }
                }
                elseif ($ln -match '^\s*\d+:\d+>Done building target "([^"]+)"') {
                    Add-InstallApiPublishActionCount -Increment 1
                }
            }
        }
        finally {
            [void][System.Threading.Monitor]::Exit($queueState.Sync)
        }
    }

    try {
        if (-not $proc.Start()) {
            throw "Failed to start dotnet publish process."
        }

        $proc.BeginOutputReadLine()
        $proc.BeginErrorReadLine()

        while (-not $proc.HasExited) {
            Drain-PublishQueue -UpdateStatus $true
            Update-InstallApiProgressDisplay
            Start-Sleep -Milliseconds 400
        }

        $proc.WaitForExit()

        Start-Sleep -Milliseconds 250
        Drain-PublishQueue -UpdateStatus $false
        Update-InstallApiProgressDisplay

        if ($proc.ExitCode -eq 0) {
            Set-InstallApiProgressStatus -StatusLine "Publish finished ($($script:InstallApiProgressPublishActions) build actions logged)"
        }

        return $proc.ExitCode
    }
    finally {
        foreach ($sub in $subs) {
            if ($sub) {
                try {
                    Unregister-Event -SubscriptionId $sub.Id -ErrorAction SilentlyContinue
                    Remove-Job -Id $sub.Id -Force -ErrorAction SilentlyContinue
                }
                catch {
                }
            }
        }
        if ($proc -and -not $proc.HasExited) {
            try { $proc.Kill() } catch { }
        }
        if ($proc) {
            try { $proc.Dispose() } catch { }
        }
    }
}

function Invoke-DotNetPublishWithLiveViewport {
    param(
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][string]$OutputDir,
        [Parameter(Mandatory = $true)][datetime]$FinishAt,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [int]$MaxVisibleLines = 10
    )

    return Invoke-DotNetPublishWithProgress -Project $Project -OutputDir $OutputDir -FinishAt $FinishAt -LogPath $LogPath
}

function Wait-ProcessWithInstallCountdown {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)][datetime]$FinishAt,
        [string]$StatusPrefix = "Installing API"
    )

    if (-not $Process) { return -1 }

    try {
        while ($Process -and -not $Process.HasExited) {
            $status = Format-InstallApiCountdownStatus -FinishAt $FinishAt -Prefix $StatusPrefix
            Set-UiStatus $status
            [System.Windows.Forms.Application]::DoEvents()
            Start-Sleep -Milliseconds 250
        }
    }
    finally {
    }

    $exitCode = -1
    if ($Process) {
        $Process.Refresh()
        $exitCode = $Process.ExitCode
    }

    if ($exitCode -eq 0) {
        Set-UiStatus "API install finished successfully"
    }
    elseif ($exitCode -gt 0) {
        Set-UiStatus "API install failed (exit $exitCode)"
    }
    else {
        Set-UiStatus "API install finished"
    }

    return $exitCode
}
