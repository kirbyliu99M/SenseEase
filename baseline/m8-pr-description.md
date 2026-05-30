# Head-tracking sprint: Chang-Hain FOV mask follows gaze (M1-M8)

Branch: `feature/headtracking-mediapipe` → `main`
Base: `v0.9` (commit `8ae37b0`) + cherry-picked `v0.9.3` hotfix (`80d84aa`)

## Sprint goal

Drive the Chang-Hain FOV mask center `(cx, cy)` from MediaPipe head pose so the comfort zone follows the user's gaze. Grid stays world-locked (vestibular anchor preserved). Opt-in, privacy-first. Pure-CPU prototype; NPU deferred to v1.0+ now that M7 measurement proved CPU sustains 15 Hz under LLM-hot load.

## Five locked design decisions (each with verification reference)

1. **Path B — Python sidecar + JSON IPC, not in-process ONNX, not native shim.**
   Verification: `tools/head-tracker-sidecar/head_tracker.py` exists and emits the OBS-3 derived-only schema; `src/SenseEase.Infrastructure/Real/SidecarHeadPoseReader.cs` consumes the JSON line; no ONNX runtime added to any .csproj.

2. **Grid stays world-locked; only the mask follows gaze.**
   Verification: `OverlayWindow.ApplyHeadPoseToBrush` mutates the FOV brush + grid OpacityMask brush only; the grid lines themselves stay at fixed Canvas pixel coordinates. The OpacityMask geometry tracks head pose so the visibility window moves; the grid does not.

3. **Sidecar owns the webcam; lux derived from same JSON stream.**
   Verification: `LightSensorProvider` factory in `MainViewModel.BuildAmbientLightProvider` selects `SidecarLuxProvider` (luma from sidecar JSON) when `AppConfig.HeadTrackingEnabled` is true, avoiding a separate MediaCapture session. See M6 commit `d53d8bb`.

4. **`AppConfig.HeadTrackingEnabled = false` by default — opt-in privacy.**
   Verification: `AppConfig.HeadTrackingEnabled` default is `false`. App.OnStartup hydrates from `UserPreferences.HeadTrackingEnabled` which also defaults to `false`. New `Settings` UI annotates the toggle as "需重新啟動 / Requires restart". See `docs/HEAD-TRACKING-PRIVACY.md` §1.

5. **Parameter freeze: capture 480×360, infer @ 15 Hz, smoothing α-curve per M5.1-C, yaw 0.0233°/°, pitch 0.035°/° with sign flip.**
   Verification: sidecar `head_tracker.py` constants unchanged since M2 + M5.1-A pitch negation + M5.1-C adaptive curve. Locked baseline: `1a6a1f2` M2 Layer-1 measurement = 14.988 Hz on AMD Ryzen AI 9 HX 370; M5.1-C `7f649c2` full-active = 15.04 Hz; M7 `1d16be2` under LLM-hot = 15.00 Hz mean.

## Milestone arc (M1 → M8)

| Milestone | What | Lead commit |
|---|---|---|
| M1 | Foundation snapshot anchor at sealed v0.9 | `a3b8071` (tag `headtracking-m1-foundation-passed`) |
| M2 | Python sidecar standalone (Path B); perf measured | `3d89a94` + `1a6a1f2` (perf) + `d04c812` (G1 follow-ups) |
| M3 | C# scaffolding (flag-off parameter-identical to baseline) | `8fd3dbf` + `f5c2d8d` (G1 rename) |
| M4 | IPC reader + sidecar-kill safety (OBS-2) | `50e68d0` |
| M5 | Overlay consumes pose; mock-sweep hotkey | `7378dd5` + UX iters `9e4d6f6`/`803e543`/`72ec9e7` |
| M5.1-A | Inverted-direction defensive pipeline (empirical CSV) | `8468626` → `29fb458` → `4d0ee14` → `3bd33cd` → `a76c842` |
| M5.1-B | Multi-mode mask (Balanced / FullFollow / escape hatch) | `5251b64` |
| M5.1-C | Adaptive EMA + per-frame interpolation | `6c1a86c` + `7f649c2` (instr) + `7a08e40` (iter 1) + `098c89a` (iter 2) + `6aad4d1` (tests) |
| v0.9.3 | Cherry-pick: Dispatcher marshal hotfix from main | `80d84aa` |
| M6 | SidecarLuxProvider — luma from same JSON | `d53d8bb` |
| M7 | Perf report — three-layer with LLM hot | `1d16be2` |
| M8 | Packaging — privacy doc + Settings UI + escape×vestibular guard + perf-log gating + lessons | (this PR) |

