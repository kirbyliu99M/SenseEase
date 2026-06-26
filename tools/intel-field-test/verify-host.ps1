# Verify the GenAI host: wait for /health=ready, then send one NLU request.
# Usage:  powershell -ExecutionPolicy Bypass -File verify-host.ps1 [-Port 8181]
param([int]$Port = 8181)
$ErrorActionPreference = 'Stop'
$base = "http://127.0.0.1:$Port"

Write-Host "Waiting for $base/health = ready  (a cold NPU compile can take ~4 min the first time)..."
$deadline = (Get-Date).AddMinutes(6)
$ready = $false
while ((Get-Date) -lt $deadline) {
  try {
    $h = Invoke-RestMethod "$base/health" -TimeoutSec 4
    Write-Host ("  status={0}  device={1}" -f $h.status, $h.device)
    if ($h.status -eq 'ready') { $ready = $true; break }
  } catch { Write-Host "  (host not answering yet)" }
  Start-Sleep -Seconds 3
}
if (-not $ready) { Write-Host "FAIL: host never reached ready." -ForegroundColor Red; exit 1 }

Write-Host "`n/v1/models:"; (Invoke-RestMethod "$base/v1/models").data | Format-Table id, owned_by -AutoSize

$body = @{
  model       = 'qwen3-4b'
  messages    = @(
    @{ role = 'system'; content = 'You are SenseEase. Reply with one short calm sentence.' },
    @{ role = 'user';   content = 'The screen feels too bright and a bit shaky.' }
  )
  max_tokens  = 64
  temperature = 0
} | ConvertTo-Json -Depth 5

Write-Host "Sending a test NLU request (first turn on NPU can take ~15-30s)..."
$r = Invoke-RestMethod "$base/v1/chat/completions" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 180
$reply = $r.choices[0].message.content
Write-Host ("reply: {0}" -f $reply)
if ([string]::IsNullOrWhiteSpace($reply)) { Write-Host "FAIL: empty reply." -ForegroundColor Red; exit 1 }
Write-Host "PASS - host is serving real replies." -ForegroundColor Green
