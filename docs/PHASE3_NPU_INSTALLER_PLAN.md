# Phase 3 — Hardware-Differentiated NPU Installer (SPEC / NOT BUILT)

> **Status:** SPEC ONLY · 2026-06-21 · branch `alpha1-w2-engine-integrity`
> **This is the "need to know" + structure for a future installer build. NOTHING here is built.**
> Installer builds are **never autonomous** — Kirby confirms every `publish → stage → ISCC` cycle.
> Builds on the existing Inno Setup path; **no WiX needed** for this (the
> [WiX/WPF bootstrapper plan](../installer/WIX_WPF_BOOTSTRAPPER_PLAN.md) is a *separate* installer-UI-theming
> concern, orthogonal to hardware differentiation).

---

## 0. One-paragraph summary

The runtime side is **done and shipped** (commits `2e8f08e`, `de9fdf4`, `67a493c`, `fdee008`): SenseEase
auto-selects an LLM backend at startup — **FastFlowLM on an AMD Ryzen-AI NPU**, the **OpenVINO `llama-server`
on an Intel NPU/iGPU**, or the **universal Vulkan `llama-server`** — and **always falls back to Vulkan** if the
matched accelerator can't initialise. Phase 3 makes the **installer** lay down only the **hardware-matched
pack(s)** instead of shipping all three to every machine, so an ultrabook's install stays small. The one place
with a real trade-off — the AMD box, where the NPU model (Q4NX) and the universal GGUF are *different formats
of the same model* and can't be shared — gets a **user-choice wizard page** (informed consent). Everything else
installs silently. The install-time probe **mirrors the runtime probe** (one shared spec), and the runtime
**verifies** (Vulkan fallback), so install-time can pick optimistically.

---

## 1. Packaging matrix (what each machine installs)

| Machine | Backend chosen at runtime | Model artifact | Installed pack | Net install Δ vs baseline |
|---|---|---|---|---|
| **AMD Ryzen-AI 300+ (NPU)** — *Efficient (default)* | FastFlowLM on NPU | **Q4NX** (~2.5 GB) | flm runtime (~16 MB) + Q4NX; **no GGUF** | ≈ same as baseline (one model) |
| **AMD Ryzen-AI 300+ (NPU)** — *Maximum reliability* | FLM NPU, GGUF/Vulkan fallback | **Q4NX + GGUF** (~5 GB) | flm + Q4NX **and** Vulkan + GGUF | **+~2.5 GB** |
| **Intel Core Ultra (NPU/iGPU)** | OpenVINO `llama-server` | **GGUF** (same file Vulkan uses) | OpenVINO `llama-server` build + GGUF | **+~size of the OpenVINO native dir** (no duplicate model) |
| **Anything else** (older AMD/Intel, NVIDIA, no NPU) | Vulkan `llama-server` | **GGUF** | Vulkan build + GGUF | baseline |

Key asymmetry: **only AMD duplicates the model** (Q4NX ≠ GGUF). Intel reuses the one GGUF for both its OpenVINO
and Vulkan rungs, so **Intel/non-NPU machines see NO choice page** — the matched pack installs silently.

**Runtime guarantee:** whatever is installed, if the accelerator fails at runtime (missing NPU driver, BIOS
disabled, FLM/OpenVINO fault) the app degrades gracefully — Vulkan if its GGUF is present, else the
deterministic lexicon. Screen protection + the crisis-safety floor are **never** affected.

---

## 2. Current state — the foundation Phase 3 extends

| Asset | File | Role for Phase 3 |
|---|---|---|
| Installer | [`installer/SenseEase-v0.11.iss`](../installer/SenseEase-v0.11.iss) | per-user, no elevation; today one recursive `[Files]` from the dist tree. Add `Check:`-gated entries + the AMD page. |
| Staging | [`scripts/stage-pre-alpha-assets.ps1`](../scripts/stage-pre-alpha-assets.ps1) | reshapes publish → dist; trims runtimes; bundles GGUF + sidecar; **generates `install.manifest.json` LAST**. Add OpenVINO/flm/Q4NX staging + manifest patterns. |
| Integrity gate | `src/SenseEase.Infrastructure/Storage/IntegrityVerifier.cs` | SHA-256-verifies the **launched** server exe vs the manifest. Already covers whatever `LaunchAsync` spawns (Vulkan *or* OpenVINO). |
| **Runtime probe (DONE)** | `LlmBackendRouting.cs`, `FlmServerLauncher.cs`, `LocalLlmChatbotService.cs` | the canonical selection logic the install-time probe must mirror — see §3. |

