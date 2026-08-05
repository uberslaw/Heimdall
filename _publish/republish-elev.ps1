$ErrorActionPreference = "Stop"
$installDir = "$env:ProgramFiles\Heimdall\Api"
$stage = "C:\Heimdall\_publish\Api"
$log = "C:\Heimdall\_publish\republish.log"
function L($m) { Add-Content $log "$(Get-Date -Format o) $m" }
Remove-Item $log -ErrorAction SilentlyContinue
try {
  L "Stopping..."
  Stop-Service HeimdallApi -Force
  Start-Sleep -Seconds 2
  L "Copying from stage (excluding appsettings — preserve Urls + ProgramData DB paths)..."
  robocopy $stage $installDir /E /XO /NFL /NDL /NJH /NJS /nc /ns /np /XF appsettings.json appsettings.Development.json | Out-Null
  # Force overwrite key binaries
  Copy-Item (Join-Path $stage "Heimdall.Api.dll") (Join-Path $installDir "Heimdall.Api.dll") -Force
  Copy-Item (Join-Path $stage "Heimdall.Api.exe") (Join-Path $installDir "Heimdall.Api.exe") -Force
  Copy-Item (Join-Path $stage "Heimdall.Shared.dll") (Join-Path $installDir "Heimdall.Shared.dll") -Force
  L "Starting..."
  Start-Service HeimdallApi
  Start-Sleep -Seconds 3
  $s = Get-Service HeimdallApi
  $dll = Get-Item (Join-Path $installDir "Heimdall.Api.dll")
  L "OK Status=$($s.Status) DllWrite=$($dll.LastWriteTime)"
  exit 0
} catch {
  L "FAIL $_"
  try { Start-Service HeimdallApi } catch { L "restart also failed: $_" }
  exit 1
}
