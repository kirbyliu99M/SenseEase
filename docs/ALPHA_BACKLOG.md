# Audit Summary (30/30 items)

Source: 2-phase workflow `wiiwdwgld` against TECHNICAL_WHITEPAPER.md, 2026-06-04.

# Audit Summary Table

| Item | Title | Verdict | Action |
|------|-------|---------|--------|
| A1 | FovRadiusPercent default/clamp/composition | CODE-ONLY | Added composition formula + `[15, 100]` clamp + rest-state value to §8.2 |
| A2 | Pressure-to-radius mapping (piecewise-linear, not table) | CODE-ONLY | Added explicit pre/post-trigger linear formula to §8.2 and §6.4; corrected "tier table" misnomer |
| A3 | `ComputeMaskTightness` derivation | CODE-ONLY | Added tightness formula + single-consumer attenuation derivation to §5.8.3 |
| A4 | `GridVisibility` semantics + chatbot delta path | CODE-ONLY | Added grid-intensity formula, default `0.75`, role description to §8.3 |
| A5 | `FeatherWidth` clamp is actually `[0, 1]` not `[0, 0.5]` | REAL-GAP | Whitepaper §8.2 corrected to `[0, 1]` with default `0.50`; backlog item A5 |
| A6 | "600 ms animation" reference is wrong | CODE-ONLY | Replaced with actual 380/280/250/500 ms table sourced from `OverlayWindow.xaml.cs` |
| A7 | Shader axes not profile-differentiated | REAL-GAP | Documented as known scope in §2.5/§12; backlog item A7 (per-profile shader defaults) |
| A8 | Multi-monitor coverage limitation | REAL-GAP | Documented explicitly in §8.5 as MVP scope limit; backlog item A8 |
| B1 | Tier projection is linear formula, not "table" | CODE-ONLY | §6.4 updated with `levelF = 1 + 9·clamp(P/T, 0, 1)`, saturation note, hysteresis cross-ref |
| B2 | Habituation `_offset` is additive to FOV radius, not trigger | CODE-ONLY | §6.5 rewritten — `_offset` adds to FOV radius via `ComposeFovRadius`; trigger uses `EffectiveTrigger` separately |
| B3 | Habituation two-stage state machine | CODE-ONLY | §7.5 added — stability accumulator + step-interval ticker; profile cadence table; severe-strike zeroing |
| B4 | `MinSamplesForFullWeight` semantics | CODE-ONLY | §6.5 + §7.5 — confidence ramp denominator; asymmetric caution; `15.0` divisor |
| B5 | `SevereDebounceMs` does not exist | REAL-GAP | §7.4 corrected — no `SevereDebounceMs`; de-facto debounce via `_pauseCooldown`; backlog item B5 (rename/clarify) |
| B6 | `personal_pressure_scale` divides trigger, not pressure | CODE-ONLY | §6.5 + §7.4 + §11.6 expanded with `EffectiveTrigger = T/scale + adjustment`, scope qualifier (engine ignorant of scale), override precedence |
| B7 | `PausedAwaitingUserConfirm` parked/live table | CODE-ONLY | §7.4 added frozen-vs-live table; severe-strike still active; only `AcknowledgeAndResume` exits |
| C1 | Vestibular trigger=50 magic literal | REAL-GAP | Added to safety-floor constants table in §12; backlog item C1 (extract `VestibularTriggerPressureOverride`) |
| C2 | Crisis safety-floor execution differential | CODE-ONLY | Added subsection in §11.1 — call-site differential (Calibration ends session vs Chat continues); language pinning |
| C3 | Safety-floor drift + missing parity test | REAL-GAP | Documented full-width vs ASCII colon drift; backlog item C3 (parity test + extraction) |
| C4 | `CalibrationConfidence` is an unused enum | REAL-GAP | Documented in §11.6 as diagnostic-only field with no downstream consumer; backlog item C4 |
| C5 | `ResetPersonalization` violates Q4.5 | REAL-GAP | Mentioned in §11.8 as known violation; backlog item C5 (split `Reset` into two methods) |
| C6 | Three-rule precedence + per-rule × per-intent matrix gaps | REAL-GAP (PARTIAL) | §11.4.2 listed rules with precedence; backlog item C6 (9-cell matrix tests + precedence test) |
| C7 | No single-source step-magnitude table | REAL-GAP | §12.1.6 "Delta Bounds Table" added with severity→magnitude lookup; backlog item C7 (extract named constants) |
| D1 | No `baseline/` MAD/cursor-velocity → OFI calibration data | REAL-GAP | §13.5 calls out empirical gap; backlog item D1 (commit `wgc-mad-calibration.csv`, `cursor-velocity-calibration.csv`) |
| D2 | 8× Driver/Passenger ratio is direction-tested only | REAL-GAP | §6.3 + §13.5 flag the partial coverage; backlog item D2 (ratio assertion test) |
| D3 | No leaky-integrator 5τ convergence test | REAL-GAP | §6.3 + §13.5 flag the derivation-only status; backlog item D3 (4 convergence tests) |
| E1 | `Driver + MaskActive` non-stacking invariant untested | REAL-GAP | §5.8.3 documents the gate; backlog item E1 (assertion test) |
| E2 | Mask-attenuation arithmetic is constant-orphan in tests | REAL-GAP | §5.8.3 documents constants; backlog item E2 (range + tightness-scaling tests) |
| E3 | `SidecarFrameDto` schema lacks negative-property assertion | REAL-GAP | §3.2 + §2.6 mention OBS-3 schema; backlog item E3 (reflection-based schema test) |
| E4 | Tier-≥8 engine-level coverage only on default Resilient | REAL-GAP (PARTIAL) | §2.5 + §7.4 reference uniformity; backlog item E4 (`[Theory]` over 3 profiles × tier {8,9,10}) |
| F1 | 8 named values in §12.1.4 live OUTSIDE SenseDefaults | REAL-GAP | §12.1.4 now annotated; backlog item F1 (migrate 8 constants, fix `137` cosmetic bug, document Card≤Watchdog invariant) |
| F2 | 10+ load-bearing magic numbers should migrate | REAL-GAP | §12.1.5 lists Tier-2/Tier-3 candidates; backlog item F2 (10-item migration + literal-duplication audit) |


