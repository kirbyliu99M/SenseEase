> **[LEGACY]** Superseded by [Beta-1.md](../Beta-1.md) (2026-06-14). Retained for historical reference / back-trace; not the current plan.

# SenseEase — .NET Packaging & Release Plan (Alpha-2 end)

**Status:** DRAFT for Kirby review · 2026-06-08 · branch `alpha1-w2-engine-integrity`
**Scope:** Windows-only (win-x64), .NET 8 WPF self-contained desktop app + native LLM
backend + Python head-tracking sidecar. Goal: ship the first packaged Alpha-2 build.

> **Key finding:** packaging is *further along than a greenfield plan assumes.* An Inno Setup
> installer (`SenseEase-v0.11.iss`), a post-publish staging script
> (`stage-pre-alpha-assets.ps1`, with H28 bloat-trim + H32 manifest generation), and a staged
> `dist/v0.11-pre-alpha/` (≈2.9 GB) already exist. This document **consolidates and finalizes**
> that path and defines the **release-gate cut-line** — it does not start from scratch.

---

## 1. Current state (what already exists)

| Asset | State |
|:------|:------|
| Inno Setup installer (`SenseEase-v0.11.iss`) | ✅ per-user (`PrivilegesRequired=lowest`, `%LOCALAPPDATA%\Programs\SenseEase`), LZMA2/ultra64 solid compression, zh-Hant+EN uninstall PII prompt (H25) |
| Staging script (`stage-pre-alpha-assets.ps1`) | ✅ trims cross-RID + unused backends (H28), copies model, mirrors sidecar, emits `install.manifest.json` (H32) |
| Integrity gate (`IntegrityVerifier`, App.xaml.cs) | ✅ SHA-256 verifies `head_tracker.exe` + `llama-server.exe` vs manifest; **dormant on Alpha builds** (no manifest), enforced on packaged builds |
| CUDA exclusion | ✅ `ExcludeAssets="native;runtime"` in Infrastructure.csproj + `DropUnusedCudaNative` post-publish target (saves ~148 MB) |
| Deterministic build | ✅ `Directory.Build.props`: `Deterministic`, `DeterministicSourcePaths`, PathMap (no username leak in PDBs) |
| CI/CD | ❌ **none** (no `.github/workflows/`); build + test are manual today |
| Code-signing | ❌ **none** — main exe + sidecar + installer all unsigned (H26, parked) |
| Auto-update | ❌ none — manual re-download + replace (acceptable for Alpha/Beta) |

---

## 2. Bill of materials (≈2.9–3.0 GB uncompressed)

| Component | Size | Ship? | Notes |
|:----------|-----:|:------|:------|
| **Ministral-3B `Q4_K_M.gguf`** | ~2.0 GB | ✅ required | dominates footprint; no smaller quant viable for 3B |
| `mmproj-…-F16.gguf` (vision head) | ~0.8 GB | ⚠️ **verify** | **likely dead** — confirm LLamaSharp never loads multimodal; if unused, drop it (saves ~0.8 GB) |
| Head-tracker sidecar (`dist/head_tracker/`) | ~0.22–0.6 GB | ✅ required | PyInstaller `--onedir`: `head_tracker.exe` (18 MB) + `face_landmarker.task` (3.6 MB) + `_internal/` (numpy/opencv/mediapipe). Ship the tree **unmodified** |
| Vulkan llama.cpp backend (`runtimes/win-x64/native/vulkan/`) | ~100 MB | ✅ required | `llama-server.exe` + `ggml-vulkan.dll` (~55 MB) + CPU-fallback `ggml-cpu-*.dll` |
| .NET 8 self-contained runtime | ~170 MB | ✅ required | so users without the Desktop Runtime can run it |
| Managed assemblies (App/Core/Infra + deps) | ~20 MB | ✅ required | + `Hardcodet.NotifyIcon.Wpf` (tray) |
| **Excluded:** CUDA12 backend | (148 MB) | ❌ dropped | `ExcludeAssets` + `DropUnusedCudaNative` |
| **Excluded:** demo video / MediaElement | — | ❌ removed | dead-code sweep — no codec dep, no packed video |

**Compressed installer:** ~450–500 MB (LZMA2 ~10–15% on the incompressible quantized GGUF).
**Cleanup before ship:** prune stray `src/**/bin-validate/` (≈99 MB Linux natives from NuGet
restore) and `publish-test/` cross-RID `runtimes/linux-*`, `osx-*`.

---

## 3. Publish strategy (.NET 8 WPF)

