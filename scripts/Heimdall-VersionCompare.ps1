#Requires -Version 5.1
<#
.SYNOPSIS
  Normalize Heimdall productVersion strings for comparison.

.DESCRIPTION
  Strips InformationalVersion build metadata after '+' so
  0.1.0 matches 0.1.0+549a17b65863a06e8c3098fcd35a386e8e82cd67.
  ASCII-only; dot-source from Install-Client.ps1 and Launch Control.
#>

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
