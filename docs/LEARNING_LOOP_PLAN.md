# SenseEase — FMS Feedback + Learning-Loop Plan

**Created 2026-06-16.** Roadmap collection (not a single-PR scope). Each Part-A/B/C item is
independently shippable and individually gated (build + full test green, adversarial review,
UVHS for visible surfaces, sign-off before commit).

## Context

This work reworked SenseEase's **user feedback** around a 0-20 FMS (Fast Motion Sickness Scale)
self-report, then turned into an architecture investigation of how SenseEase **learns** and how to
make its core motion signal (**OFI**) more accurate. Two motivations:

1. **Feedback was naive.** The old dashboard had an always-on 5-star "comfort" widget with no
   context, no cadence, and no trend — low willingness to use, low data quality. Steered to an
   **event-driven, contextual, paced** model (Teams/Trend-Micro style), organized into the **Eye
   Care tab**, with data exportable as CSV for long-term analysis.
2. **The learning loop is built-but-disconnected.** The rich signals (OFI, hand-adjustments, chatbot
   analysis, and the new FMS reports) mostly **dead-end** — they don't feed adaptation. And OFI
   calibration ("M1") is a manual, incomplete, un-applied runbook on a single global placeholder.

Intended outcome: (a) land the FMS/check-in feature cleanly; (b) connect the signals into one closed
loop that produces **two outputs — ① the personalized overlay set, ② the per-app effectiveness
analysis — where ② feeds back into ①**, bucketed per app; (c) make OFI accurate and self-calibrating.

---

## Part 0 — Done (staged, uncommitted; build 0 errors / 1432 tests pass / 0 fail / 3 skip)

- **C — cleanup:** removed orphan `OnConsoleResetClicked` + the dead MainWindow `GridViewColumnHeader`
  style. (Locked `SenseDefaults` dead constants left quarantined — see A3.)
- **D — FMS redesign:** `SymptomEvent.Sources.Slider`; `ComfortRatingScale.BuildMeasuredFmsReport`
  (0-20, fail-loud); `MainViewModel.RecordFmsRating` / `GetRecentComfortSummary` / `GetRecentCheckIns`;
  capture UI 5★ → 0-20 slider → inline → **paced dynamic** inline selector (30-min cooldown + tier-rise
  re-warrant + live update + no-flicker latch); not-a-medical-device disclaimers; `SymptomEventCsvExporter`
  + Settings "Export raw data (CSV)".
- **E — overlay topmost:** dropped `ShowInTaskbar="False"` (rely on `WS_EX_TOOLWINDOW`).
- **Phase 1 — event-driven check-in:** `UserPreferences.ProactiveCheckInsConsent` tri-state (opt-in);
  `Services/CheckInGovernor` (8h cooldown + 24h dismiss-suppression); spike → consent/governor → context
  → survey; **big centered Teams-style overlay + dismiss-on-click scrim** (console-foreground-only;
  background spikes defer to next activation; fades gate on `ReducedMotion()`).
- **Phase 2 — Eye Care tab (護眼):** "我的感受紀錄 / My check-ins" card (weekly trend, aligned recent rows,
  inline paced self-report, CSV hint).
- **Data lifecycle:** "Clear all my data" deletes `symptom_log.jsonl` (+ regression test); clear-all dialog
  lists check-in history; uninstaller offers to `DelTree` the whole `%LOCALAPPDATA%\SenseEase` dir.

### Open decisions (not blocking the above)
- **Copy tone** — sweep parked, awaiting tone (clinical-neutral / warm-plain / minimal).
- **UVHS visual pass** — the big centered overlay + scrim is the one surface that can't be triggered on demand.
- **Locked `SenseDefaults` dead constants** (`OverlayRadiusAttack/ReleaseHalfLifeMs`,
  `OverlayBrushRadiusCornerMargin`) — quarantined; deletion pending sign-off.
- **Commit** the arc on a branch.
- **Head-tracker Release A/B experiment** (`dotnet run -c Release`) — deferred; head-tracker paused +
  byte-identical to the working 0611 installer.

---

## Part A — Close out the FMS / check-in arc (near-term)

- **A1. Copy-tone sweep** — one pass over every check-in / Eye-Care / disclaimer string (EN + 繁中) in
  `MainWindow.xaml(.cs)` + `SettingsContent.xaml`; must pass `BannedPhraseLintTests` (§6).
- **A2. UVHS visual pass** — optional temporary "force-show overlay" dev shortcut (strip before commit).
- **A3. Locked-constant decision** — delete the 3 dead `SenseDefaults` constants (sign-off) or keep quarantined.
- **A4. Commit** the arc on a branch (on the word).

---

## Part B — Connect the learning loop (core forward work)

**Model.** Inputs (per-app OFI · hand-adjustments · chatbot analysis · FMS) → learning → **① overlay set**
(`DeterministicSetSelector` → `_activeSet` → `SetCurve` → FOV/mask) + **② effectiveness analysis**
(`ClinicalExport` ON-vs-OFF FMS delta per category). Today ② is a dead-end; goal: **② feeds ①**, per app.

