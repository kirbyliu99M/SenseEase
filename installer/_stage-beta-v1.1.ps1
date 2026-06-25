# Stage one Beta-V1.1 dist variant (separate folder per package). All variants are COMPLETE
# (self-contained app + head-tracker sidecar) and Vulkan-trimmed. Run AFTER the publish (dist\beta-v1.1-base).
#   py   = OV Python host + IR        cpp  = OV C++ host + IR
#   amd  = FLM runtime + Q4NX         auto = OV(Python) host + IR + FLM + Q4NX (runtime auto-select)
#   powershell -File installer\_stage-beta-v1.1.ps1 -Variant py|cpp|amd|auto
param([Parameter(Mandatory)][ValidateSet("py","cpp","amd","auto")][string]$Variant, [string]$Ver = "v1.2")
$ErrorActionPreference = "Stop"
$repo    = "C:\Users\kirby\Desktop\SenseEase APP"
$base    = "$repo\dist\beta-$Ver-base"
$ir      = "C:\Users\kirby\Desktop\SenseEase-Debug-intel-2\models\Qwen3-4B-Instruct-2507-ov-int4"
$pyHost  = "C:\Users\kirby\ovhost-build\dist\ovgenai-host"
$cppHost = "C:\Users\kirby\ovhost-build\ovgenai-host-cpp"
$flmSrc  = "C:\Program Files\flm"
$q4nxSrc = "$repo\models\Qwen3-4B-NPU2"
$scSrc   = "$repo\tools\head-tracker-sidecar\dist\head_tracker"
$dest    = "$repo\dist\beta-$Ver-$Variant"

function RC($src, $dst, $xf) {
  New-Item -ItemType Directory -Path $dst -Force | Out-Null
  $a = @($src, $dst, '/MIR', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/R:2', '/W:1')
  if ($xf) { $a += '/XF'; $a += $xf }
  robocopy @a | Out-Null
  if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE): $src" }
}

if (-not (Test-Path $base)) { throw "publish base not found: $base (run dotnet publish first)" }
Write-Host "=== staging '$Variant' -> $dest ==="
if (Test-Path $dest) { [System.IO.Directory]::Delete($dest, $true) }
New-Item -ItemType Directory -Path $dest -Force | Out-Null

# --- common: self-contained app (minus debug) + head-tracker sidecar ---
RC $base $dest @('*.pdb', '*.xml', '*.log', 'createdump.exe')
if (Test-Path $scSrc) {
  $scDst = "$dest\tools\head-tracker-sidecar\dist\head_tracker"
  RC $scSrc $scDst
  $tk = "$repo\tools\head-tracker-sidecar\face_landmarker.task"
  if (-not (Test-Path $tk)) { $tk = "$repo\face_landmarker.task" }
  if (Test-Path $tk) { Copy-Item $tk (Join-Path $scDst "face_landmarker.task") -Force }
} else { Write-Host "  WARN: sidecar source absent" }

# --- OV backend (py / cpp / auto): host (renamed genai_host.exe for FindExe) + INT4 IR ---
if ($Variant -in @('py', 'cpp', 'auto')) {
  $hostSrc = if ($Variant -eq 'cpp') { $cppHost } else { $pyHost }   # auto uses the field-proven Python host
  $hostDst = "$dest\packs\intel-ov\host"
  RC $hostSrc $hostDst @('*_debug.dll')
  if (Test-Path "$hostDst\ovgenai-host.exe") { Rename-Item "$hostDst\ovgenai-host.exe" "genai_host.exe" -Force }
  if (-not (Test-Path "$hostDst\genai_host.exe")) { throw "OV host exe missing after rename" }
  RC $ir "$dest\packs\intel-ov\model"
}

# --- FLM backend (amd / auto): FLM runtime + Q4NX (renamed to the FLM catalog name) ---
if ($Variant -in @('amd', 'auto')) {
  RC $flmSrc "$dest\packs\amd-flm\flm"
  RC $q4nxSrc "$dest\packs\amd-flm\q4nx\Qwen3-4B-Instruct-2507-NPU2"
}

$totMB = [math]::Round((Get-ChildItem $dest -Recurse -File | Measure-Object Length -Sum).Sum/1MB, 0)
Write-Host ("=== staged '$Variant': $totMB MB ===")
Get-ChildItem $dest -Directory | ForEach-Object {
  $mb = [math]::Round((Get-ChildItem $_.FullName -Recurse -File | Measure-Object Length -Sum).Sum/1MB, 0)
  Write-Host ("    {0,-22} {1} MB" -f $_.Name, $mb)
}
exit 0
