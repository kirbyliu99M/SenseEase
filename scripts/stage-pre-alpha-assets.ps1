# =============================================================================
# stage-pre-alpha-assets.ps1
# =============================================================================
# After `dotnet publish -c Release -r win-x64 --self-contained -o <Dist>`, this
# script reshapes the publish output into the Pre-Alpha distributable layout:
#
#   1. Trim cross-platform runtimes (Linux / macOS / win-arm64) -- ~85 MB saved
#   2. Bundle Ministral 3B Q4_K_M GGUF model into models/ -- 2.0 GB
#   3. Bundle setup demo video (samples/FPV-vid-h264.mp4) -- 113 MB
#   4. Bundle head-tracker PyInstaller sidecar (tools/head-tracker-sidecar/dist/head_tracker/) -- ~350 MB
#   5. Drop canonical README (scripts/README.txt) into dist root
#
# The structural problem this solves: SenseEase.App.csproj has NO MSBuild rule
# that copies samples/, tools/, or models/ to publish output. Only the
# runtimes/win-x64/native/<backend>/ glob is auto-staged. Everything else must
# be staged post-publish or it won't ship.
#
# Usage:
#   .\scripts\stage-pre-alpha-assets.ps1                     # all assets
#   .\scripts\stage-pre-alpha-assets.ps1 -SkipSidecar        # before PyInstaller pipeline ready
#   .\scripts\stage-pre-alpha-assets.ps1 -Dist 'dist/foo'    # alt dist target
# =============================================================================

param(
    [string]$Dist = "dist/v0.11-pre-alpha",
    [switch]$SkipSidecar
)

$ErrorActionPreference = "Stop"
$startTime = Get-Date

function Write-Section($title) {
    Write-Host ""
    Write-Host "=== $title ===" -ForegroundColor Cyan
}

# -----------------------------------------------------------------------------
# 0. Sanity checks
# -----------------------------------------------------------------------------
if (-not (Test-Path $Dist)) {
    Write-Error "Dist not found at '$Dist'. Run dotnet publish first:`n  dotnet publish src/SenseEase.App/SenseEase.App.csproj -c Release -r win-x64 --self-contained -o $Dist"
    exit 1
}

$exePath = Join-Path $Dist "SenseEase.App.exe"
if (-not (Test-Path $exePath)) {
    Write-Error "SenseEase.App.exe missing from '$Dist'. Publish may have failed."
    exit 1
}

Write-Host "Staging into: $Dist" -ForegroundColor Green
Write-Host "Pre-stage size: $([math]::Round((Get-ChildItem $Dist -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)) MB"

# -----------------------------------------------------------------------------
# 1. Trim cross-platform runtimes
# -----------------------------------------------------------------------------
Write-Section "Trim cross-platform runtimes (keep only win-x64)"
$trim = @(
    "runtimes/linux-arm64",
    "runtimes/linux-musl-x64",
    "runtimes/linux-x64",
    "runtimes/osx-arm64",
    "runtimes/osx-x64",
    "runtimes/win-arm64"
)
$removed = 0
foreach ($d in $trim) {
    $full = Join-Path $Dist $d
    if (Test-Path $full) {
        Remove-Item $full -Recurse -Force
        Write-Host "  removed: $d"
        $removed++
    }
}
Write-Host "  ($removed trimmed)"

# -----------------------------------------------------------------------------
# 1b. Trim win-x64 native backend variants + diagnostics (H28)
# -----------------------------------------------------------------------------
Write-Section "Trim win-x64 native backend variants (H28)"
# llama.cpp ships per-ISA / GPU backend variant folders; this build runs the
# vulkan path only, so the AVX/AVX2/AVX512/CUDA12/noavx variants are dead weight.
# Each removal is Test-Path-guarded => a path absent on this build is a safe
# no-op, never a hard failure.
$nativeTrim = @(
    "runtimes/win-x64/native/avx",
    "runtimes/win-x64/native/avx2",
    "runtimes/win-x64/native/avx512",
    "runtimes/win-x64/native/cuda12",
    "runtimes/win-x64/native/noavx"
)
$nativeRemoved = 0
foreach ($d in $nativeTrim) {
    $full = Join-Path $Dist $d
    if (Test-Path $full) {
        Remove-Item $full -Recurse -Force
        Write-Host "  removed: $d"
        $nativeRemoved++
    }
}
# createdump.exe: the .NET crash-dump tool, never needed at runtime. Also excluded
# at package time by the .iss [Files] Excludes -- this is defense in depth.
Get-ChildItem -Path $Dist -Recurse -File -Filter "createdump.exe" -ErrorAction SilentlyContinue | ForEach-Object {
    Remove-Item $_.FullName -Force
    Write-Host "  removed: $($_.Name) (in $($_.Directory.Name))"
    $nativeRemoved++
}
Write-Host "  ($nativeRemoved trimmed)"
# NOTE (H28, deferred): the vulkan/ keep-list trim (delete unused llama-*.exe,
# keeping only llama-server.exe + llama-server-impl.dll + ggml*.dll [minus
# ggml-rpc.dll] + llama.dll + libomp140.x86_64.dll) is intentionally NOT done
# here -- a delete-with-exceptions pass is unsafe to author blind, so run it once
# against the real staged native tree to avoid removing a required dependency.

