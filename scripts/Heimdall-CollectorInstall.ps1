#Requires -Version 5.1
<#
.SYNOPSIS
  Shared collector install launch, default ApiUrl, and version compare bootstrap.

.DESCRIPTION
  Dot-source from Install-Client.ps1 and Heimdall-LaunchControl.ps1.
  ASCII-only; PS 5.1; UTF-8 BOM.
#>

$script:HeimdallDefaultCollectorApiUrl = "http://BNELT5CG5152D8R:5080"

function Resolve-HeimdallFilesystemPath {
    param([Parameter(Mandatory)][string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $Path }
    if ($Path -match '^[^:]+::(.+)$') {
        return $Matches[1]
    }
    try {
        if (Test-Path -LiteralPath $Path) {
            return (Resolve-Path -LiteralPath $Path).ProviderPath
        }
    }
    catch { }
    return $Path
}

function Import-HeimdallVersionCompare {
    param([Parameter(Mandatory)][string]$ScriptDir)
    if (Get-Command Test-HeimdallProductVersionMatch -ErrorAction SilentlyContinue) {
        return
    }
    $helperPath = Join-Path $ScriptDir "Heimdall-VersionCompare.ps1"
    if (Test-Path -LiteralPath $helperPath) {
        . $helperPath
        foreach ($fn in @(
                'Get-HeimdallCoreProductVersion',
                'Get-HeimdallSimpleClientVersion',
                'Test-HeimdallProductVersionMatch',
                'Format-HeimdallVersionCompareLine'
            )) {
            $cmd = Get-Command $fn -ErrorAction SilentlyContinue
            if ($cmd) {
                New-Item -Path "Function:\script:$fn" -Value $cmd.ScriptBlock -Force | Out-Null
            }
        }
    }
    if (-not (Get-Command Test-HeimdallProductVersionMatch -ErrorAction SilentlyContinue)) {
        Write-HeimdallVersionCompareInlineFallback
    }
}

function Write-HeimdallVersionCompareInlineFallback {
    if (Get-Command Test-HeimdallProductVersionMatch -ErrorAction SilentlyContinue) {
        return
    }
    function Get-HeimdallCoreProductVersion {
        param([string]$Version)
        if ([string]::IsNullOrWhiteSpace($Version)) { return "" }
        $v = $Version.Trim()
        $plusIdx = $v.IndexOf('+')
        if ($plusIdx -ge 0) {
            $v = $v.Substring(0, $plusIdx)
        }
        return $v.Trim()
    }
    function Get-HeimdallSimpleClientVersion {
        param([string]$Version)
        $core = Get-HeimdallCoreProductVersion -Version $Version
        if ([string]::IsNullOrWhiteSpace($core)) { return $null }
        $n = 0
        if ([int]::TryParse($core, [ref]$n) -and $n -ge 0 -and ($core -match '^\d+$')) {
            return $n
        }
        return 1
    }
    function Test-HeimdallProductVersionMatch {
        param(
            [string]$VersionA,
            [string]$VersionB
        )
        $a = Get-HeimdallSimpleClientVersion -Version $VersionA
        $b = Get-HeimdallSimpleClientVersion -Version $VersionB
        if ($null -eq $a -or $null -eq $b) {
            return $true
        }
        return ($a -eq $b)
    }
    function Format-HeimdallVersionCompareLine {
        param(
            [string]$LabelA,
            [string]$VersionA,
            [string]$LabelB,
            [string]$VersionB
        )
        $coreA = Get-HeimdallCoreProductVersion -Version $VersionA
        $coreB = Get-HeimdallCoreProductVersion -Version $VersionB
        $simpleA = Get-HeimdallSimpleClientVersion -Version $VersionA
        $simpleB = Get-HeimdallSimpleClientVersion -Version $VersionB
        $match = Test-HeimdallProductVersionMatch -VersionA $VersionA -VersionB $VersionB
        if ($match) {
            if ($VersionA -ne $VersionB) {
                return "$LabelA=$VersionA | $LabelB=$VersionB | simple=$simpleA (match; legacy SemVer maps to 1)"
            }
            return "$LabelA=$VersionA | $LabelB=$VersionB | match"
        }
        return "$LabelA=$VersionA (core=$coreA simple=$simpleA) | $LabelB=$VersionB (core=$coreB simple=$simpleB) | MISMATCH"
    }
    foreach ($fn in @(
            'Get-HeimdallCoreProductVersion',
            'Get-HeimdallSimpleClientVersion',
            'Test-HeimdallProductVersionMatch',
            'Format-HeimdallVersionCompareLine'
        )) {
        $cmd = Get-Command $fn -ErrorAction SilentlyContinue
        if ($cmd) {
            New-Item -Path "Function:\script:$fn" -Value $cmd.ScriptBlock -Force | Out-Null
        }
    }
}

function Resolve-HeimdallDefaultCollectorApiUrl {
    param(
        [string]$LastInstallSettingsFile = $null,
        [scriptblock]$Log = $null
    )
    if ($LastInstallSettingsFile -and (Test-Path -LiteralPath $LastInstallSettingsFile)) {
        try {
            $raw = Get-Content -Raw -Path $LastInstallSettingsFile -Encoding UTF8
            if (-not [string]::IsNullOrWhiteSpace($raw)) {
                $last = $raw | ConvertFrom-Json
                if ($last -and $last.apiUrl) {
                    $u = [string]$last.apiUrl
                    $u = $u.Trim().TrimEnd('/')
                    if ($u) {
                        if ($Log) { & $Log "Prefill ApiUrl from last install: $u" "INFO" }
                        return $u
                    }
                }
            }
        }
        catch {
            if ($Log) { & $Log "Could not read last install settings: $($_.Exception.Message)" "WARN" }
        }
    }
    if ($Log) { & $Log "Prefill ApiUrl from default host: $($script:HeimdallDefaultCollectorApiUrl)" "INFO" }
    return $script:HeimdallDefaultCollectorApiUrl
}

function Invoke-HeimdallElevatedCollectorInstall {
    param(
        [Parameter(Mandatory)][string]$InstallerCmdPath,
        [Parameter(Mandatory)][string]$ApiUrl,
        [Parameter(Mandatory)][string]$ApiKey,
        [Parameter(Mandatory)][string]$MachineGroup,
        [Parameter(Mandatory)][string]$PayloadPath,
        [switch]$AlreadyElevated,
        [scriptblock]$PumpUi = $null,
        [scriptblock]$Log = $null
    )

    $installer = Resolve-HeimdallFilesystemPath -Path $InstallerCmdPath
    $payload = Resolve-HeimdallFilesystemPath -Path $PayloadPath
    $packDir = Resolve-HeimdallFilesystemPath -Path (Split-Path -Parent $InstallerCmdPath)

    $tempCmd = Join-Path $env:TEMP ("heimdall-install-" + (Get-Date -Format "yyyyMMdd-HHmmss") + "-" + ([guid]::NewGuid().ToString("N")).Substring(0, 6) + ".cmd")
    $captureLog = Join-Path $env:TEMP ("heimdall-install-capture-" + (Get-Date -Format "yyyyMMdd-HHmmss") + "-" + ([guid]::NewGuid().ToString("N")).Substring(0, 6) + ".log")

    $batchLines = @(
        '@echo off'
        'setlocal EnableExtensions EnableDelayedExpansion'
        ('cd /d "{0}"' -f $packDir)
        'set HEIMDALL_SKIP_LAUNCH=1'
        'set HEIMDALL_NOPAUSE=1'
        ('call "{0}" -ApiUrl "{1}" -ApiKey "{2}" -MachineGroup "{3}" -Payload "{4}" > "{5}" 2>&1' -f $installer, $ApiUrl, $ApiKey, $MachineGroup, $payload, $captureLog)
        'set EC=!ERRORLEVEL!'
        'exit /b !EC!'
    )
    $batchContent = ($batchLines -join "`r`n") + "`r`n"
    [System.IO.File]::WriteAllText($tempCmd, $batchContent, [System.Text.Encoding]::ASCII)
    if ($Log) { & $Log "Elevated install wrapper: $tempCmd" "INFO" }

    try {
        $startParams = @{
            FilePath         = $tempCmd
            WorkingDirectory = $packDir
            PassThru         = $true
        }
        if (-not $AlreadyElevated) {
            $startParams['Verb'] = 'RunAs'
        }
        else {
            $startParams['WindowStyle'] = 'Hidden'
        }
        if ($Log) {
            $elevNote = if ($AlreadyElevated) { "already elevated (console captured to log)" } else { "RunAs UAC" }
            & $Log "Starting installer via temp wrapper ($elevNote)" "INFO"
            & $Log "Service install capture: $captureLog" "INFO"
        }
        $proc = Start-Process @startParams
        while ($proc -and -not $proc.HasExited) {
            if ($PumpUi) {
                & $PumpUi
            }
            else {
                try {
                    [System.Windows.Forms.Application]::DoEvents()
                }
                catch { }
            }
            Start-Sleep -Milliseconds 150
        }
        $exitCode = -1
        if ($proc) {
            $proc.Refresh()
            $exitCode = [int]$proc.ExitCode
        }
        if (Test-Path -LiteralPath $captureLog) {
            try {
                $captureLines = @(Get-Content -LiteralPath $captureLog -ErrorAction Stop)
                if ($Log -and $captureLines.Count -gt 0) {
                    $capLevel = if ($exitCode -eq 0) { "INFO" } else { "ERROR" }
                    & $Log "Service install console output:" $capLevel
                    foreach ($line in $captureLines) {
                        & $Log "  install> $line" $capLevel
                    }
                }
            }
            catch {
                if ($Log) { & $Log "Could not read service install capture: $($_.Exception.Message)" "WARN" }
            }
        }
        elseif ($Log -and $exitCode -ne 0) {
            & $Log "Service install produced no console capture at $captureLog" "WARN"
        }
        return $exitCode
    }
    finally {
        Remove-Item -LiteralPath $tempCmd -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $captureLog -Force -ErrorAction SilentlyContinue
    }
}

function Get-HeimdallInstallAgentLogTail {
    param(
        [int]$LineCount = 30,
        [string]$LogRoot = (Join-Path $env:ProgramData "Heimdall\logs")
    )
    if (-not (Test-Path -LiteralPath $LogRoot)) {
        return $null
    }
    $logFiles = @(
        Get-ChildItem -LiteralPath $LogRoot -Filter "install-agent-*.log" -ErrorAction SilentlyContinue
        Get-ChildItem -LiteralPath $LogRoot -Filter "install-workstation-collector-*.log" -ErrorAction SilentlyContinue
    )
    $latest = $logFiles |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $latest) {
        return $null
    }
    try {
        $all = @(Get-Content -LiteralPath $latest.FullName -ErrorAction Stop)
        if ($all.Count -le $LineCount) {
            $tail = $all
        }
        else {
            $tail = $all[($all.Count - $LineCount)..($all.Count - 1)]
        }
        return [pscustomobject]@{
            Path  = $latest.FullName
            Lines = $tail
            Text  = ($tail -join [Environment]::NewLine)
        }
    }
    catch {
        return [pscustomobject]@{
            Path  = $latest.FullName
            Lines = @("Could not read log: $($_.Exception.Message)")
            Text  = "Could not read log: $($_.Exception.Message)"
        }
    }
}