- **B1. FMS → selector (keystone).** Pre-built seam: `IntensityStatsBuilder.Build` already takes an optional
  `fmsSelector` and computes `UserIntensityStats.FmsAggregate`, but it's always 0 and the selector ignores
  it. Join measured FMS from `symptom_log.jsonl` into the durable 7-day slice; populate `FmsAggregate`; add
  an FMS term to `DeterministicSetSelector.Score`; tests + effectiveness-regression guard. → overlay set
  becomes **outcome-driven**. Re-tighten the "provisional until M1" selector constants.
  Files: `src/SenseEase.Core/AdaptiveFit/IntensityStatsBuilder.cs`, `DeterministicSetSelector.cs`,
  `UserIntensityStats.cs`; `src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs` (Select sites ~1025/1046,
  durable slice ~1005-1011).
- **B2. Persist the manual nudge as a preference.** The ±1 nudge (`MainViewModel.NudgeIntensityLevel` →
  `MockInferenceEngine.NotifyManualLevelNudge`) only **brakes** the selector (`W5ManualConflict` +
  `AdaptiveFitStateMachine.OnManualAdjust`) and is **NOT logged**. Append an `AdjustmentRecord` (per
  `ForegroundApp`) so the strongest direct signal becomes a learned target (selector prior + habituation
  caution via `WeightFor`/`SetPersonalization`), not just a transient veto.
- **B3. Per-app bucketing.** Bucket `IntensityStatsBuilder` + the effectiveness analysis by `ForegroundApp`
  → per-app `UserIntensityStats` → per-app overlay set (apply seam `ApplySetMove`/`SetLevel` exists). Behind D-16.
- **B4. App-aware OFI/pressure.** Three additive seams (behind D-16): add `ForegroundKey` to
  `OpticalFlowSample` (populated in `WgcSensingProvider`); thread optional `foregroundKey` into
  `IInferenceEngine.Update`; branch `PersonalPressureScale` by category (`category → scale`).
- **B5. Promote chatbot retrospective analysis** from prompt-only (`UserModelRetrospectiveAnalyzer` →
  longitudinal observations) to a structured adaptation input. Later.

---

## Part C — OFI accuracy + automated calibration

**Background.** OFI = MAD (mean per-pixel frame diff, 160×90) × `WgcMadNormFactor` (global `4.0` placeholder)
→ clamp → median → EMA → leaky integrator → pressure → tier/FOV/mask. **M1** (`MadCalibrationReducer` +
`NormFactorRecommender`) should set that factor from a real capture, but the capture is **partial (2 of 5
scenes), the factor is still 4.0, and the M1 gate was retired**. MAD is content-blind + app-agnostic.

- **C1. Finish M1 (foundational).** Complete the 5-scene capture (`SENSEEASE_MAD_CALIBRATION=1` → `baseline/`)
  on representative hardware; run `NormFactorRecommender`; apply the factor (manual edit to locked
  `SenseDefaults.WgcMadNormFactor`).
- **C2. Per-app/per-content norm-factor** instead of one global scalar (M1 already produces per-scene stats);
  same `WgcMadNormFactor` seam made category-keyed.
- **C3. Automate OFI calibration — recommended "degree ②" runtime self-calibration.** Adaptive gain control:
  accumulate **raw-MAD percentiles per app** (reuse `DailyBehaviorAccumulator` / `MadReducer`, tapped at the
  raw-MAD point the M1 capture already exposes); derive a **bounded per-user/per-app multiplier** mapping MAD
  onto the **same canonical 0-150 OFI scale**, layered as an *override* on the locked global anchor (parallel
  to `PersonalPressureScale`). Cold-start fallback + safety clamps + rate-limit; optionally validate via
  output ② (FMS effectiveness) = "degree ③". Degree ① interim: auto-collect + auto-recommend (app categories
  as scene labels), human applies.
- **C4. Upgrade the motion signal** (Farnebäck / learned flow vs MAD magnitude proxy) — Beta, higher effort.

---

## Cross-cutting constraints (B & C)

- Keep the downstream OFI scale (0-150) + trigger (85) stable, or co-tune; auto-calibration emits the canonical scale.
- Safety: OFI drives protection. Never auto-edit the **locked** clinical default — use bounded per-user
  overrides with clamps + rate-limiting + cold-start fallback; validate via FMS effectiveness.
- Privacy: everything per-app behind the existing **D-16 opt-in consent**; on-device; category-only.
- Each learning/calibration change re-tightens the "provisional until M1" selector constants.
- UVHS-gate every visible change; build + full test green each step; nothing committed without sign-off.

---

## Suggested sequencing

1. **A1–A4** — land the FMS/check-in arc.
2. **B1** — FMS → selector (keystone).
3. **C1** — finish M1.
4. **B2 + B3 + B4** — manual-nudge persistence; per-app bucketing; app-aware OFI.
5. **C3** — automate OFI calibration (degree ②).
6. **B5 + C4** — chatbot-analysis promotion; motion-signal upgrade (later).

---

## Verification (each step)

- `dotnet build SenseEase.Windows.sln -c Debug` → 0 errors; `dotnet test tests/SenseEase.Tests` → green
  (currently 1432/0/3) with new unit tests per step.
- Adversarial diff review per change; fix confirmed findings.
- UVHS visual confirmation for any visible surface; live `--diag` for OFI/calibration behavior.
- App must be closed before a clean build (the running dev build locks `SenseEase.App.exe`).
