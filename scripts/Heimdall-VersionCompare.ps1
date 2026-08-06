#Requires -Version 5.1
<#
.SYNOPSIS
  Normalize Heimdall client productVersion strings for comparison.

.DESCRIPTION
  Client versions are simple integers from pack VERSION.json (auto-bumped).
  Legacy SemVer InformationalVersion strings (e.g. 0.1.0, 0.1.0+549a17b6...)
  map to 1. Pure integer strings parse as that int. Keep in sync with
  Heimdall.Api.Services.VersionCompare.

  Use these helpers for client-vs-client comparisons only (pack vs expected /
  published client version, agent vs published). Do not compare client pack
  versions to API /api/health productVersion — that is independent API SemVer.

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

function Get-HeimdallSimpleClientVersion {
    param([string]$Version)
    $core = Get-HeimdallCoreProductVersion -Version $Version
    if ([string]::IsNullOrWhiteSpace($core)) { return $null }
    $n = 0
    if ([int]::TryParse($core, [ref]$n) -and $n -ge 0 -and ($core -match '^\d+$')) {
        return $n
    }
    # Legacy SemVer / non-integer → 1
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
