# =============================================================================
# stage-pre-alpha-assets.ps1
# =============================================================================
# After `dotnet publish -c Release -r win-x64 --self-contained -o <Dist>`, this
# script reshapes the publish output into the Pre-Alpha distributable layout:
#
#   1. Trim cross-platform runtimes (Linux / macOS / win-arm64) -- ~85 MB saved
#   2. Bundle Qwen3-4B Q4_K_M GGUF model into models/ -- ~2.3 GB
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

# Robocopy exit codes 0-7 are SUCCESS (1 = files copied; 2/4 = extras/mismatch present; 3/5/6/7 =
# combinations); only >= 8 is a real failure. Guard every robocopy so a genuine copy failure ABORTS the
# stage, AND reset $LASTEXITCODE to 0 afterwards so a benign '1' from the LAST robocopy can't surface as
# the whole script's non-zero exit code to an orchestrator (Beta-1: the stage otherwise reports a false
# 'failed (exit 1)' even though every pack staged correctly).
function Assert-Robocopy($what) {
    if ($LASTEXITCODE -ge 8) { Write-Error "robocopy failed (exit $LASTEXITCODE) staging $what"; exit 1 }
    $global:LASTEXITCODE = 0
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
# The chatbot launches ONLY llama-server.exe (see LocalLlmChatbotService); the
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
    # Beta-1 review (cleanup): drop the dead CLI *-impl.dll backends. The matching
    # llama-cli/bench/quantize/... .exe launchers are deleted above; their *-impl.dll
    # backing libraries are NOT in llama-server.exe's import closure (verified: the server
    # imports only llama-server-impl.dll -> ggml*.dll + llama.dll + llama-common.dll +
    # mtmd.dll), so they are pure bloat that also polluted install.manifest.json with
    # entries nothing runs. Keep llama-server-impl.dll (the server's own backend).
    Get-ChildItem -Path $vulkanDir -File -Filter "*-impl.dll" -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne "llama-server-impl.dll" } |
        ForEach-Object {
            Remove-Item $_.FullName -Force
            Write-Host "  removed: vulkan/$($_.Name)"
            $vulkanRemoved++
        }
} else {
    Write-Host "  (vulkan/ not present -- skipping)"
}
Write-Host "  ($vulkanRemoved trimmed)"