---

# SenseEase Alpha Backlog — Real Gaps from POC Whitepaper Audit

**Source:** 30-item audit of `docs/TECHNICAL_WHITEPAPER.md` against codebase, 2026-06-04. Phase labels updated 2026-06-05 per Kirby's phase-rename direction.
**Items flagged `[CODE-ONLY]`** were folded back into the whitepaper as documentation corrections.
**Items below are `[REAL-GAP]`** — they describe code, test, or constants work that engineering should pick up.

**Security backlog (H-series, prefix H):** maintained as a separate document — see [ALPHA_SECURITY_BACKLOG.md](ALPHA_SECURITY_BACKLOG.md). 53 H-series entries from the 2026-06-05 10-domain cybersecurity audit (workflow `w5ga7nd83`): 29 Alpha-1 must-fix (block tester ship) + 24 Alpha-2 should-fix. The top-3 critical items (H1 safety-floor bypass via profile-picker, H2 self-harm phrases persisted-before-floor, H3 missing Simplified Chinese / NFKC / youth-shorthand variants) are bundled into a single first-PR per the ordering rule documented there.

## Release-phase naming (current)

- **Pre-Alpha** (now, v0.11.x) — POC validation; this is where bugs surface from tester rounds.
- **Alpha** — first user-facing release (formerly internally called "Beta" in earlier planning). All items in this document are scheduled for the Alpha phase, sub-prioritized as `Alpha-1` / `Alpha-2` / `v1.0`.
- **Beta 1** — future phase, scope determined by POC + Alpha test outcomes. No pre-populated items at this time.
- **Beta 2** — future phase, scope TBD per same rule.

Priority key for the items below (all within the Alpha phase):
- **Alpha-1** — must-fix before Alpha cut; safety, correctness, or audit-trail integrity at risk.
- **Alpha-2** — should-fix during Alpha; quality / governance / spec-vs-code drift.
- **v1.0** — defer to v1.0 release; scope-expansion items.

**Cross-ID layout:** prefixes **A-G** = functional / spec-parity work in this file; prefix **H** = security work in [ALPHA_SECURITY_BACKLOG.md](ALPHA_SECURITY_BACKLOG.md). Both target the same Alpha cut.

---

## Alpha-1 (safety, correctness, audit integrity)

### C1 — Extract vestibular trigger override `50` to a named constant
**What's missing:** `CalibrationProcessor.cs:74` sets `triggerOverride = 50` as a raw literal. Two advisory strings (`CalibrationApplicator.cs:102-103, 106-107`) and five tests repeat the literal. Silent drift between processor and UI is possible.
**Fix:** Add `public const int VestibularTriggerPressureOverride = 50;` to `CalibrationProcessor` (alongside `VestibularPersonalPressureScaleCap`). Replace literal sites and the two `CalibrationApplicator` advisory strings to read from the constant. Add a single-line test that pins the value.
**Files:** `CalibrationProcessor.cs`, `CalibrationApplicator.cs`, plus tests in `CalibrationProcessorTests.cs`, `CalibrationApplicationTests.cs`, `CalibrationEndToEndSmoke.cs`, `CalibrationIntegrationTests.cs`.

### C3 — Safety-floor drift + parity test
**What's missing:** `MistralChatbotService.cs:892` uses ASCII colon `:` in zh crisis text; `CalibrationChatbotService.cs:330` uses full-width colon `：`. The two services' phrase tables are byte-for-byte identical but the drift admits the duplicate-copy pattern is already breaking. No drift-prevention test exists.
**Fix:**
1. Pick canonical punctuation (recommend full-width `：` to match the existing zh script convention) and update the diverged file.
2. Add `SafetyFloorParityTests.cs` with `Assert.Equal(CalibrationChatbotService.SafetyFloorPhrasesZh, MistralChatbotService.SafetyFloorPhrasesZh)` for both `Zh` and `En` arrays AND for the two crisis-response constants.
3. Optional: extract `SafetyFloorVocabulary` to a Core utility; both services reference it.
**Files:** `MistralChatbotService.cs`, `CalibrationChatbotService.cs`, new `tests/SenseEase.Tests/SafetyFloorParityTests.cs`.