Runtime pieces already in place (this is what the installer aligns to):
- `HardwareProbe.DetectCpuVendor()` — CPU vendor from `PROCESSOR_IDENTIFIER`.
- `HardwareProbe.Classify(cpuVendor, deviceNames)` + `HardwareProbe.NpuMarkers` — the NPU-name markers.
- `LlmBackendSelector.ShouldTryFlmNpu(...)` / `ShouldUseOpenVino(...)` — the runtime decision (unit-tested).
- `FlmServerLauncher.FindExe()` — locates `flm.exe` (PATH → `C:\Program Files\flm\flm.exe`).
- `LocalServerKind` + `ResolveServerExePath(kind)` — Vulkan vs `runtimes/.../openvino/llama-server.exe`.

---

## 3. The shared probe spec (single source of truth)

Install-time and runtime **must agree** on what hardware they see, or a machine could install the AMD pack but
run the Vulkan path (or vice-versa). The canonical inputs:

| Signal | Runtime source | Install-time source (Inno Pascal) |
|---|---|---|
| **CPU vendor** | `PROCESSOR_IDENTIFIER` env (`…AuthenticAMD` / `…GenuineIntel`) | WMI `Win32_Processor.Manufacturer` (`AuthenticAMD`/`GenuineIntel`) and/or `.Name` (brand) |
| **AMD ⇒ FLM** | vendor contains `amd`/`ryzen` **and** `flm.exe` present | vendor is AMD **and** the box is Ryzen-AI 300+ (NPU). NPU presence optional — see note. |
| **Intel ⇒ OpenVINO** | vendor contains `intel` **and** OpenVINO build present | vendor is Intel (OpenVINO picks NPU→CPU itself) |
| **NPU device name** | (not needed at runtime — capability is proven by a successful launch) | `Win32_PnPEntity.Name` matched against the marker list below |

**Canonical NPU markers** (keep in lock-step with `HardwareProbe.NpuMarkers`):
```
"npu compute accelerator", "npu compute", "ipu device", "amd ipu", "ai boost", "neural processor"
```

**Design principle — install optimistically, runtime verifies.** The install-time probe does *not* need to be
perfect about NPU presence, because the runtime **launch-verifies** (a failed FLM/OpenVINO launch → Vulkan).
So the installer can attribute the NPU by CPU vendor + generation alone and let the runtime sort out a machine
that lied. This is why the runtime fallback (already built) is load-bearing for the whole packaging story.

---

## 4. Install-time hardware probe (Inno Setup `[Code]`, WMI)

Per-user install (`PrivilegesRequired=lowest`) — **read-only WMI works without elevation**. A reusable Pascal
template lives in [`installer/npu-hardware-probe.draft.iss`](../installer/npu-hardware-probe.draft.iss) (NOT wired
into the shipping `.iss`; copy/`#include` it when the build is greenlit). It provides:

- `DetectCpuVendor(): String` — `Win32_Processor.Manufacturer`.
- `HasNpuDevice(): Boolean` — enumerate `Win32_PnPEntity`, match `.Name` against the marker list.
- Cached globals (`GCpuVendor`, `GHasNpu`) populated once in `InitializeSetup`.
- `Check:` predicates: `IsAmdNpu`, `IsIntelCpu`, plus the AMD page's `WantsGgufFallback`.

WMI access pattern (Inno Pascal):
```pascal
WbemLocator := CreateOleObject('WbemScripting.SWbemLocator');
WbemServices := WbemLocator.ConnectServer('localhost', 'root\CIMV2');
// ExecQuery('SELECT Manufacturer,Name FROM Win32_Processor')
// ExecQuery('SELECT Name FROM Win32_PnPEntity')  -> match NpuMarkers
```

---

## 5. Installer structure (Inno Setup changes)

