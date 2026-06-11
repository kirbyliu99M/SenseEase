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

# -----------------------------------------------------------------------------
# 1c. Trim vulkan/ to the server keep-list (H28)
# -----------------------------------------------------------------------------
Write-Section "Trim vulkan/ to server keep-list (H28)"
# The chatbot launches ONLY llama-server.exe (see MistralChatbotService); the
# other llama-*.exe CLIs (llama-cli, llama-bench, llama-quantize, ...) and the
# RPC backend (ggml-rpc.dll) are never invoked at runtime and are pure bloat.
# Keep-list = everything llama-server.exe needs to load:
#   - llama-server.exe          the server binary we spawn
#   - *.dll                     all backend / framework DLLs EXCEPT ggml-rpc.dll
#                               (llama.dll, ggml*.dll [vulkan/base/cpu], the MSVC
#                               libomp / vcruntime redists, etc.)
# Everything else in vulkan/ (the extra llama-*.exe tools + ggml-rpc.dll) is
# deleted. Forward-additive: any *.dll not on the explicit drop-list survives, so
# a future backend DLL is kept by default rather than silently dropped.
# Idempotent + guarded: the whole pass is a no-op when vulkan/ is absent, and a
# second run finds nothing left to remove.
$vulkanDir = Join-Path $Dist "runtimes/win-x64/native/vulkan"
$vulkanRemoved = 0
if (Test-Path $vulkanDir) {
    # Drop every .exe that is not llama-server.exe (llama-cli, llama-bench,
    # llama-quantize, llama-tokenize, ...).
    Get-ChildItem -Path $vulkanDir -File -Filter "*.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne "llama-server.exe" } |
        ForEach-Object {
            Remove-Item $_.FullName -Force
            Write-Host "  removed: vulkan/$($_.Name)"
            $vulkanRemoved++
        }
    # Drop the RPC backend DLL -- only used for distributed/remote ggml, never
    # in a single-process local server.
    $rpcDll = Join-Path $vulkanDir "ggml-rpc.dll"
    if (Test-Path $rpcDll) {
        Remove-Item $rpcDll -Force
        Write-Host "  removed: vulkan/ggml-rpc.dll"
        $vulkanRemoved++
    }
} else {
    Write-Host "  (vulkan/ not present -- skipping)"
}
Write-Host "  ($vulkanRemoved trimmed)"

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
# 3. (REMOVED 2026-06-08) Demo-video bundling
# -----------------------------------------------------------------------------
# The vection-clip demo and its WPF MediaElement player were excised in the
# explicit-calibration dead-code sweep — nothing plays a video at runtime, so
# samples/ is no longer staged into the dist. (The repo's samples/ holds only
# dev-fixture JSON with zero src/ references.)

# PKG-08: defensive removal of an empty (or stale) samples/ directory from the
# staged dist. dotnet publish may emit an empty samples/ placeholder on some SDK
# versions; the installer's [InstallDelete] covers it on the target machine, but
# removing it here keeps the dist tree clean and shaves the install size.
Write-Section "Remove stale/empty samples/ from dist (PKG-08)"
$samplesDir = Join-Path $Dist "samples"
if (Test-Path $samplesDir) {
    $sampleFiles = Get-ChildItem -Path $samplesDir -Recurse -File -ErrorAction SilentlyContinue
    if ($null -eq $sampleFiles -or @($sampleFiles).Count -eq 0) {
        Remove-Item $samplesDir -Recurse -Force
        Write-Host "  removed: samples/ (was empty)"
    } else {
        Write-Warning "  samples/ contains $(@($sampleFiles).Count) file(s) — not removing (review contents)"
    }
} else {
    Write-Host "  (samples/ not present — nothing to do)"
}

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
# 5b. Emit installer .sha256 sidecar (PKG-03)
# -----------------------------------------------------------------------------
# Run AFTER the installer is compiled (ISCC produces it in installer/).
# This step is a build-time reminder + convenience wrapper; call it manually
# once ISCC finishes:
#
#   $exe = "installer\SenseEase_Setup-Alpha_Test.0609.exe"   # bump each release
#   $hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
#   "$hash *$(Split-Path $exe -Leaf)" | Set-Content "$exe.sha256" -Encoding ascii
#
# The .sha256 format matches the 0608 sidecar: lowercase hex + space + asterisk
# + filename, no trailing newline beyond what Set-Content adds.
# The step below auto-runs when the installer artifact already sits next to
# this script's sibling installer/ folder (i.e. on the build machine post-ISCC).
Write-Section "Emit installer .sha256 sidecar (PKG-03)"
$installerPattern = Join-Path "installer" "SenseEase_Setup-Alpha_Test.*.exe"
$installerFiles   = Get-ChildItem -Path $installerPattern -ErrorAction SilentlyContinue |
                        Where-Object { $_.Extension -eq ".exe" }