### C5 — `ResetPersonalization` violates Q4.5
**What's missing:** `MockInferenceEngine.ResetPersonalization` (`MockInferenceEngine.cs:324-338`) calls `_habituation.Reset()` which zeros `_offset` — directly contradicting the Q4.5 locked decision ("chatbot reset must only clear shader, NOT habituation offset"). Zero tests pin this contract.
**Fix:**
1. Split `HabituationEngine.Reset()` into `ResetPostCalibration()` (zeros everything) and `ResetTransientState()` (clears strike-count + phase + accumulator only; preserves `_offset`).
2. Have `MockInferenceEngine.ResetPersonalization` call `ResetTransientState()`.
3. Add regression test that drives a non-zero `_offset`, calls `ResetPersonalization`, asserts `HabituationOffset` is unchanged.
**Files:** `HabituationEngine.cs`, `MockInferenceEngine.cs`, new test in `HabituationTests.cs`.

### E3 — `SidecarFrameDto` privacy schema test (OBS-3 enforcement)
**What's missing:** `HEAD-TRACKING-PRIVACY.md §2.5` lists "forbidden additions" as a manual code-review gate. A future change adding `[JsonPropertyName("landmarks")] public double[] Landmarks` would silently pass current tests.
**Fix:** Add `SidecarFrameDtoSchemaTests.cs` that uses reflection to:
1. Enumerate `SidecarHeadPoseReader.SidecarFrameDto` properties.
2. Assert exact allowlist of `[JsonPropertyName]` values: `{ "t_ms", "cx", "cy", "luma", "face_detected", "face_present", "yaw_deg", "pitch_deg" }`.
3. Assert each property is primitive/scalar (no arrays, no `byte[]`, no nested objects).
4. Assert no property name contains forbidden tokens: `landmark`, `nose`, `eye_`, `blendshape`, `frame`, `image`, `jpeg`, `b64`, `bytes`.
5. Add `InternalsVisibleTo` if not already configured.
**Files:** `SidecarHeadPoseReader.cs` (visibility), new `tests/SenseEase.Tests/SidecarFrameDtoSchemaTests.cs`.

### E1 — Driver + MaskActive non-stacking invariant
**What's missing:** `MockInferenceEngine.cs:220-225` gates the mask attenuation by `if (!isDriverMode && Current.MaskActive)`. No test asserts the Driver + MaskActive=true coexistence path. A future refactor flipping the gate would silently double-attenuate Driver-mode passive flow to as low as 0.0375×.
**Fix:** Add `InferenceTests.DriverMode_WithMaskActive_AppliesOnlyDriverAttenuation_NoExtraMaskAttenuation`:
1. Drive engine to `MaskActive = true` in Passenger mode first.
2. Switch to Driver mode and call `Update(ofi=32, "Driver", sampledAt)`.
3. Assert `state.OpticalFlowIndex == 8.0` exactly (32 × 0.25, no mask attenuation).
**Files:** `tests/SenseEase.Tests/InferenceTests.cs`.

### E4 — Tier-≥8 engine state per profile
**What's missing:** All four engine-level tier-9 tests in `HabituationStrikeAndStateTests.cs` run only against default-constructed (Resilient) engine. Tier 8 boundary is only hit at the param layer. A profile-specific regression at tier 8 in Typical or Sensitive would slip past the engine-level suite.
**Fix:** Convert `Tier9Spike_EntersPausedAwaitingUserConfirmAndStays` to a `[Theory]` parameterised over `HabituationProfile × {tier 8, 9, 10}`. Also add tier-7 negative-control parameterised over all three profiles, asserting `EngineState.Cooldown` (not `PausedAwaitingUserConfirm`).
**Files:** `tests/SenseEase.Tests/HabituationStrikeAndStateTests.cs`.

### F1 — Migrate 8 named values in §12.1.4 to `SenseDefaults`
**What's missing:** Per §12.1.4 of the whitepaper, 8 values live OUTSIDE `SenseDefaults` (`PersonalPressureScaleMin/Max` is even duplicated across two Core/Inference files). Includes a latent bug: `HabituationOverrideProcessor.cs:137` rejection message says `"{ExpiresAtMaxFutureDays} days"` when it should reference the cooldown days (both equal 7 today, but they're two policies).
**Fix:**
1. Add to `SenseDefaults.cs`: `PersonalPressureScaleMin/Max`, `SevereStrikeLimitMin/Max`, `ProposalExpiresAtMaxFutureDays`, `ChatbotProposalExpiryTargetHours`, `ChatbotProposalRecentDeclineCooldownDays`, `NotificationCardCountdownSec`, `ProposalExpiryWatchdogSec`, `HighRiskComboSignificantRelaxationThreshold`.
2. Update consumers: `CalibrationProcessor.cs:25-26`, `HabituationOverrideProcessor.cs:49-64`, `NotificationCardWindow.xaml.cs:186`, `MainViewModel.cs:267, 299`, `HabituationOverrideApplicator.cs:172`, `HabituationProposalLexicon.cs:47`, `MistralChatbotService.cs:128`.
3. Fix `HabituationOverrideProcessor.cs:137` cosmetic bug.
4. Document `NotificationCardCountdownSec ≤ ProposalExpiryWatchdogSec` invariant in `SenseDefaults` doc-comment as a Phase 5 Step 7 handshake.
**Files:** as above.