1. **`Check:`-gated `[Files]`** — split the single recursive entry into the always-on app/runtime + per-pack
   entries staged into vendor subdirs, each gated:
   ```
   ; Vulkan baseline — always (universal fallback)
   Source: "{#DistRoot}\runtimes\win-x64\native\vulkan\*"; DestDir: "{app}\runtimes\win-x64\native\vulkan"; Flags: ...
   ; AMD NPU pack (flm runtime + Q4NX) — only on AMD-NPU boxes
   Source: "{#DistRoot}\packs\amd-flm\*";   DestDir: "{app}";  Check: IsAmdNpu;  Flags: ...
   ; Intel OpenVINO build — only on Intel boxes
   Source: "{#DistRoot}\packs\intel-ov\*";  DestDir: "{app}\runtimes\win-x64\native\openvino"; Check: IsIntelCpu; Flags: ...
   ; GGUF — always EXCEPT AMD-Efficient (which is Q4NX-only)
   Source: "{#DistRoot}\models\*"; DestDir: "{app}\models"; Check: WantsGguf; Flags: ...
   ```
2. **AMD user-choice page** — `CreateInputOptionPage` (exclusive radio), shown only when `IsAmdNpu`:
   - ○ **Efficient (recommended) — ~2.5 GB.** NPU only (Q4NX). If the NPU is ever unavailable it auto-switches
     to the lightweight keyword/lexicon mode — still works. *(= Q4NX-only; GGUF/Vulkan rung dropped.)*
   - ○ **Maximum reliability — ~5 GB.** Also installs the universal model on the graphics chip, so the full
     assistant keeps working at full quality if the NPU fails. *(= Q4NX + GGUF.)*
   - Footer: *"Everything stays on your device. Re-run the installer to change this later."*
   - **Default = Efficient** (FLM validated stable on the HX 370, 2026-06-21). **Flip to Maximum only if a
     later FLM validation shows the NPU path is flaky.**
   - The page text must be bilingual zh-Hant + EN; **save the `.iss` as UTF-8 *with BOM*** (the existing script
     already requires this for its CJK uninstall prompt).
3. **Online bootstrapper vs offline kit** (pick one — see §8 decisions):
   - *Online:* tiny installer probes hardware → downloads only the matched pack (`idp` / `DownloadTemporaryFile`).
     "Efficient" ships nothing extra and pulls Q4NX on first run; needs internet at install.
   - *Offline-selective:* one big installer contains all packs, `Check:` installs only the matched — bigger
     download, minimal *installed* footprint.
4. **`[InstallDelete]` hygiene** — on a matched/upgrade install, prune the *other* vendor's pack so a machine
   that changed hands (or a re-run with a different choice) doesn't carry dead weight. Append-only, `filesandordirs`
   (safe no-op when absent), e.g. `{app}\runtimes\win-x64\native\openvino` on an AMD-only install.

---

## 6. Staging pipeline changes (`stage-pre-alpha-assets.ps1`)

Add, alongside the existing GGUF/sidecar staging:
1. **OpenVINO build** → stage the `-DGGML_OPENVINO=ON` `llama-server.exe` + its DLLs into
   `runtimes/win-x64/native/openvino/` (mirror the vulkan keep-list trim).
2. **FastFlowLM runtime** (if bundling, not download-on-run) → stage `flm.exe` + its DLLs into a `packs/amd-flm/`
   tree, and the **Q4NX** model set (`config.json`, `model.q4nx`, `tokenizer*.json`).
3. **Manifest patterns** — extend `$manifestPatterns` so the new *launched/loaded* assets are integrity-hashed:
   add the OpenVINO `llama-server.exe` (caught by the existing `llama-server.exe` + `ggml*.dll` globs **if** it
   lands under a hashed path — verify), `flm.exe`, the flm DLLs, and `model.q4nx`. The manifest is the security
   surface for "what an attacker would tamper to alter inference."
4. **Lock-step rule (unchanged, now wider):** the manifest is generated **LAST**, so **swap a binary/model →
   re-run the stage script → done.** Never ship a new backend beside a *stale* `install.manifest.json` → the
   runtime H32 gate reads `Tampered` and refuses to launch it (closing to Vulkan/lexicon).

---

## 7. Integrity / security implications