**Decisions (all confirmed against the codebase):**

- **Self-contained** (`--self-contained -r win-x64`) — end users need not install the .NET 8
  Desktop Runtime; also isolates from a divergent global .NET 8 patch.
- **NOT single-file** — WPF + the 2.9 GB loose models + the PyInstaller `_internal/` tree cannot
  live in one exe. Ship a structured folder (below).
- **Trimming DISABLED** (`PublishTrimmed=false`, explicit) — WPF is not trim-safe and LLamaSharp
  uses reflection. Non-negotiable.
- **ReadyToRun ENABLED** (`PublishReadyToRun=true`) — pre-JIT for faster cold start.
- **RID `win-x64` only** — x86/arm64 untested; do not wildcard RIDs (pulls linux/osx natives).

**Shipped layout (publish output):**
```
SenseEase.App.exe  (+ .deps.json, .runtimeconfig.json)
bin/                                         managed deps + .NET runtime
runtimes/win-x64/native/vulkan/              llama-server.exe + ggml-*.dll   (NO cuda12/)
models/Ministral-3-3B-Instruct-2512-GGUF/    Q4_K_M.gguf  (+ mmproj only if proven needed)
tools/head-tracker-sidecar/dist/head_tracker/  head_tracker.exe + face_landmarker.task + _internal/
install.manifest.json                        SHA-256 of the security-critical binaries (H32)
```
Path resolution already supports this: `AppConfig.ResolveModelPath` / `ResolveSidecarPath` probe
`<bin>/…` first; `MistralChatbotService.ResolveServerExePath` → `runtimes/win-x64/native/vulkan/`;
`SidecarHeadPoseReader` sets the sidecar working dir so `./face_landmarker.task` resolves.