function Get-HeimdallInstallWorkstationCollectorLogTail {
    param(
        [int]$LineCount = 30,
        [string]$LogRoot = (Join-Path $env:ProgramData "Heimdall\logs")
    )
    return Get-HeimdallInstallAgentLogTail -LineCount $LineCount -LogRoot $LogRoot
}

function Resolve-HeimdallProductVersionExpected {
    param(
        [Parameter(Mandatory)][string]$ScriptDir,
        [string]$RepoRoot = $null,
        # Legacy integer when no VERSION.json (pre-integer packs map to 1 in VersionCompare).
        [string]$Fallback = "1"
    )
    $candidates = @(
        (Join-Path $ScriptDir "VERSION.json")
    )
    if (-not [string]::IsNullOrWhiteSpace($RepoRoot)) {
        $candidates += (Join-Path $RepoRoot "dist\Heimdall-Client\VERSION.json")
        $candidates += (Join-Path $RepoRoot "dist\workstation-collector\VERSION.json")
    }
    foreach ($c in $candidates) {
        if (-not (Test-Path -LiteralPath $c)) { continue }
        try {
            $obj = Get-Content -Raw -LiteralPath $c | ConvertFrom-Json
            $pv = [string]$obj.productVersion
            if (-not [string]::IsNullOrWhiteSpace($pv)) {
                return $pv.Trim()
            }
        }
        catch { }
    }
    return $Fallback
}

