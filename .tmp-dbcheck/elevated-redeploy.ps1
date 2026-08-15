$ErrorActionPreference = 'Stop'
$publish = 'C:\Heimdall\dist\_publish\Api'
$dest = Join-Path $env:ProgramFiles 'Heimdall\Api'
$log = Join-Path $env:ProgramData 'Heimdall\logs\republish-api-elevated.log'
function Log($m) { $line = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $m; Add-Content $log $line; Write-Host $line }

Log 'Stopping HeimdallApi...'
Stop-Service HeimdallApi -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

Log 'Robocopy deploy (exclude appsettings)...'
& robocopy $publish $dest /E /XF appsettings.json appsettings.*.json /NFL /NDL /NJH /NJS /nc /ns /np
if ($LASTEXITCODE -ge 8) { throw "robocopy failed: $LASTEXITCODE" }

Log 'Starting HeimdallApi...'
Start-Service HeimdallApi
Start-Sleep -Seconds 5
$svc = Get-Service HeimdallApi
Log "Service status: $($svc.Status)"
if ($svc.Status -ne 'Running') { throw "HeimdallApi not Running" }

try {
    $r = Invoke-WebRequest -Uri 'http://127.0.0.1:5080/api/health' -UseBasicParsing -TimeoutSec 15
    Log "Health: $($r.StatusCode) $($r.Content.Substring(0, [Math]::Min(200, $r.Content.Length)))"
} catch {
    Log "Health check failed: $($_.Exception.Message)"
    # try common ports / config
    try {
        $cfg = Get-Content (Join-Path $dest 'appsettings.json') -Raw | ConvertFrom-Json
        Log "appsettings urls hint present"
    } catch {}
    throw
}
Log 'DONE OK'
exit 0