**`SenseEase.App.csproj` (Release PropertyGroup) or a `Release.pubxml`:**
```xml
<PropertyGroup Condition="'$(Configuration)'=='Release'">
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <SelfContained>true</SelfContained>
  <PublishTrimmed>false</PublishTrimmed>
  <PublishReadyToRun>true</PublishReadyToRun>
  <PublishAot>false</PublishAot>
</PropertyGroup>
```
```
dotnet publish src/SenseEase.App/SenseEase.App.csproj -c Release -r win-x64 --self-contained
# then: pwsh stage-pre-alpha-assets.ps1   (trims, copies model + sidecar, writes manifest)
# then: ISCC.exe SenseEase-v0.11.iss      (builds the installer)
```
Models/sidecar are **gitignored loose content** — the staging script copies them from the local
checkout; keep that (don't add 3 GB to the csproj `<Content>` closure / git).

---

## 4. Installer

**Keep Inno Setup** for Alpha→Beta (it works, per-user avoids UAC, LZMA2 is efficient, no
WiX/MSI complexity gain). Revisit **Velopack** for delta auto-update at v1.0 (~100–200 MB patches
vs a 450 MB full re-download).

- Per-user install to `%LOCALAPPDATA%\Programs\SenseEase`; user data in `%LOCALAPPDATA%\SenseEase`.
- Uninstall keeps the opt-in PII-cleanup prompt (H25, default Yes).
- Release builds **must** emit `install.manifest.json` (H32) so the integrity gate goes live.

---

## 5. Code-signing (H26) — *critical, with a functional payoff*

Signing is **not just trust polish** — it is the fix for the dominant real-world bug:

> The unsigned `head_tracker.exe` **hangs under Trend Micro's AMSI / behavioral scanning** at
> native-import (observed 2026-06-07). Head-tracking silently stays inactive (FOV mask pinned
> at center). The 8-second startup watchdog now logs an actionable diagnostic, but **signing the
> sidecar removes the root cause.**

- **Cert:** OV Authenticode is sufficient (EV unnecessary). Recommend **Azure Trusted Signing**
  (~$100/yr, fast issuing) or DigiCert (~$400/yr). **Procurement is a 2–4 week external lead —
  start now** if signed Alpha-2 binaries are wanted.
- **Sign:** installer exe + `SenseEase.App.exe` + `head_tracker.exe` + `llama-server.exe`
  (not individual DLLs). Add a post-publish `signtool` step + Inno `SignTool=` directive;
  unattended once cert + secret are in the pipeline (~2 h integration).
- **SmartScreen:** after signing, submit the signed installer to Microsoft to warm reputation
  (~1 month vs ~3 months organic). Until then, first run shows "Unknown Publisher."

---

## 6. Release gate — Alpha-2 cut-line

### IN scope to call Alpha-2 done + ship a first build
1. **Land the dead-code sweep** (this branch) + my Adaptive-Fit wiring (committed `bdf193b`) —
   then **merge `alpha1-w2-engine-integrity` → main**.
2. **`dotnet build -c Release` + `dotnet test` green** (full suite) — *currently cannot run
   locally while the app holds the DLLs; needs the app closed* — plus a **PII grep sweep**.
3. **Stage + build installer** (`stage-pre-alpha-assets.ps1` → Inno) → `v0.11.1` (or `v0.12`
   if the cert lands in time).
4. **Dual-hardware smoke test** (dGPU + iGPU) — see checklist below.
5. **Tag the release** (semantic version + git tag — none exist today).

### Smoke checklist (pre-ship gate)
- [ ] Installer runs (unsigned ⇒ SmartScreen warning expected; signed ⇒ none).
- [ ] Cold start < 40 s (dGPU) / < 120 s (iGPU/CPU).
- [ ] Chatbot status flips Loading → ready (GGUF lazy-loads on first use).
- [ ] Tray icon + right-click menu work; pop-up card on tray click.
- [ ] Head-tracking starts (watch `%TEMP%\wgc_diag.txt` for "Started pid=…" vs the watchdog fire).
- [ ] `%LOCALAPPDATA%\SenseEase\` created (logs, `habituation_profile.json`, empty `crash.log`).
- [ ] `install.manifest.json` present + integrity gate passes; **no `runtimes/.../cuda12/`**.
- [ ] Uninstall removes the app folder + HKCU Run-key (H49); PII prompt honored.

### DEFERRED to Beta-1
- **CI/CD** (GitHub Actions: build + test on push-to-main) — define what "CI green ×3" means
  (see open questions).
- H9/H10 loopback-auth smoke; H30/H31 single-instance mutex smoke; H32 integrity on a *packaged*
  build (implemented, dev-pass only); H33/H47/H48 dependency bumps (llama.cpp/opencv/pyinstaller).
- Velopack auto-update; model licensing finalization (see risks); first-run model-download tier.

---

## 7. Risks

- **H26 signing is a hard blocker for clean SmartScreen + the AMSI sidecar hang.** 2–4 week
  procurement lead — the long pole. Unsigned ship is functionally complete but socially costly
  (trains the bad `Unblock-File` habit).
- **Model size** (2.0 GB GGUF) dominates; install is network-bound. Compressed ~450–500 MB.
- **`mmproj` (~0.8 GB) may be dead weight** — verify and drop if unused.
- **Sidecar `_internal/` is layout-fragile** — copy the PyInstaller tree byte-for-byte; any
  reshuffle breaks the bootloader silently.
- **No CI** — manual `dotnet test` is the only gate; a missed local failure can slip to ship.
- **Model licensing** — bundled Ministral 3B is "community/POC scope"; resolve before any
  commercial/v1.0 distribution.
- **H12 sidecar env-var** (`SENSEEASE_FACE_LANDMARKER_MODEL` redirect) — confirm the
  `ProcessStartInfo` clearing is merged (potential TFLite-model redirect by same-user malware).

---

## 8. Open decisions for Kirby

1. **Start H26 cert procurement now?** (2–4 wk lead — gates signed Alpha-2 + fixes the AMSI hang.)
2. **Drop `mmproj` (~0.8 GB)?** Confirm multimodal is never used → remove from ship.
3. **Ship `v0.11.1` unsigned now, or wait for `v0.12` signed?**
4. **Define "CI green ×3"** — 3 clean builds? 3 consecutive test passes? 3 hardware runs?
5. **Dual-hardware smoke** — one switchable-GPU machine or two boxes?
6. **Model licensing** — acceptable as "community/POC" for a packaged Alpha-2, or swap first?
7. **Retire `AutoProfilingEnabled` as a flag?** The dead-code sweep removed the explicit-calibration
   *flow entirely*, so the flag's OFF branch is no longer a usable revert path (see the sweep audit).
   If auto-profiling is now the only world, the flag + its OFF-path scaffolding can be retired.

---

## Appendix — dead-code sweep's packaging impact (net positive)

The in-flight calibration/demo removal **simplifies** packaging: no MediaElement/codec dependency,
no bundled video, a smaller managed surface, and fewer XAML views. No packaging step depended on
the removed code. The only packaging-relevant follow-up is decision #7 above (flag retirement) and
confirming no stray `[Calibration]` XAML/config references survive the merge (the sweep audit found
the tree statically clean — all remaining references are comments).