# -----------------------------------------------------------------------------
# 1d. Stage the Intel OpenVINO backend pack (Phase-3)
# -----------------------------------------------------------------------------
# `dotnet publish` carries ONLY the vulkan/ pack (the csproj Content rule); the validated
# OpenVINO llama-server build lives in the repo at runtimes/win-x64/native/openvino/ (33-file
# pack, docs/PHASE3_NPU_INSTALLER_PLAN.md §11). Copy it into the dist so the .iss can lay it on
# Intel boxes (Check: IsIntelCpu). Trim the OV model-frontend DLLs (~13 MB) -- ggml-openvino
# builds the OV graph directly, so the onnx/paddle/pytorch/tensorflow frontends never load.
# Non-fatal: if the pack is absent, the Intel backend is simply ABSENT and Intel boxes use Vulkan.
Write-Section "Stage Intel OpenVINO pack (Phase-3)"
$ovSrc  = "runtimes/win-x64/native/openvino"
$ovDest = Join-Path $Dist "runtimes/win-x64/native/openvino"
if (-not (Test-Path $ovSrc)) {
    Write-Warning "OpenVINO pack not found at $ovSrc -- the Intel backend will be ABSENT from this build."
    Write-Warning "Build it via installer/build-openvino-llama-server.bat and copy into $ovSrc first."
} else {
    New-Item -ItemType Directory -Path $ovDest -Force | Out-Null
    robocopy $ovSrc $ovDest /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
    Assert-Robocopy "OpenVINO pack"
    $ovFrontends = @(
        "openvino_onnx_frontend.dll",
        "openvino_paddle_frontend.dll",
        "openvino_pytorch_frontend.dll",
        "openvino_tensorflow_frontend.dll",
        "openvino_tensorflow_lite_frontend.dll"
    )
    foreach ($f in $ovFrontends) {
        $fp = Join-Path $ovDest $f
        if (Test-Path $fp) { Remove-Item $fp -Force; Write-Host "  trimmed: openvino/$f" }
    }
    $ovSize = [math]::Round((Get-ChildItem $ovDest -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 0)
    Write-Host "  staged: OpenVINO pack ($((Get-ChildItem $ovDest -File).Count) files, $ovSize MB)"
}

# -----------------------------------------------------------------------------
# 2. Bundle Qwen3-4B Q4_K_M GGUF (the SOLE chat model)
# -----------------------------------------------------------------------------
# Qwen3-4B-Instruct-2507 is now the ONLY chat model -- the Mistral/Ministral
# fallback was removed 2026-06-19 once Qwen proved good in live testing (see
# AppConfig.ModelCatalog). This source path MUST stay in lock-step with
# AppConfig.ModelPath ("models/Qwen3-4B-Instruct-2507-GGUF/...-Q4_K_M.gguf") so the
# app resolves the weights relative to AppContext.BaseDirectory at runtime.
Write-Section "Bundle Qwen3-4B Q4_K_M GGUF model (sole chat model)"
# Defense in depth: scrub any stale Ministral model dir left in the dist by a prior
# staging run so we never ship the retired fallback (the dist's models/ tree is
# created only by this script, so this is the only place it could linger).
$staleMistralDir = Join-Path $Dist "models/Ministral-3-3B-Instruct-2512-GGUF"
if (Test-Path $staleMistralDir) {
    Remove-Item $staleMistralDir -Recurse -Force
    Write-Host "  removed stale Ministral model dir (Mistral fallback retired 2026-06-19)"
}
$modelSrc = "models/Qwen3-4B-Instruct-2507-GGUF/Qwen3-4B-Instruct-2507-Q4_K_M.gguf"
if (-not (Test-Path $modelSrc)) {
    Write-Error "Model not found at $modelSrc. Cannot stage."
    exit 1
}
$modelDest = Join-Path $Dist "models/Qwen3-4B-Instruct-2507-GGUF"
New-Item -ItemType Directory -Path $modelDest -Force | Out-Null
Copy-Item $modelSrc $modelDest -Force
$modelSize = [math]::Round((Get-Item (Join-Path $modelDest "Qwen3-4B-Instruct-2507-Q4_K_M.gguf")).Length / 1MB, 0)
Write-Host "  copied: Qwen3-4B-Instruct-2507-Q4_K_M.gguf ($modelSize MB)"

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
        Assert-Robocopy "head-tracker sidecar"
        $sidecarSize = [math]::Round((Get-ChildItem $sidecarDest -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 0)
        Write-Host "  copied: head_tracker sidecar tree ($sidecarSize MB)"
    }
}