# -----------------------------------------------------------------------------
# 2. Bundle Mistral 3B Q4_K_M GGUF
# -----------------------------------------------------------------------------
Write-Section "Bundle Mistral 3B Q4_K_M GGUF model"
$modelSrc = "models/Ministral-3-3B-Instruct-2512-GGUF/Ministral-3-3B-Instruct-2512-Q4_K_M.gguf"
if (-not (Test-Path $modelSrc)) {
    Write-Error "Model not found at $modelSrc. Cannot stage."
    exit 1
}
$modelDest = Join-Path $Dist "models/Ministral-3-3B-Instruct-2512-GGUF"
New-Item -ItemType Directory -Path $modelDest -Force | Out-Null
Copy-Item $modelSrc $modelDest -Force
$modelSize = [math]::Round((Get-Item (Join-Path $modelDest "Ministral-3-3B-Instruct-2512-Q4_K_M.gguf")).Length / 1MB, 0)
Write-Host "  copied: Q4_K_M.gguf ($modelSize MB)"

# -----------------------------------------------------------------------------
# 3. Bundle setup demo video
# -----------------------------------------------------------------------------
Write-Section "Bundle setup demo video"
$videoSrc = "samples/FPV-vid-h264.mp4"
if (-not (Test-Path $videoSrc)) {
    Write-Warning "Demo video not found at $videoSrc. Calibration & Demo will fall back to synthetic OFI ramp."
} else {
    $samplesDest = Join-Path $Dist "samples"
    New-Item -ItemType Directory -Path $samplesDest -Force | Out-Null
    Copy-Item $videoSrc $samplesDest -Force
    $videoSize = [math]::Round((Get-Item (Join-Path $samplesDest "FPV-vid-h264.mp4")).Length / 1MB, 0)
    Write-Host "  copied: FPV-vid-h264.mp4 ($videoSize MB)"
}
# Explicitly NOT copying:
#   - samples/FPV-vid.mp4 (AV1 master, reference-only, not played at runtime)
#   - samples/demo_event_log.json (dev fixture, zero src/ references)
#   - samples/demo_nlu_response.json (dev fixture, zero src/ references)

# -----------------------------------------------------------------------------
# 4. Bundle head-tracker PyInstaller sidecar
# -----------------------------------------------------------------------------
Write-Section "Bundle head-tracker sidecar"
if ($SkipSidecar) {
    Write-Host "  SKIPPED (use -SkipSidecar:`$false to include)" -ForegroundColor Yellow
    Write-Warning "Head tracking will silently soft-disable on target machines without the sidecar."
} else {
    $sidecarSrc = "tools/head-tracker-sidecar/dist/head_tracker"
    if (-not (Test-Path $sidecarSrc)) {
        Write-Warning "Sidecar PyInstaller output not found at $sidecarSrc"
        Write-Warning "Run pyinstaller against tools/head-tracker-sidecar/head_tracker.spec first."
        Write-Warning "Skipping for now -- head tracking will not work in this build."
    } else {
        # Ensure face_landmarker.task is exe-adjacent BEFORE staging (PyInstaller
        # only puts it in _internal/; the runtime hook checks exe-adjacent first).
        # The model lives at tools/head-tracker-sidecar/face_landmarker.task or
        # at the repo root. Either source works.
        $modelSrc = "tools/head-tracker-sidecar/face_landmarker.task"
        if (-not (Test-Path $modelSrc)) { $modelSrc = "face_landmarker.task" }
        if (Test-Path $modelSrc) {
            Copy-Item $modelSrc (Join-Path $sidecarSrc "face_landmarker.task") -Force
            Write-Host "  ensured: face_landmarker.task exe-adjacent in PyInstaller dist"
        } else {
            Write-Warning "face_landmarker.task not found at expected paths; runtime hook will fall back to _internal/ copy"
        }

        $sidecarDest = Join-Path $Dist "tools/head-tracker-sidecar/dist/head_tracker"
        New-Item -ItemType Directory -Path $sidecarDest -Force | Out-Null
        # robocopy mirrors directory trees efficiently and reports progress sparingly
        # /MIR mirrors source -> dest (dest matches source exactly)
        # /NFL no file list (less verbose)
        # /NDL no directory list
        # /NJH /NJS no job header/summary
        # /NP no progress bytes
        # /R:2 /W:1 minimal retries
        robocopy $sidecarSrc $sidecarDest /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
        $sidecarSize = [math]::Round((Get-ChildItem $sidecarDest -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 0)
        Write-Host "  copied: head_tracker sidecar tree ($sidecarSize MB)"
    }
}

# -----------------------------------------------------------------------------
# 5. Drop canonical README into dist
# -----------------------------------------------------------------------------
Write-Section "Drop canonical README"
$readmeSrc = "scripts/README.txt"
if (-not (Test-Path $readmeSrc)) {
    Write-Warning "Canonical README not found at $readmeSrc. Dist will ship without README.txt."
} else {
    Copy-Item $readmeSrc (Join-Path $Dist "README.txt") -Force
    Write-Host "  copied: README.txt"
}

# -----------------------------------------------------------------------------
# Final summary
# -----------------------------------------------------------------------------
$finalMB = [math]::Round((Get-ChildItem $Dist -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
$elapsed = [math]::Round(((Get-Date) - $startTime).TotalSeconds, 1)
Write-Host ""
Write-Host "=== Staging complete ===" -ForegroundColor Green
Write-Host "  Final dist size: $finalMB MB"
Write-Host "  Elapsed:         $elapsed s"
Write-Host "  Dist path:       $Dist"