---

## Alpha-2 (governance, spec parity, coverage)

### A5 — Whitepaper claimed FeatherWidth clamp `[0, 0.5]`, actual is `[0, 1]`
**What's missing:** `ShaderParams.cs:28-33` clamps `FeatherWidth` to `[0, 1]`. The `0.5` value the prior whitepaper referenced is the *default* (`DefaultFeatherWidth`), not an upper bound. Given per-turn cap of `±0.3`, two "softer edges" turns already exceeds the claimed ceiling.
**Fix (one of):**
- (preferred) Accept the actual `[0, 1]` bound; whitepaper §8.2 already corrected. No code change. Add a doc-comment on `FeatherWidth` clarifying.
- (alternative, if `[0, 0.5]` was a design intent) Tighten clamp to `[0, 0.5]` in `ShaderParams.cs:32`. Backfill regression tests pinning new bound.
**Recommendation:** Take the preferred path — there is no source-tree evidence the `0.5` ceiling was ever an intentional invariant.
**Files:** `ShaderParams.cs` (doc-comment only if preferred path).

### A7 — Per-profile shader defaults
**What's missing:** `MockInferenceEngine._shader = ShaderParams.Default` (`MockInferenceEngine.cs:19`) is a single shared instance. `SetHabituationProfile` does not re-seed it. A SENSITIVE-profile user gets the exact same peak `MaskDepth = 0.74 / GridVisibility = 0.75 / FeatherWidth = 0.50` as a RESILIENT user.
**Fix:** Add `ShaderParams ResolveDefaultsFor(HabituationProfile)` to `HabituationProfileParams`. On profile change, re-seed `_shader` from the profile's defaults unless user-personalised. Tighten Sensitive defaults (suggestion: `MaskDepth = 0.80, GridVisibility = 0.60, FeatherWidth = 0.55`).
**Files:** `HabituationProfile.cs`, `MockInferenceEngine.cs`, new test pinning Sensitive vs Resilient shader-defaults divergence.

### B5 — `SevereDebounceMs` rename / clarify
**What's missing:** Prior whitepaper referenced a `SevereDebounceMs` constant — no such field exists. Severe strikes are de-facto debounced by `_pauseCooldown` (`PostSpikePauseLowMs` / `PostSpikePauseMidMs` / `∞` at tier ≥ 8). Whitepaper §7.4 is corrected.
**Fix (optional but recommended):** Add an explicit comment block at `HabituationEngine.cs:273-283` documenting "Severe strikes are NOT time-debounced; the post-spike pause provides effective debouncing." Prevents a future engineer from adding a redundant explicit debounce.
**Files:** `HabituationEngine.cs` (comment only).

### C4 — `CalibrationConfidence` is dead weight or must gain a consumer
**What's missing:** `CalibrationConfidence` is computed in `CalibrationProcessor.cs:297-320` and persisted on `HabituationProfileSnapshot.cs:177`. Zero consumers branch on it. The `CalibrationProcessor.cs:306` comment promises "chatbot can probe in subsequent sessions" — but no chatbot code does.
**Fix (pick one):**
- (A) Wire a downstream effect: surface a "Recalibrate" banner when `CalibrationConfidence == Low`; OR halve `personal_pressure_scale` blending weight on Low; OR annotate the chatbot context block with `[low_confidence_calibration]`.
- (B) Document in §11.6 + add tests pinning the contract that the field is diagnostic-only with no behavioural effect. Remove the misleading inline comment at line 306.
**Recommendation:** (A) — wire the chatbot context-block annotation; it's the lightest-touch behavioural use of the field.
**Files:** `HabituationStateBlockBuilder.cs`, `CalibrationProcessor.cs` (comment cleanup), tests.

### C6 — Three-rule × three-intent matrix tests
**What's missing:** `HabituationOverrideApplicator.CheckSafetyIntercepts` has three rules. Tests cover only a subset: HighRiskCombo + AdjustStrikeLimit not tested; HighRiskCombo + SwitchProfile not tested; vestibular + strike-limit-2 (negative for Rule 1) not tested; no precedence-order test.
**Fix:** Add per-rule × per-intent matrix tests (3 rules × 3 intents = up to 9 cells minimum). Mark logically-unreachable cells as Skip with explanation. Add a precedence test: a proposal satisfying multiple rules must fire the most-specific rule's reason string.
**Files:** `tests/SenseEase.Tests/HabituationOverrideApplicatorTests.cs`.

### C7 — Single-source step-magnitude table
**What's missing:** Severity → magnitude mapping (`-6/-10/-15/-22/-30` for FOV, `0.05/0.08/0.12/0.18/0.25` for MaskDepth, etc.) lives only as literal switch arms in `FeedbackLexicon.cs:208-210`. The two-level clamp system (per-call vs accumulated) is not documented. The directional-hint-beats-severity precedence is undocumented.
**Fix:**
1. Add named constants to `SenseDefaults`: `MaxFovDeltaPerCall = -30`, `MaxUserFovOffsetAccumulated = -60`, `MaxShaderDeltaPerCall = 0.3`, `MinComposedFovRadius = 15`.
2. Replace `FeedbackLexicon.cs:208-210` switch arms to reference named tuples (or an array constant) on `SenseDefaults`.
3. Add tests pinning per-severity magnitudes.
4. Whitepaper §12.1.6 already documents the table.
**Files:** `SenseDefaults.cs`, `FeedbackLexicon.cs`, `MistralChatbotService.cs:606-609` (mirror clamp values), tests.