## Gate record (G0 → G7)

| Gate | Type | Verdict |
|---|---|---|
| G0 | user quick-approve | PASS — baseline cold-start + observation note |
| G1 | [ADVISOR] schema + perf early signal | PASS — schema decisions Q1-Q5; Layer 1 GREEN edge (14.988 Hz, 0.012 Hz margin) |
| G2 | machine | PASS — M3 flag-off invariant green |
| G3 | half-UVHS (OBS-2 sidecar-kill) | PASS — user ran `Stop-Process -Force`; overlay re-centered ≤2 s, no host crash |
| G4 | [UVHS + ADVISOR] | PASS WITH FOLLOW-UPS — both closed (`6aad4d1` AdaptiveEma2D Python tests; `80d84aa` v0.9.3 backport) |
| G5 | machine | PASS — M6 lux provider builds clean, runtime selection verified |
| G6 | [UVHS + ADVISOR] perf decision | PASS — Layer 1/2 GREEN under LLM hot; NPU now bonus, not load-bearing |
| G7 | machine + advisor docs | (pending — this PR) |

## M8 specifically — six items

1. **`docs/HEAD-TRACKING-PRIVACY.md`** — new repo-level privacy doc. Elevates OBS-3 hard rules from `tools/head-tracker-sidecar/PRIVACY.md`; adds opt-in default explanation, OS-level webcam-indicator note, iGPU-vs-NPU distinction for the v1.0 roadmap, auditing/verification recipes.

2. **Settings UI — head-tracking section.** New "頭部追蹤 / Head Tracking" section in `SettingsWindow.xaml`. Three controls:
   - `啟用頭部追蹤 / Enable Head Tracking` checkbox + "需重新啟動 / Requires restart" badge (factory pattern at `MainViewModel` construction).
   - `跟隨模式 / Follow Mode` radio group: `平衡 Balanced (建議)` (default) / `完整跟隨 Full follow (邊緣較完整，可能感覺較不穩定)`.
   - `啟用暫時透明 / Enable temporary transparency` checkbox with sub-label "連點兩下 Ctrl / Double-tap Ctrl".

3. **Escape × vestibular guard (read-only consumer).** `OverlayWindow.HasVestibularDiagnosis` lookup wired in `MainWindow` when the overlay is created. `OverlayWindow.TriggerEscapeHatch` returns early when the lookup is true; `MainWindow.PreviewKeyDown` also short-circuits the Ctrl+Ctrl detection (defensive second layer). Settings UI greys out the escape-hatch checkbox with a tooltip explaining why. **Engine + snapshot types in `SenseEase.Core` are NOT modified — verified via empty diff.**

4. **Lessons doc updated.** `docs/POPUP-AND-HABITUATION-INTEGRATION-AUDIT.md` extended with L16 (empirical CSV for sign conventions), L17 (sprint branches vs main hotfixes), L18 (`empirical > inferred` meta-lesson).

5. **Perf-log gating.** New `AppConfig.HeadTrackingPerfLogEnabled` flag (default false). `MainWindow.OnCompositionRendering` only emits `[HeadPoseRender]` log lines when the flag is on. Counters still tick (negligible `Interlocked.Increment`), but production gets no log spam.

6. **PR readiness.** This file + a single M8 commit covering all five items above.

## Test summary