function Test-HeimdallProductVersionAccept {
    param(
        [string]$LocalVersion,
        # Client pack baseline (ProductVersionExpected from local VERSION.json).
        # Do NOT pass API /api/health productVersion — that is API SemVer and is independent.
        [string]$ExpectedClientVersion = $null,
        [scriptblock]$Log = $null,
        [scriptblock]$ConfirmMismatch = $null
    )
    $localPv = [string]$LocalVersion
    $expectedPv = [string]$ExpectedClientVersion

    # Guard: never treat API SemVer (e.g. 0.1.0+hash) as a client pack baseline.
    if (-not [string]::IsNullOrWhiteSpace($expectedPv)) {
        $coreExpected = Get-HeimdallCoreProductVersion -Version $expectedPv
        if ($coreExpected -notmatch '^\d+$') {
            if ($Log) {
                & $Log "Ignoring non-integer ExpectedClientVersion '$expectedPv' (API /api/health SemVer is reachability-only; not compared to client pack)." "WARN"
            }
            return $true
        }
    }

    $verLine = Format-HeimdallVersionCompareLine -LabelA "Pack" -VersionA $localPv -LabelB "Expected" -VersionB $expectedPv
    if ($Log) { & $Log "Version: $verLine" "INFO" }

    if (Test-HeimdallProductVersionMatch -VersionA $localPv -VersionB $expectedPv) {
        $simple = Get-HeimdallSimpleClientVersion -Version $localPv
        if ($localPv -ne $expectedPv) {
            if ($Log) { & $Log "Client version match (simple=$simple; legacy SemVer maps to 1)" "INFO" }
        }
        else {
            if ($Log) { & $Log "Client pack version matches expected ($localPv)." "OK" }
        }
        return $true
    }

    $simpleA = Get-HeimdallSimpleClientVersion -Version $localPv
    $simpleB = Get-HeimdallSimpleClientVersion -Version $expectedPv
    if ($Log) { & $Log "Client version MISMATCH: pack=$localPv (simple=$simpleA) expected=$expectedPv (simple=$simpleB)" "WARN" }
    if ($ConfirmMismatch) {
        return [bool](& $ConfirmMismatch $localPv $expectedPv)
    }
    return $false
}