### D1 — Commit `baseline/wgc-mad-calibration.csv` and `baseline/cursor-velocity-calibration.csv`
**What's missing:** `WgcMadNormFactor = 4.0` and `CursorVelocityNormFactor = 120.0` are inline-comment hypotheses; no measured "content → OFI" calibration table exists. `SenseDefaults.cs:40` comment "Tune after first real WGC data" is an open TODO.
**Fix:**
1. Add `baseline/wgc-mad-calibration.csv` with schema `scene_label,duration_s,mean_mad,p95_mad,derived_ofi_mean,derived_ofi_p95`. Populate via instrumented WGC run: idle desktop, Word scroll, 1080p video, fullscreen game, scene-cut clip.
2. Add `baseline/cursor-velocity-calibration.csv` with schema `gesture_label,distance_px,duration_ms,derived_speed_pxs,derived_ofi`. Populate via canned cursor traces.
3. Add `WgcSensingProviderCalibrationTests` — promote `ComputeMad` to `internal` + `InternalsVisibleTo`, feed canned greyscale arrays, assert OFI sits in documented bands.
4. Update `SenseDefaults.cs:40` comment from "Tune after first real WGC data" to "Calibrated against `baseline/wgc-mad-calibration.csv` (commit X)".
**Files:** new `baseline/wgc-mad-calibration.csv`, new `baseline/cursor-velocity-calibration.csv`, new `tests/SenseEase.Tests/WgcSensingProviderCalibrationTests.cs`, `WgcSensingProvider.cs` (visibility), `SenseDefaults.cs` (comment).

### D2 — Pin the 8× Driver/Passenger ratio
**What's missing:** `InferenceTests.PassengerMode_BuildsPressureFasterThanDriverMode` pins direction only. Either half of the ratio (`0.25`, `2.0`) could silently drift.
**Fix:**
1. Promote literal `0.25` at `MockInferenceEngine.cs:217` to `SenseDefaults.DriverFlowAttenuation = 0.25`.
2. Add `InferenceTests.PassengerDriverPressureRatio_Is8x_AtSteadyState` — drive each engine to equilibrium at the same input OFI, assert `passengerP / driverP ∈ [7.5, 8.5]`.
3. Add `InferenceTests.DriverAttenuationConstantIs0_25` — assert `Update(OFI=32, "Driver")` produces `OpticalFlowIndex == 8.0` exactly.
4. Add `InferenceTests.PassengerFlowWeightIs2_0` — Passenger pressure equals Driver pressure × 8 within tolerance.
**Files:** `SenseDefaults.cs`, `MockInferenceEngine.cs`, `tests/SenseEase.Tests/InferenceTests.cs`.

### D3 — Leaky-integrator convergence tests (5τ → trigger)
**What's missing:** The "OFI 30 sustained ~14 s reaches TriggerPressure 85" derivation is prose-only. No test references `tau`, `5τ`, `14s`, or `2.81`. A refactor changing `BaseDecayAlpha`, the dt-scaling exponent, `QuadraticTerm`, or the integrator update order would silently invalidate the rule of thumb.
**Fix:** Add `tests/SenseEase.Tests/LeakyIntegratorConvergenceTests.cs` with:
1. `SustainedOfi30_Passenger_ReachesTrigger_Within14s` — drive `Step(30, 2.0, dt=0.066)` for ≈212 ticks at 15 Hz; assert pressure ≥ 85 within `[10s, 18s]` band.
2. `EquilibriumPressureMatchesAnalytic` — assert post-5τ pressure within 5% of analytic `P* ≈ 173.5`.
3. `TimeConstantIsRateInvariant` — same trajectory at 1/15/30 Hz; pressures agree within 1% at 5-second mark.
4. `OneSecondSpike_DoesNotReachTrigger` — g=30 for 1s then g=0 for 30s; assert peak pressure < 0.5 × TriggerPressure and decays to near-zero.
**Files:** new `tests/SenseEase.Tests/LeakyIntegratorConvergenceTests.cs`.

### E2 — Mask-attenuation arithmetic range + tightness-scaling tests
**What's missing:** Constants `0.45`, `0.15`, `0.30` in `MockInferenceEngine.cs:220-225` are test-orphans. A typo flipping the sign or swapping clamp bounds would not break any test.
**Fix:** Add to `InferenceTests.cs`:
1. `PassengerMode_WithMaskActive_AttenuatesOfiBetween15And45Percent` — drive engine to `MaskActive=true` in Passenger, assert next `Update(ofi=100)` produces `OpticalFlowIndex ∈ [15, 45]`.
2. `PassengerMode_WithMaskActive_AttenuationScalesWithFovTightness` — at tight FOV, attenuation near 0.15; at loose FOV, near 0.45.
**Files:** `tests/SenseEase.Tests/InferenceTests.cs`.