- The **OpenVINO `llama-server.exe` is integrity-verified for free** — `LaunchAsync` already calls
  `IntegrityVerifier.Verify(serverExe)` on *whatever* exe it resolves (Vulkan or OpenVINO). It only needs to be
  **in the manifest** (staging §6.3).
- **flm.exe + its DLLs + `model.q4nx`** are *new* security-critical surface if bundled. The FLM server is
  reached over loopback with **no bearer** (it's a 3rd-party runtime we don't control), so the install-time
  manifest hash is the main tamper check for the AMD pack. (Runtime note: we connect to flm; we don't
  integrity-gate it per-request the way we gate our own llama-server's spawn.)
- The **App startup H32 gate** (`App.xaml.cs`) currently resolves the *Vulkan* exe via the parameterless
  `ResolveServerExePath()`. For a packaged OpenVINO build it should also be able to verify the OpenVINO exe —
  small follow-up: make the startup gate verify whichever local build the probe selected (or verify both if
  present). Dev/alpha builds have no manifest → no-op pass, so this is a packaging-time concern only.

---

## 8. Decisions needed from Kirby (before any build)

1. **Online bootstrapper vs offline-selective kit?** (Online = smallest download, needs internet at install;
   offline = one big file, works air-gapped.) *Recommendation: offline-selective first (simplest, testers have
   no install-time network assumptions), online later.*
2. **AMD default — Efficient vs Maximum reliability?** *Recommendation: Efficient (FLM validated stable on the
   HX 370). Revisit if broader FLM testing shows NPU flakiness.*
3. **Bundle the FastFlowLM runtime, or require the user to install FastFlowLM separately?** The dev box has it
   at `C:\Program Files\flm`. For an installer, bundling (~16 MB runtime) is cleanest; check FLM's
   license/redistribution terms first.
4. **Q4NX in the installer vs `flm pull` on first run?** Bundling = offline-capable but +2.5 GB in the kit;
   `flm pull qwen3-it:4b` on first run = smaller kit, needs internet once.
5. ~~**Intel/OpenVINO binary — obtain now or defer?**~~ **DONE (2026-06-21)** — the `-DGGML_OPENVINO=ON`
   `llama-server.exe` was built from source + validated (CPU device, SenseEase GGUF served, inference OK) and
   placed in the dev-build `runtimes/.../openvino/`. See §11. Remaining for Intel: run it on the actual NPU
   (Kirby's Intel box) + stage the pack into the installer.

---

## 9. Build invocation (FOR REFERENCE — run only on Kirby's explicit go)

```
# 1. publish
dotnet publish src/SenseEase.App/SenseEase.App.csproj -c Release -r win-x64 --self-contained -o dist/<ver>
# 2. stage (trims, bundles backends + models, regenerates install.manifest.json LAST)
.\scripts\stage-pre-alpha-assets.ps1 -Dist dist/<ver>
# 3. compile installer
& "C:\Users\kirby\AppData\Local\Programs\Inno Setup 6\ISCC.exe" "installer\SenseEase-v<ver>.iss"
# 4. emit .sha256 sidecar (stage script step 5b, re-run post-ISCC)
```
The **2026-06-11 installer artifact is STALE** (A2 grid bug) — do not distribute. **Every** installer build
needs Kirby's confirmation; never build autonomously.

---

## 10. What's already done (so this stays small)

Runtime selection, launch, fallback, idle-unload, diagnostics, and the OpenVINO seam are **built, tested, and
committed**. Phase 3 is *packaging only* — wiring the probe into Inno, splitting `[Files]` by `Check:`, the AMD
page, and extending the stage script + manifest. No new runtime code is required to ship the AMD path; the Intel
path needs only the OpenVINO binary + an Intel machine to validate.

---

## 11. OpenVINO binary — BUILT + VALIDATED (2026-06-21)

The OpenVINO `llama-server` is no longer hypothetical — it was **built from source and validated** on Kirby's
machine (the AMD dev box, CPU device) and placed in the dev-build runtime. The Intel-NPU run is still Kirby's to
do (this box has no Intel NPU; SenseEase keeps AMD on FLM/Vulkan by design), but the binary + DLL pack are proven.

### Verified build recipe (what actually worked)
- **Host:** Visual Studio Community **2026** (MSVC 14.51) — newer than the doc's VS 2022, configured + compiled
  cleanly. OpenVINO toolkit **2026.2.0** (archive, extracted under Downloads). **vcpkg + `opencl`** — a REQUIRED
  build dep (`ggml/src/ggml-openvino/CMakeLists.txt` has `find_package(OpenCL REQUIRED)`).
- **Source:** llama.cpp master (`063d9c1`), `git clone --depth 1`.
- **Env (one shell):** `VsDevCmd.bat -arch=amd64` then the toolkit's `setupvars.bat` (sets `OpenVINO_DIR` →
  `runtime\cmake`; `find_package(OpenVINO)` then resolves).
- **Configure / build:**
  ```
  cmake -B build\ReleaseOV -G Ninja -DCMAKE_BUILD_TYPE=Release -DGGML_OPENVINO=ON ^
        -DCMAKE_TOOLCHAIN_FILE=<vcpkg>\scripts\buildsystems\vcpkg.cmake -DLLAMA_CURL=OFF -DGGML_VULKAN=OFF
  cmake --build build\ReleaseOV --target llama-server --parallel
  ```
  → "Including OPENVINO backend", "Found OpenCL 3.0", build emits `build\ReleaseOV\bin\` (`llama-server.exe` +
  `llama-server-impl.dll` + `ggml*.dll` incl. **`ggml-openvino.dll`** + `llama*.dll` + `mtmd.dll` + `OpenCL.dll`).
  Reproducible script: [`installer/build-openvino-llama-server.bat`](../installer/build-openvino-llama-server.bat).

### The runtime pack (`runtimes/win-x64/native/openvino/`) — self-contained, ~222 MB, 33 files
- the build's `*.exe` + `*.dll` closure;
- OpenVINO runtime from `<toolkit>\runtime\bin\intel64\Release\*.dll` — `openvino.dll`, the device plugins
  `openvino_intel_{npu,gpu,cpu}_plugin.dll` (+ `openvino_intel_npu_compiler*.dll`), `auto`/`hetero` plugins,
  `openvino_ir_frontend.dll`. **OV 2026 ships NO `plugins.xml`** — plugins are found by the
  `openvino_intel_<device>_plugin.dll` naming convention, so co-location alone works;
- TBB (`tbb12.dll`, `tbbbind_2_5.dll`, `tbbmalloc*.dll`, release only);
- **VC runtime** (`vcruntime140.dll`, `vcruntime140_1.dll`, `msvcp140.dll` from the VS redist) so it runs on a
  machine without Visual Studio.
- *Installer trim:* the model frontends (`openvino_{onnx,pytorch,tensorflow,paddle}_frontend.dll`, ~13 MB) are
  NOT needed (ggml-openvino builds the OV graph directly) — drop them to shave size.

### Validation (this AMD box, CPU device)
`GGML_OPENVINO_DEVICE=CPU` serving SenseEase's **Qwen3-4B-Instruct-2507-Q4_K_M.gguf**: log showed
`OPENVINO0: OpenVINO Runtime` registered, the **Q4_K_M GGUF loaded** (no quant issue), `server is listening`,
and a chat completion returned a token. ⇒ binary + DLL set good; pack is in the dev-build `runtimes/.../openvino/`.

### To test on the Intel device
- **Quick (no installer):** copy the dev build + this `openvino/` pack to the Intel machine and run — SenseEase
  sees Intel + the OpenVINO exe present → `ShouldUseOpenVino` true → launches it with `GGML_OPENVINO_DEVICE=NPU`.
- **Installer (Phase 3):** stage this pack as the Intel pack (`Check: IsIntelCpu` → `runtimes/.../openvino/`),
  and add its `llama-server.exe` + `ggml*.dll` + `openvino*.dll` to `install.manifest.json`.

### Staging-script lines (for `stage-pre-alpha-assets.ps1`, when bundling the Intel pack)
```powershell
# stage the prebuilt + validated OpenVINO pack (built per §11):
$ovPack = "<path-to>/openvino-pack"
robocopy $ovPack (Join-Path $Dist "runtimes/win-x64/native/openvino") /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
# extend the manifest so the OV runtime is integrity-hashed (the existing globs already catch llama-server.exe + ggml*.dll):
$manifestPatterns += "openvino*.dll"
```