if ($installerFiles) {
    foreach ($exe in $installerFiles) {
        $sidecar = "$($exe.FullName).sha256"
        if (-not (Test-Path $sidecar)) {
            $hash = (Get-FileHash -Path $exe.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash *$($exe.Name)" | Set-Content -Path $sidecar -Encoding ascii -NoNewline
            Write-Host "  wrote: $($exe.Name).sha256"
        } else {
            Write-Host "  exists (skipped): $($exe.Name).sha256"
        }
    }
} else {
    Write-Host "  (no installer artifacts found in installer/ -- run ISCC first, then re-run this step)" -ForegroundColor Yellow
}

# -----------------------------------------------------------------------------
# 6. Generate install.manifest.json (H32) -- engine-integrity manifest
# -----------------------------------------------------------------------------
Write-Section "Generate install.manifest.json (H32)"
# Consumed by SenseEase.Infrastructure.Storage.IntegrityVerifier.Verify(fileName).
# Contract (read from IntegrityVerifier.cs):
#   - File name MUST be "install.manifest.json", placed at the dist ROOT (next to
#     SenseEase.App.exe == AppContext.BaseDirectory).
#   - The verifier accepts EITHER a flat { "key": "hash" } object OR a
#     { "files": { ... } } wrapper. We emit the flat form.
#   - Keys are matched two ways (case-insensitive): exact key == fileName, AND
#     Path.GetFileName(key) == Path.GetFileName(fileName). Keying by RELATIVE path
#     satisfies both (a relative key still resolves under the bare-name lookup),
#     so we key by the dist-relative path with FORWARD slashes -- Path.GetFileName
#     treats '/' as a separator on Windows, so the bare-name fallback still works.
#   - Values are SHA-256 hex. The verifier compares case-insensitively
#     (Convert.ToHexString yields uppercase; we emit lowercase, conventionally
#     matching the published .sha256 sidecar).
# This step MUST run LAST (after every trim/copy) so hashes describe the final
# shipped bytes. It is what makes the H32 "absent-manifest = pass" no-op turn
# into a real, enforced gate on packaged builds.
#
# Security-critical surface = the native code + model weights an attacker would
# tamper to alter inference / run code: the llama.cpp server + backend DLLs, the
# GGUF weights, the head-tracker sidecar EXE, and the MediaPipe face model.
# PKG-06: extended to cover every executable asset that actually runs at launch:
#   llama*.dll      — llama.cpp core + any named variant DLLs (e.g. llama-decode.dll)
#   mtmd.dll        — multimodal bridge DLL (ships with recent llama.cpp Vulkan builds)
#   libomp*.dll     — OpenMP runtime bundled alongside the Vulkan backend
#   _internal/*     — PyInstaller bootstrap tree (head_tracker/_internal/*.{pyd,so,dll})
#   face_landmarker.task — SIDE-10: MediaPipe face model (exe-adjacent copy)
# The _internal/ tree is matched by a broad *.dll + *.pyd pass limited to the
# sidecar subtree; get-ChildItem with -Filter only accepts one pattern at a time
# so we iterate the list below.
$manifestPatterns = @(
    "llama-server.exe",
    "head_tracker.exe",
    "llama*.dll",
    "mtmd.dll",
    "libomp*.dll",
    "ggml*.dll",
    "*.gguf",
    "face_landmarker.task",
    "*.pyd"        # PyInstaller _internal/ extension modules (mediapipe, numpy, cv2 etc.)
)

$distFull = (Resolve-Path $Dist).Path
$manifest = [ordered]@{}
$hashedCount = 0
foreach ($pat in $manifestPatterns) {
    Get-ChildItem -Path $Dist -Recurse -File -Filter $pat -ErrorAction SilentlyContinue |
        Sort-Object FullName |
        ForEach-Object {
            # Dist-relative path with forward slashes (stable across machines).
            $rel = $_.FullName.Substring($distFull.Length).TrimStart('\', '/').Replace('\', '/')
            if (-not $manifest.Contains($rel)) {
                $sha = (Get-FileHash -Path $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                $manifest[$rel] = $sha
                $hashedCount++
                Write-Host "  hashed: $rel"
            }
        }
}

if ($hashedCount -eq 0) {
    Write-Warning "No security-critical assets matched; install.manifest.json will be empty."
}

$manifestPath = Join-Path $Dist "install.manifest.json"
# Depth 2 is ample for a flat string->string map; ASCII-only hex + relative paths.
$manifest | ConvertTo-Json -Depth 2 | Set-Content -Path $manifestPath -Encoding UTF8
Write-Host "  wrote: install.manifest.json ($hashedCount entr$(if ($hashedCount -eq 1) { 'y' } else { 'ies' }))"

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
