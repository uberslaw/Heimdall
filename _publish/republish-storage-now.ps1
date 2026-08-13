$ErrorActionPreference = "Stop"
$installDir = "$env:ProgramFiles\Heimdall\Api"
$stage = "C:\Heimdall\dist\_publish\Api"
$log = "C:\Heimdall\_publish\republish-storage.log"
function L($m) { Add-Content $log "$(Get-Date -Format o) $m"; Write-Output $m }
Remove-Item $log -ErrorAction SilentlyContinue
try {
  L "Stopping HeimdallApi..."
  Stop-Service HeimdallApi -Force
  Start-Sleep -Seconds 3
  L "Copying from stage (preserve appsettings)..."
  robocopy $stage $installDir /E /XO /NFL /NDL /NJH /NJS /nc /ns /np /XF appsettings.json appsettings.Development.json | Out-Null
  Copy-Item (Join-Path $stage "Heimdall.Api.dll") (Join-Path $installDir "Heimdall.Api.dll") -Force
  Copy-Item (Join-Path $stage "Heimdall.Api.exe") (Join-Path $installDir "Heimdall.Api.exe") -Force
  Copy-Item (Join-Path $stage "Heimdall.Shared.dll") (Join-Path $installDir "Heimdall.Shared.dll") -Force
  L "Starting HeimdallApi..."
  Start-Service HeimdallApi
  Start-Sleep -Seconds 4
  $s = Get-Service HeimdallApi
  $dll = Get-Item (Join-Path $installDir "Heimdall.Api.dll")
  L "OK Status=$($s.Status) DllWrite=$($dll.LastWriteTime)"
  exit 0
} catch {
  L "FAIL $_"
  try { Start-Service HeimdallApi } catch { L "restart also failed: $_" }
  exit 1
}
