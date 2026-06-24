# Stage one Beta-V1.1 Intel-OV dist variant: the self-contained app + the OV host
# (renamed genai_host.exe for OvGenAiHostLauncher.FindExe) + the INT4 IR. No FLM /
# Vulkan / GGUF / sidecar / manifest (lean OV-host A/B test package). Run AFTER the publish.
#   powershell -File installer\_stage-beta-v1.1.ps1 -Variant py    (or cpp)
param([Parameter(Mandatory)][ValidateSet("py","cpp")][string]$Variant)
$ErrorActionPreference = "Stop"
$repo = "C:\Users\kirby\Desktop\SenseEase APP"
$base = "$repo\dist\beta-v1.1-base"
$ir   = "C:\Users\kirby\Desktop\SenseEase-Debug-intel-2\models\Qwen3-4B-Instruct-2507-ov-int4"
$hostSrc = if ($Variant -eq "py") { "C:\Users\kirby\ovhost-build\dist\ovgenai-host" } else { "C:\Users\kirby\ovhost-build\ovgenai-host-cpp" }
$dest = "$repo\dist\beta-v1.1-$Variant"

if (-not (Test-Path $base))    { throw "publish base not found: $base (run dotnet publish first)" }
if (-not (Test-Path $hostSrc)) { throw "host src not found: $hostSrc" }
if (-not (Test-Path $ir))      { throw "IR not found: $ir" }

Write-Host "=== staging '$Variant' -> $dest ==="
if (Test-Path $dest) { [System.IO.Directory]::Delete($dest, $true) }
New-Item -ItemType Directory -Path $dest -Force | Out-Null

# 1. app (self-contained publish) minus debug symbols
robocopy $base $dest /MIR /XF *.pdb *.xml *.log createdump.exe /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
if ($LASTEXITCODE -ge 8) { throw "app robocopy failed ($LASTEXITCODE)" }

# 1b. Trim Vulkan: the publish bundles it via the csproj content rule, but Beta V1.1 is OV-only.
# Keep the CODE connector (LocalServerKind.Vulkan stays in the app); just drop the ~100 MB of binaries.
$vk = "$dest\runtimes\win-x64\native\vulkan"
if (Test-Path $vk) { [System.IO.Directory]::Delete($vk, $true) }

# 2. OV host -> packs\intel-ov\host ; rename the exe to genai_host.exe (FindExe probes that name)
$hostDst = "$dest\packs\intel-ov\host"
New-Item -ItemType Directory -Path $hostDst -Force | Out-Null
robocopy $hostSrc $hostDst /MIR /XF *_debug.dll /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
if ($LASTEXITCODE -ge 8) { throw "host robocopy failed ($LASTEXITCODE)" }
if (Test-Path "$hostDst\ovgenai-host.exe") { Rename-Item "$hostDst\ovgenai-host.exe" "genai_host.exe" -Force }
if (-not (Test-Path "$hostDst\genai_host.exe")) { throw "host exe not at $hostDst\genai_host.exe after rename" }

# 3. INT4 IR -> packs\intel-ov\model
$modelDst = "$dest\packs\intel-ov\model"
New-Item -ItemType Directory -Path $modelDst -Force | Out-Null
robocopy $ir $modelDst /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
if ($LASTEXITCODE -ge 8) { throw "IR robocopy failed ($LASTEXITCODE)" }

$appMB   = [math]::Round((Get-ChildItem $dest -File | Measure-Object Length -Sum).Sum/1MB,0)
$hostMB  = [math]::Round((Get-ChildItem $hostDst -Recurse -File | Measure-Object Length -Sum).Sum/1MB,0)
$modelMB = [math]::Round((Get-ChildItem $modelDst -Recurse -File | Measure-Object Length -Sum).Sum/1MB,0)
$totMB   = [math]::Round((Get-ChildItem $dest -Recurse -File | Measure-Object Length -Sum).Sum/1MB,0)
Write-Host ("=== staged '{0}': total {1} MB  (app-root {2} MB, host {3} MB, IR {4} MB) ===" -f $Variant,$totMB,$appMB,$hostMB,$modelMB)
Write-Host ("    host exe: " + (Test-Path "$hostDst\genai_host.exe") + " | IR xml/bin: " + ((Get-ChildItem $modelDst -File -Include *.xml,*.bin -Recurse | Measure-Object).Count) + " files")