### F2 — Migrate 10+ load-bearing magic numbers to `SenseDefaults`
**What's missing:** Tier-2 (UX timing) and Tier-3 (cross-layer literal duplication) constants scattered across 6+ files. Notable duplicates: `0.25` mask smoothing, `0.85/1.10` brush radius baseline, `0.35/0.95/8` ramp geometry (duplicated in two methods!), `OFI / 120.0` normalisation (already in SenseDefaults as `CursorVelocityNormFactor`), `ChartTriggerLine = 85.0` (duplicates `TriggerPressure`).
**Fix:** Migration list (whitepaper §12.1.5 documents the target). Highest priority:
1. Replace `OverlayWindow.xaml.cs:699` (`/ 120.0`) with `SenseDefaults.CursorVelocityNormFactor` reference.
2. Replace `MainViewModel.cs:889` `ChartTriggerLine` literal `85.0` with `SenseDefaults.TriggerPressure`.
3. Migrate `MaskSmoothingFactor`, `BrushRadius`, `BrushRadiusCornerMargin`, `GridIntensityEmaAlpha`, `GridIntensityFlowWeight/PressureWeight`, animation durations (`UiCardFadeInMs`, `UiOverlayFadeInMs`, `UiGridAnimMs`, `UiBarHoverMs`, `UiBarProximityPollMs`), `CardIdleTimeoutSec`, `CardFadeDurationSec`, `ChatbotContextWindowTurns`, `ChatbotPersistedHistoryMax`, `ChartYMax`, `ChartHistoryMax`.
4. Document the rule in `SenseDefaults.cs` header: **any tuning constant referenced by 2+ files MUST live in SenseDefaults**.
**Files:** `SenseDefaults.cs`, `OverlayWindow.xaml.cs`, `MainWindow.xaml.cs`, `MainViewModel.cs`, `ChatbotViewModel.cs`, `NotificationCardWindow.xaml.cs`, `PersistentBarWindow.xaml.cs`.

### G1 — Strict-language + slash-command path for shader parameters

**What's missing (today's chatbot can't satisfy "grid off" or absolute-set requests):**

The current chatbot → engine pipeline is **delta-only** end-to-end:

