#Requires -Version 5.1
<#
.SYNOPSIS
  Shared collector install launch, default ApiUrl, and version compare bootstrap.

.DESCRIPTION
  Dot-source from Install-Client.ps1 and Heimdall-LaunchControl.ps1.
  ASCII-only; PS 5.1; UTF-8 BOM.
#>

$script:HeimdallDefaultCollectorApiUrl = "http://BNELT5CG5152D8R:5080"

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
    function Test-HeimdallProductVersionMatch {
        param(
            [string]$VersionA,
            [string]$VersionB
        )
        $a = Get-HeimdallCoreProductVersion -Version $VersionA
        $b = Get-HeimdallCoreProductVersion -Version $VersionB
        if ([string]::IsNullOrWhiteSpace($a) -or [string]::IsNullOrWhiteSpace($b)) {
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
        $match = Test-HeimdallProductVersionMatch -VersionA $VersionA -VersionB $VersionB
        if ($match) {
            if ($VersionA -ne $VersionB) {
                return "$LabelA=$VersionA | $LabelB=$VersionB | core=$coreA (match; build metadata ignored)"
            }
            return "$LabelA=$VersionA | $LabelB=$VersionB | match"
        }
        return "$LabelA=$VersionA (core=$coreA) | $LabelB=$VersionB (core=$coreB) | MISMATCH"
    }
    foreach ($fn in @(
            'Get-HeimdallCoreProductVersion',
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

    $installer = (Resolve-Path -LiteralPath $InstallerCmdPath).Path
    $payload = (Resolve-Path -LiteralPath $PayloadPath).Path
    $packDir = Split-Path -Parent $installer

    $tempCmd = Join-Path $env:TEMP ("heimdall-install-" + (Get-Date -Format "yyyyMMdd-HHmmss") + "-" + ([guid]::NewGuid().ToString("N")).Substring(0, 6) + ".cmd")

    $batchLines = @(
        '@echo off'
        'setlocal EnableExtensions'
        ('cd /d "{0}"' -f $packDir)
        'set HEIMDALL_SKIP_LAUNCH=1'
        'set HEIMDALL_NOPAUSE=1'
        ('call "{0}" -ApiUrl "{1}" -ApiKey "{2}" -MachineGroup "{3}" -Payload "{4}"' -f $installer, $ApiUrl, $ApiKey, $MachineGroup, $payload)
        'exit /b %ERRORLEVEL%'
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
        if ($Log) {
            $elevNote = if ($AlreadyElevated) { "already elevated" } else { "RunAs UAC" }
            & $Log "Starting installer via temp wrapper ($elevNote)" "INFO"
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
        if ($proc) {
            $proc.Refresh()
            return [int]$proc.ExitCode
        }
        return -1
    }
    finally {
        Remove-Item -LiteralPath $tempCmd -Force -ErrorAction SilentlyContinue
    }
}

function Get-HeimdallInstallWorkstationCollectorLogTail {
    param(
        [int]$LineCount = 30,
        [string]$LogRoot = (Join-Path $env:ProgramData "Heimdall\logs")
    )
    if (-not (Test-Path -LiteralPath $LogRoot)) {
        return $null
    }
    $latest = Get-ChildItem -LiteralPath $LogRoot -Filter "install-workstation-collector-*.log" -ErrorAction SilentlyContinue |
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

function Test-HeimdallProductVersionAccept {
    param(
        [string]$LocalVersion,
        [string]$ServerVersion,
        [scriptblock]$Log = $null,
        [scriptblock]$ConfirmMismatch = $null
    )
    $localPv = [string]$LocalVersion
    $serverPv = [string]$ServerVersion
    $verLine = Format-HeimdallVersionCompareLine -LabelA "Pack" -VersionA $localPv -LabelB "Server" -VersionB $serverPv
    if ($Log) { & $Log "Version: $verLine" "INFO" }

    if (Test-HeimdallProductVersionMatch -VersionA $localPv -VersionB $serverPv) {
        $core = Get-HeimdallCoreProductVersion -Version $localPv
        if ($localPv -ne $serverPv) {
            if ($Log) { & $Log "Version match (core $core; server build metadata ignored)" "INFO" }
        }
        else {
            if ($Log) { & $Log "Product versions match (core SemVer)." "OK" }
        }
        return $true
    }

    $coreA = Get-HeimdallCoreProductVersion -Version $localPv
    $coreB = Get-HeimdallCoreProductVersion -Version $serverPv
    if ($Log) { & $Log "Product version MISMATCH: pack core=$coreA server core=$coreB" "WARN" }
    if ($ConfirmMismatch) {
        return [bool](& $ConfirmMismatch $localPv $serverPv)
    }
    return $false
}