- **Before M8**: 685 / 0 / 1 (head-tracking branch after `v0.9.3` cherry-pick).
- **After M8**: **706 / 0 / 1** in `dotnet test tests/SenseEase.Tests/SenseEase.Tests.csproj --nologo --verbosity minimal`. Net +21 (2 new `UserPreferencesTests` for schema v1→v2 roll-forward + head-tracking field round-trip; remainder from rebuild / new XUnit discovery, no test deletion).
- Locked file confirmation:
  - `git diff v0.9 HEAD -- src/SenseEase.Core/HabituationEngine.cs src/SenseEase.Core/Models/HabituationProfileSnapshot.cs` → empty.
  - `git diff HEAD -- tools/head-tracker-sidecar/head_tracker.py src/SenseEase.Infrastructure/Real/SidecarHeadPoseReader.cs` (for the M8 commit) → empty.

## v1.0 backlog (recorded, not blocking)

1. **Webcam-takeover race-cooldown** — if another app grabs the webcam while head-tracking is on, sidecar dies cleanly but reacquisition currently requires manual head-tracking toggle. v1.0: detect the exit, wait N seconds for the other app to release, retry.
2. **Layer 3 median filter** — removed during M5.1-A v2 against 22 s of controlled test motion. Real v1.0 use (months of varied lighting / webcams) could surface slow-drift outliers; reinstate if real-world reports "mask occasionally drifts to wrong position".
3. **Snappy ↔ Stable smoothing slider** — advisor's M5.1-C spec included a user-tunable slider deferred per S2 lean-prototype scope. Current AdaptiveEma2D thresholds (0.08 / 0.19 / 0.55) are tuned to one user's motion + webcam latency.
4. **Runtime HeadTrackingEnabled toggle without restart** — currently `IHeadPoseReader` and `LightSensorProvider` factories run once at `MainViewModel` construction. v1.0 should rebuild both on flag flip so the Settings checkbox doesn't need the "需重新啟動" annotation.
5. **Escape × vestibular tuning beyond simple disable** — current behaviour for vestibular-diagnosed users is hard-off. v1.0 candidate: tunable cap (e.g. 60 % instead of 30 %, or shorter window) so the affordance isn't lost entirely.
6. **Startup fade-in (~2 s) for sidecar warm-up smoothness** — advisor mentioned this as M8 optional. Skipped to keep M8 commit clean; existing 380 ms OverlayWindow fade is the current behaviour. v1.0: extend the fade-in to ~2 s and synchronise with sidecar first-sample arrival so the user doesn't see the mask snap to a stale (0.5, 0.5) before the first pose arrives.
7. **NPU engagement (Path B in privacy doc §4.2)** — ONNX Runtime DirectML EP or AMD Ryzen AI SDK. Requires `face_landmarker.task` (TFLite) → ONNX conversion. M7 confirmed not load-bearing for the prototype.
8. **iGPU delegate (Path A in privacy doc §4.1)** — `BaseOptions(delegate=GPU)` one-line change in sidecar to free a CPU core for WPF compositing, climbing render rate from ~75 Hz under LLM-hot back toward ~165 Hz on the M7 test hardware.

## Files touched in M8

- `docs/HEAD-TRACKING-PRIVACY.md` (new)
- `docs/POPUP-AND-HABITUATION-INTEGRATION-AUDIT.md` (lessons L16-L18 appended)
- `src/SenseEase.App/App.xaml.cs` (hydrate AppConfig.HeadTracking* from UserPreferences at startup)
- `src/SenseEase.App/Configuration/AppConfig.cs` (revert dev-override; add `HeadTrackingPerfLogEnabled` flag)
- `src/SenseEase.App/Configuration/UserPreferences.cs` (schema 1 → 2; 3 new fields + roll-forward loader)
- `src/SenseEase.App/Views/SettingsWindow.xaml` (head-tracking section)
- `src/SenseEase.App/Views/SettingsWindow.xaml.cs` (hydrate + handlers)
- `src/SenseEase.App/Views/OverlayWindow.xaml.cs` (`HasVestibularDiagnosis` property + escape-hatch guard)
- `src/SenseEase.App/MainWindow.xaml.cs` (wire `HasVestibularDiagnosis` on overlay; defensive Ctrl-tap short-circuit; perf-log gating)
- `tests/SenseEase.Tests/UserPreferencesTests.cs` (schema v=2 expectations + new v1→v2 roll-forward + head-tracking field round-trip tests)
- `baseline/m8-pr-description.md` (this file)