1. `IInferenceEngine.ApplyShaderAdjustment(maskDepthDelta, gridVisibilityDelta, featherWidthDelta)` ([IInferenceEngine.cs:34](src/SenseEase.Core/Abstractions/IInferenceEngine.cs#L34)) accepts deltas only — there is no `SetShaderParam(name, value)` or `SetShaderAbsolute(ShaderParams)` companion.
2. `MistralChatbotService` JSON envelope advertises `grid_visibility_delta` (and the three siblings) — system prompt at [MistralChatbotService.cs:49](src/SenseEase.Infrastructure/Real/MistralChatbotService.cs#L49) instructs the LLM in delta semantics, and the per-turn clamp at `[-0.3, +0.3]` is enforced in `MistralChatbotService.cs:606-609`.
3. `MockInferenceEngine.ApplyShaderAdjustment` ([MockInferenceEngine.cs:313](src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs#L313)) then clamps the result to `[0, 1]` per `ShaderParams.cs:28-33`.

**Consequence:** A user request like *"turn the grid off"* / *"make it fully transparent"* / *"把格線完全關掉"* is parsed by the LLM into at most `grid_visibility_delta = -0.3` per turn. Reaching `GridVisibility = 0` from the default `0.75` takes **at least 3 round-trips** — and each round-trip waits 5-30 s on Iris Xe iGPU (per [HANDBOOK_zh-Hant.md:154](docs/HANDBOOK_zh-Hant.md#L154)). In practice the user gives up before convergence and assumes the feature is broken. This is the exact gap the user flagged: *"I can't seem to make it work like a charm."*

A second related gap: there is **no pre-LLM interceptor** for high-confidence parameter commands. The codebase has the precedent — `HabituationProposalLexicon.TryDetectProfilePickerRequest` is invoked at [ChatbotViewModel.cs:311](src/SenseEase.App/ViewModels/ChatbotViewModel.cs#L311) before any LLM call when the utterance unambiguously asks for the profile picker — but nothing analogous exists for shader parameters or slash commands.

**Proposed mechanism — three layers stacked in priority order:**

```
SendAsync(text)
  ├─[1]─ SafetyFloorPhrasesZh/En short-circuit  (existing; do not touch)
  ├─[2]─ TryDetectProfilePickerRequest           (existing; do not touch)
  ├─[3]─ TryParseSlashCommand(text)              (NEW — explicit syntax wins)
  ├─[4]─ TryDetectStrictShaderPhrase(text)       (NEW — high-confidence NL whitelist)
  └─[5]─ LLM round-trip via NLU                  (existing; unchanged for ambiguous input)
```

Layers 3 and 4 both resolve to a single internal action type — a `ShaderCommand { Param, Mode (Absolute|Delta|Reset), Value }` — fed to a new engine method below. **Both layers are absolute-set capable** (slash commands explicitly; strict NL by mapping `"off"` → `0`, `"on"` → profile default, etc.). Both bypass the `[-0.3, +0.3]` per-turn delta cap because their semantics are *the user just told us exactly what they want*, not *the LLM inferred a small adjustment from vague language*.

**New API surface on `IInferenceEngine`:**

```csharp
// IInferenceEngine.cs — add alongside ApplyShaderAdjustment at line 34
//
// Absolute-set for a single shader parameter. Value MUST be pre-validated
// (in [0, 1] and not violating safety floors — see §G1 safety constraints).
// Caller is responsible for safety checks; the engine does final clamp only.
void SetShaderParam(ShaderParam param, double value);

// Reset shader to profile defaults. MUST NOT touch HabituationProfileSnapshot.Offset
// — that is Q4.5 (chatbot reset clears shader values only, never habituation offset).
// See Alpha-1 item C5 for the related ResetPersonalization bug.
void ResetShaderToProfileDefault();

// New enum (in SenseEase.Core.Models)
public enum ShaderParam { FovRadius, MaskDepth, GridVisibility, FeatherWidth }
```

`MockInferenceEngine` implementation is ~15 lines: switch on `param`, write the field, fire the existing `ShaderUpdated` event. `ResetShaderToProfileDefault` reads `HabituationProfileParams.ResolveDefaultsFor(currentProfile)` once Alpha-2 A7 lands; until then, falls back to `ShaderParams.Default`. **Critically does not call `_habituation.Reset()` or zero `_offset`** — that conflation is the C5 bug being fixed in Alpha-1.

**Slash command surface (canonical grammar):**

| Command | Effect | Bounds & safety |
|---|---|---|
| `/grid off` | `SetShaderParam(GridVisibility, 0)` | unrestricted (grid is opt-in visual) |
| `/grid on` | `SetShaderParam(GridVisibility, profile default ≈ 0.75)` | — |
| `/grid <0-100>` | `SetShaderParam(GridVisibility, n/100)` | rejected if outside `[0, 100]` |
| `/mask <0-100>` | `SetShaderParam(MaskDepth, n/100)` | **lower-bounded at safety floor (see below)** |
| `/feather <0-100>` | `SetShaderParam(FeatherWidth, n/100)` | unrestricted |
| `/fov <15-100>` | absolute-set FOV radius percent | clamped to `[MinComposedFovRadius=15, 100]` |
| `/reset shader` | `ResetShaderToProfileDefault()` | **does NOT touch habituation offset (Q4.5)** |
| `/help` | prints command list inline as assistant reply | local only, no LLM call |
| `/show` | prints current shader values inline | local only, no LLM call |

**Safety floor for `MaskDepth` and grid disabling:** mask depth is the *core* visual protection. A naive `/mask 0` would defeat the whole product. Two options:

- **(A, recommended)** Hard-floor at `MaskDepth ≥ 0.20`. Reject `/mask <20` with `"Mask depth below 20% disables the core protection — refusing. Use /grid off if you want to hide the grid only."`
- **(B)** Allow `/mask 0` only when paired with a confirmation slash-command (`/mask 0 confirm`). Heavier UX; only justified if a tester actually needs full-mask-off for evaluation.

Grid (Chang-Hain pattern) is *additive* visual aid, so `/grid off` is unconditionally safe — the user simply turns off a single overlay element. The user's request *"the Chang-Hain grid can also be turned off via full transparency"* is satisfied by either `/grid off` or strict-language `"grid off"` / `"關掉格線"` / `"hide the grid"`.

**Strict-language pattern whitelist** (lives in new `ShaderCommandLexicon` class, mirroring `HabituationProposalLexicon`):

```
en:                                   zh:
  "grid off"  / "hide grid"             "關掉格線" / "隱藏格線" / "格線關掉"
  "grid on"   / "show grid"             "打開格線" / "顯示格線"
  "mask off"  (→ rejected, suggest      "拿掉遮罩" / "關掉遮罩" (→ rejected,
              /mask 20 alternative)     suggest /mask 20)
  "reset shader" / "reset overlay"      "重設遮罩" / "重置畫面"
  "show settings" / "what are my        "目前設定" / "現在的設定"
        current settings"
```

Patterns are **whole-utterance** (with light trim/lowercase) — not substring — to avoid intercepting *"I don't want the grid off, why is it gone?"*. Implementation pattern: ordered list of `(regex, ShaderCommand)` tuples; first match wins; no match → fall through to LLM. Add 10-15 patterns initially; tests pin every pattern and every rejection case.

**UX feedback (replies are local, not LLM-generated):**

| Outcome | Reply (en) |
|---|---|
| `/grid off` succeeded | "Grid hidden. Use `/grid on` to bring it back." |
| `/mask 15` rejected by safety floor | "Mask depth below 20% disables core protection — refusing. The lowest safe value is `/mask 20`." |
| `/fov 150` rejected by clamp | "FOV must be between 15 and 100. Try `/fov 60` for a softer view." |
| `/help` | bulleted command list with one-line summaries |
| `/show` | `"FOV: 60% · Mask: 74% · Grid: 75% · Feather: 50%"` |

Replies bypass the LLM entirely → instant feedback even on Iris Xe, addresses the user-visible latency frustration that prompted the issue.

**Logging:**

Each accepted command emits an `IEventLogStore` entry under a new `Chatbot.SlashCommand` or `Chatbot.StrictLanguage` category, with payload `{ pattern_id, resolved_param, mode, old_value, new_value }`. Rejected commands also log (with reason) so we can see in beta telemetry which commands testers reach for and which safety blocks fire. **No PII in payload** — store `pattern_id` only, never raw utterance, to preserve the project-wide no-PII-in-logs invariant.

**Safety constraints (load-bearing):**

1. **Mask cannot be disabled.** Per the safety-floor rationale above — block `/mask 0`...`/mask 19` and the strict-language `"mask off"` variants. Mask is the core VIMS protection.
2. **Reset must NOT touch habituation offset.** `/reset shader` and `"reset overlay"` map to `ResetShaderToProfileDefault()`, not `_habituation.Reset()`. This is Q4.5 (locked decision: chatbot reset clears shader values only) and exactly the C5 Alpha-1 bug — landing G1 *after* C5 ensures the new API can't accidentally regress the fix.
3. **No habituation profile changes via slash command.** Profile switching is intentionally LLM-mediated through the proposal/confirm flow — bypassing it would let testers thrash profiles without the 7-day re-emission gate and without recording `ChatbotActionLog` entries. `/profile sensitive` is therefore **not** in the command set; if the user asks the LLM does the routing.
4. **Slash commands are case-insensitive, single-line, leading-token only.** `/grid off please` → reject (extra args) rather than silently ignoring. Failing closed is cheaper than ambiguity.
5. **No nested commands and no chaining.** `/grid off; /mask 30` → reject. One command per send.
6. **Strict-language patterns must be whole-utterance** to prevent false positives on questions / negations.

**Discovery:** mention `/help` in the bilingual handbook ([HANDBOOK_zh-Hant.md:144-186](docs/HANDBOOK_zh-Hant.md#L144-L186)), and add a one-shot welcome-message hint *"Tip: type /help for instant commands."* when the chat tab is opened for the first session.

**Files to touch:**

| File | Change |
|---|---|
| [src/SenseEase.Core/Abstractions/IInferenceEngine.cs](src/SenseEase.Core/Abstractions/IInferenceEngine.cs) | Add `SetShaderParam` + `ResetShaderToProfileDefault` |
| [src/SenseEase.Core/Models/](src/SenseEase.Core/Models/) | New `ShaderParam` enum |
| [src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs](src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs) | Implement the two new methods (~15 lines each) |
| `src/SenseEase.Core/Inference/ShaderCommandLexicon.cs` (NEW) | Slash parser + strict-language pattern table |
| [src/SenseEase.App/ViewModels/ChatbotViewModel.cs](src/SenseEase.App/ViewModels/ChatbotViewModel.cs) | Insert layers 3 + 4 between line 311 short-circuit and the LLM path; add local reply helpers |
| [docs/HANDBOOK_zh-Hant.md](docs/HANDBOOK_zh-Hant.md) | Section 7 update — document the slash commands |
| [docs/TECHNICAL_WHITEPAPER.md](docs/TECHNICAL_WHITEPAPER.md) §13.6 | One-line mention of G1 under Alpha-2 |
| `tests/SenseEase.Tests/ShaderCommandLexiconTests.cs` (NEW) | Every grammar branch + every safety rejection |
| `tests/SenseEase.Tests/ChatbotViewModelTests.cs` (or new) | Integration: `/grid off` → engine called, LLM NOT called; ambiguous input → LLM called |

**Effort estimate:** ~8-12 hrs total —
- Lexicon + slash parser: 3 hrs (incl. pattern whitelist for both languages)
- Engine API + impl: 1 hr
- ChatbotViewModel wiring + local replies: 2 hrs
- Tests (≥ 30 cases — every pattern, every rejection, every safety floor): 3 hrs
- Handbook + whitepaper updates: 1 hr
- Manual verification on iGPU machine + dGPU machine: 1 hr

**Dependencies & ordering:** Land *after* C5 (Q4.5 fix) so the new `ResetShaderToProfileDefault` API can't silently regress the habituation-offset preservation invariant. Independent of all other Alpha-2 items; can interleave with C4/C6/C7 etc.

**Cross-reference:** addresses the user-reported gap *"Is it possible that the chatbot can make the overlay's grid to fully disabled or invisible using the chatbot? I can't seem to make it work like a charm"* (session 2026-06-04). Whitepaper §13.6 Alpha Roadmap should add a one-line entry under Alpha-2.

**Files:** see table above.

---

## v1.0 (scope expansion)

### A8 — Multi-monitor coverage
**What's missing:** `OverlayWindow.xaml:11` uses `WindowState="Maximized"` (maximises on launch monitor only). `OverlayWindow.xaml.cs:232` uses `SystemParameters.PrimaryScreenWidth` for DPI grid scaling. No per-monitor enumeration, no Display-Changed event handler, no per-monitor DPI awareness beyond primary.
**Fix:**
1. Enumerate `Screen.AllScreens` at startup.
2. Instantiate one `OverlayWindow` per screen.
3. Per-monitor `(cx, cy)` centring driven by head-pose monitor selection (currently single-display only).
4. Subscribe to `SystemEvents.DisplaySettingsChanged`; re-create overlays on monitor topology change.
5. Per-monitor DPI awareness for grid scaling.
**Files:** `OverlayWindow.xaml`, `OverlayWindow.xaml.cs`, `MainViewModel.cs` (instance management), new `MonitorOverlayCoordinator` class.

---

*End of backlog. 18 REAL-GAP items captured across 6 audit categories.*