# -----------------------------------------------------------------------------
# 4b. Bundle the AMD FastFlowLM pack (runtime + Q4NX) -- Phase-3
# -----------------------------------------------------------------------------
# AMD Ryzen-AI NPU boxes run FastFlowLM. Bundle the flm runtime + the Q4NX model so the install
# is offline-capable (Kirby 2026-06-23: bundle FLM + Q4NX). The .iss lays:
#   flm runtime -> {app}\packs\amd-flm\flm  (FlmServerLauncher.FindExe probes there FIRST, since a
#                  per-user/lowest-priv install can write neither C:\Program Files\flm nor PATH)
#   Q4NX model  -> {userprofile}\.flm\models\Qwen3-4B-Instruct-2507-NPU2  (FLM's own per-user store)
# CRITICAL dir name: FLM's model_list.json resolves the tag `qwen3-it:4b` (AppConfig.NpuModelTag)
# to the store dir "Qwen3-4B-Instruct-2507-NPU2". The repo source models/Qwen3-4B-NPU2/ is the
# verified-IDENTICAL model (config.json sha + model.q4nx bytes match) but MISNAMED -- so we RENAME
# it to the catalog name on stage. Ship it as Qwen3-4B-NPU2 => `flm serve qwen3-it:4b` finds
# nothing => silent Vulkan fallback (NPU never used). Non-fatal: a missing source => AMD pack ABSENT.
Write-Section "Bundle AMD FastFlowLM pack (runtime + Q4NX) -- Phase-3"
$flmRuntimeSrc = "C:/Program Files/flm"
$q4nxSrc       = "models/Qwen3-4B-NPU2"
$amdPackDest   = Join-Path $Dist "packs/amd-flm"
if (-not (Test-Path $flmRuntimeSrc)) {
    Write-Warning "FastFlowLM runtime not found at '$flmRuntimeSrc' -- the AMD NPU pack will be ABSENT (AMD boxes use Vulkan)."
} elseif (-not (Test-Path $q4nxSrc)) {
    Write-Warning "Q4NX model not found at '$q4nxSrc' -- skipping the AMD pack."
} else {
    # 4b.1 flm runtime -- exclude the standalone FastFlowLM uninstaller (we own this bundled copy).
    $flmDest = Join-Path $amdPackDest "flm"
    New-Item -ItemType Directory -Path $flmDest -Force | Out-Null
    robocopy $flmRuntimeSrc $flmDest /MIR /XF unins000.exe unins000.dat /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
    Assert-Robocopy "FLM runtime"
    $flmMB = [math]::Round((Get-ChildItem $flmDest -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 0)
    Write-Host "  staged: flm runtime -> packs/amd-flm/flm ($flmMB MB)"
    # 4b.2 Q4NX -- RENAMED to the FLM catalog name so `flm serve qwen3-it:4b` resolves it.
    $q4nxDest = Join-Path $amdPackDest "q4nx/Qwen3-4B-Instruct-2507-NPU2"
    New-Item -ItemType Directory -Path $q4nxDest -Force | Out-Null
    robocopy $q4nxSrc $q4nxDest /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
    Assert-Robocopy "Q4NX model"
    $q4nxMB = [math]::Round((Get-ChildItem $q4nxDest -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 0)
    Write-Host "  staged: Q4NX -> packs/amd-flm/q4nx/Qwen3-4B-Instruct-2507-NPU2 ($q4nxMB MB)"
}

# -----------------------------------------------------------------------------
# 4c. Bundle the Intel OpenVINO GenAI pack (host + INT4 IR) -- Phase-2 pivot
# -----------------------------------------------------------------------------
# Intel Core Ultra boxes run the OpenVINO GenAI host (ov::genai::LLMPipeline) on the NPU/iGPU -- the 2026-06-24
# pivot that retires the ggml-openvino llama-server. Bundle the host exe tree + the INT4 IR so the install is
# offline-capable. The .iss lays both under {app}\packs\intel-ov, where OvGenAiHostLauncher.FindExe /
# FindModelDir probe:
#   host -> {app}\packs\intel-ov\host\genai_host.exe (+ its PyInstaller _internal/ tree)
#   IR   -> {app}\packs\intel-ov\model               (openvino_model.xml/.bin + the tokenizer IR)
# Sources come from the build env (SENSEEASE_OVHOST_SRC = the host PyInstaller dist tree; SENSEEASE_OVIR_SRC =
# the INT4 OpenVINO IR dir). A missing source => the Intel pack is ABSENT (Intel boxes then use Vulkan, exactly
# like the launcher's graceful null). Non-fatal, mirrors the AMD pack. See docs/OV_GENAI_HOST.md.
Write-Section "Bundle Intel OpenVINO GenAI pack (host + IR) -- Phase-2 pivot"
$ovHostSrc = $env:SENSEEASE_OVHOST_SRC
$ovIrSrc   = $env:SENSEEASE_OVIR_SRC
$intelPackDest = Join-Path $Dist "packs/intel-ov"
if ([string]::IsNullOrWhiteSpace($ovHostSrc) -or -not (Test-Path $ovHostSrc)) {
    Write-Warning "OV GenAI host source not set/found (SENSEEASE_OVHOST_SRC) -- the Intel OV pack will be ABSENT (Intel boxes use Vulkan)."
} elseif ([string]::IsNullOrWhiteSpace($ovIrSrc) -or -not (Test-Path $ovIrSrc)) {
    Write-Warning "OV IR model source not set/found (SENSEEASE_OVIR_SRC) -- skipping the Intel OV pack."
} else {
    $ovHostDest = Join-Path $intelPackDest "host"
    New-Item -ItemType Directory -Path $ovHostDest -Force | Out-Null
    robocopy $ovHostSrc $ovHostDest /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
    Assert-Robocopy "OV GenAI host"
    $ovHostMB = [math]::Round((Get-ChildItem $ovHostDest -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 0)
    Write-Host "  staged: OV GenAI host -> packs/intel-ov/host ($ovHostMB MB)"
    $ovModelDest = Join-Path $intelPackDest "model"
    New-Item -ItemType Directory -Path $ovModelDest -Force | Out-Null
    robocopy $ovIrSrc $ovModelDest /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
    Assert-Robocopy "OV IR model"
    $ovIrMB = [math]::Round((Get-ChildItem $ovModelDest -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 0)
    Write-Host "  staged: OV IR model -> packs/intel-ov/model ($ovIrMB MB)"
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
# Beta-1 #22: ALWAYS (re)compute the sidecar for the CURRENT artifact, and exclude *.old.exe
# archives. The .iss reuses OutputBaseFilename on same-day rebuilds, so the previous
# skip-if-exists left the published .sha256 hashing the OLD installer bytes -> a tester running
# sha256 verification on the fresh download got a MISMATCH on a legitimate build. Overwriting
# guarantees the sidecar always matches the bytes that were just compiled.
# Match the current Beta/Release naming (OutputBaseFilename = SenseEase-Setup-<ver>). The retired
# Alpha_Test.* glob stopped matching at 1.0.0, so the sidecar was silently skipped for Beta artifacts.
$installerPattern = Join-Path "installer" "SenseEase-Setup-*.exe"
$installerFiles   = Get-ChildItem -Path $installerPattern -ErrorAction SilentlyContinue |
                        Where-Object { $_.Extension -eq ".exe" -and $_.Name -notlike "*.old.exe" }
if ($installerFiles) {
    foreach ($exe in $installerFiles) {
        $sidecar = "$($exe.FullName).sha256"
        $hash = (Get-FileHash -Path $exe.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash *$($exe.Name)" | Set-Content -Path $sidecar -Encoding ascii -NoNewline
        Write-Host "  wrote: $($exe.Name).sha256"
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

# Beta-1 #21: hash the head-tracker sidecar's NATIVE libraries too. head_tracker.exe is a tiny
# PyInstaller bootstrap stub whose hash matches even if _internal/python312.dll (the actual
# CPython interpreter that runs the tracker logic with live camera access) is swapped for a
# tampered copy -- the *.pyd-only pass above left the interpreter + native DLLs unverified, so
# arbitrary native code could run past a manifest that still reads "clean". Hash every
# *.dll/*.so/*.dylib UNDER the sidecar subtree only (scoped so the hundreds of .NET app-root
# DLLs are NOT pulled into the manifest).
$sidecarRoot = Join-Path $Dist "tools/head-tracker-sidecar/dist/head_tracker"
if (Test-Path $sidecarRoot) {
    Get-ChildItem -Path $sidecarRoot -Recurse -File -Include *.dll, *.so, *.dylib -ErrorAction SilentlyContinue |
        Sort-Object FullName |
        ForEach-Object {
            $rel = $_.FullName.Substring($distFull.Length).TrimStart('\', '/').Replace('\', '/')
            if (-not $manifest.Contains($rel)) {
                $sha = (Get-FileHash -Path $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                $manifest[$rel] = $sha
                $hashedCount++
                Write-Host "  hashed: $rel"
            }
        }
}

# Phase-3: hash the Intel OpenVINO pack closure (scoped to its subtree, like the sidecar pass).
# The OV llama-server.exe + ggml*.dll + llama*.dll are already caught by the global patterns above;
# this adds openvino*.dll + tbb*.dll + OpenCL.dll + the VC redist DLLs the OV server loads, so the
# whole Intel-launched closure is integrity-covered (the runtime H32 gate verifies whatever exe the
# probe spawns -- PHASE3 §7). De-dup via Contains() so the overlap with the global pass is harmless.
$ovManifestRoot = Join-Path $Dist "runtimes/win-x64/native/openvino"
if (Test-Path $ovManifestRoot) {
    Get-ChildItem -Path $ovManifestRoot -Recurse -File -Include *.dll, *.exe -ErrorAction SilentlyContinue |
        Sort-Object FullName |
        ForEach-Object {
            $rel = $_.FullName.Substring($distFull.Length).TrimStart('\', '/').Replace('\', '/')
            if (-not $manifest.Contains($rel)) {
                $manifest[$rel] = (Get-FileHash -Path $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                $hashedCount++
                Write-Host "  hashed: $rel"
            }
        }
}

# Phase-3: hash the AMD FastFlowLM pack (flm.exe + its DLLs + the Q4NX weights). flm is a 3rd-party
# runtime reached over loopback with NO bearer, so the install-time manifest hash is the primary
# tamper check for the AMD path (PHASE3 §7). NOTE the Q4NX entry is keyed by its DIST-relative path
# (packs/amd-flm/q4nx/...); the installer relocates it to {userprofile}\.flm\models\... so the runtime
# verifier (which resolves keys under {app}) does NOT re-check the relocated copy -- this is a
# build-time integrity RECORD of the exact bytes shipped, not a runtime gate on the FLM model.
$amdManifestRoot = Join-Path $Dist "packs/amd-flm"
if (Test-Path $amdManifestRoot) {
    Get-ChildItem -Path $amdManifestRoot -Recurse -File -Include *.exe, *.dll, *.q4nx -ErrorAction SilentlyContinue |
        Sort-Object FullName |
        ForEach-Object {
            $rel = $_.FullName.Substring($distFull.Length).TrimStart('\', '/').Replace('\', '/')
            if (-not $manifest.Contains($rel)) {
                $manifest[$rel] = (Get-FileHash -Path $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                $hashedCount++
                Write-Host "  hashed: $rel"
            }
        }
}

# Phase-2 pivot: hash the Intel OpenVINO GenAI pack (the host exe + its native DLLs/_internal + the INT4 IR
# model). Decision #8 (2026-06-25): hash EVERYTHING incl. the ~2.2 GB IR weights -- the host runs over loopback
# with no bearer, so the install-time manifest is the primary tamper check. The runtime sweep
# (VerifyManifestSweep) streams each file + runs on a background Task.Run, so the large IR hash adds no felt
# startup latency (it overlaps the host's cold-compile). The IR = *.xml (graph) + *.bin (weights, incl. the
# tokenizer/detokenizer IR); the host = *.exe + *.dll + *.pyd (_internal/). De-dup via Contains().
$intelOvManifestRoot = Join-Path $Dist "packs/intel-ov"
if (Test-Path $intelOvManifestRoot) {
    Get-ChildItem -Path $intelOvManifestRoot -Recurse -File -Include *.exe, *.dll, *.pyd, *.xml, *.bin -ErrorAction SilentlyContinue |
        Sort-Object FullName |
        ForEach-Object {
            $rel = $_.FullName.Substring($distFull.Length).TrimStart('\', '/').Replace('\', '/')
            if (-not $manifest.Contains($rel)) {
                $manifest[$rel] = (Get-FileHash -Path $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
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

# Stage succeeded. Force a clean exit code: Assert-Robocopy already cleared $LASTEXITCODE after each copy,
# but pin it explicitly so the script never hands a stale native exit code back to its caller.
exit 0
