> **[LEGACY]** Superseded by [Beta-1.md](../Beta-1.md) (2026-06-14). Retained for historical reference / back-trace; not the current plan.

> **Advisor‑audit document** — generated 2026‑06‑07 from a 6‑agent read‑only map of the
> current habituation engine (offset earning, adjustment inputs, spike/strike, chatbot/
> profile coupling, persistence/safety, locked decisions). Every claim is grounded in
> `file:line`. Section 4 lists the open concerns Kirby flagged for advisor scrutiny.

# SenseEase — Habituation Adjustment Plan (Current State)

## 1. One-Axis Overview

Habituation sits orthogonal to the VIMS pressure / 12-tier intensity system. The tier (1–12) is pressure-discretized only: `VimsTier = Clamp(1 + (Pressure/EffectiveTrigger) × 9, 1, 12)`, never driven by habituation state. Conversely, the habituation offset (`_offset`) is a **FOV personalization loop**, purely additive to the pressure-driven radius in the composition: `ComposeFovRadius = Clamp(ComputeFovRadiusPercent(pressure) + _userFovOffset + _habituation.HabituationOffset, 15, 100)`.

The two are **~90% decoupled** with a single, narrow **cross-axis coupling point**: `GetPostSpikePauseMs(vimsTier, out bool requireUserConfirm)` in HabituationProfile.cs (line 197–206). When a severe spike fires, the engine reads the current tier to determine pause length (tier ≤6 → low pause, tier 7–9 → mid pause, tier ≥10 → infinite pause requiring user confirm). This is the only bridge. No habituation earning logic, strike accounting, or offset magnitude depends on tier.

---

## 2. How the Adjustment Works Today

### Offset Earning (Relaxation Loop)

**Mechanism**: Offset accumulates in discrete steps when the user sustains calm. Each tick in `HabituationEngine.Step()` (100 ms in production):

- If `pressure < 25% × TriggerPressure` (default: <21.25) **and** mask is not active, `_stableElapsed` accumulates `dt`.
- Otherwise, `_stableElapsed` decays at 0.5 per second (`HabituationMidBandDecayRate`).
- When `_stableElapsed >= _effectiveWindow`, the engine phase shifts to *Relaxing*, the strike counter resets (line 326), and `_stepAccumulator` begins accumulating banked seconds.
- When `_stepAccumulator >= _effectiveStepInterval`, one step fires: `_offset += _params.StepMagnitudePp`, clamped to profile cap.

**Profile-Dependent Parameters** (HabituationProfile.cs:138–175):
- **RESILIENT**: StableWindow=120s, StepInterval=30s, StepMagnitude=2.0pp, HabOffsetCap=30pp.
- **TYPICAL**: StableWindow=180s, StepInterval=45s, StepMagnitude=1.5pp, HabOffsetCap=25pp.
- **SENSITIVE**: StableWindow=360s, StepInterval=60s, StepMagnitude=1.0pp, HabOffsetCap=15pp.

**Effective Window Personalization** (HabituationEngine.cs:411–429): User caution (derived from adjustment-log `MeanDelta` and minimum sample thresholds) scales the effective window and step interval: `effectiveWindow ∈ [nominalWindow, 2× nominalWindow]`, `effectiveStepInterval ∈ [nominalInterval, 75s max]`. Refresh cadence: every 30s (SenseDefaults.cs:83, HabituationEngine.cs:1251).

**Offset Composition**: Final FOV = `pressure-driven radius + _userFovOffset [-60, 0] + _habituation.HabituationOffset [0, cap]`, clamped [15, 100]% (MockInferenceEngine.cs:1241–1243).

---

### Adjustment Inputs: Severity → Magnitude

**User Nudges (Deliberate Preference)**:
- `MainViewModel.EaseView` / `WidenView` calls `ApplyUserFeedback(fovDelta, isDeliberatePreference: true)`.
- **Gate at line 419** of MockInferenceEngine.cs: if `isDeliberatePreference=true`, `NotifyDiscomfort` is NOT triggered; the offset is preserved, and only `_userFovOffset` adjusts (clamped [-60, 0]).
- No strike counter increment, no pause entry.
- This is the **Q4.5 invariant**: deliberate adjustments do not erode long-term habituation progress.

**Discomfort Reports (Complaint-Driven)**:
- Chatbot feedback with `fovDelta < 0` and `isDeliberatePreference=false` routes through `NotifyDiscomfort(severe)`.
- **Severity threshold** (HabituationEngine.cs:421): if `|fovDelta| >= HabituationSevereFovThreshold (18pp)`, classified as severe; otherwise, mild.

**Mild Discomfort** (`severe=false`):
- Offset decremented: `_offset -= 4.0pp` (HabituationMildBackoffPercent, SenseDefaults.cs:85, HabituationEngine.cs:373).
- Pause entered (line 374).
- **No strike counter increment** — mild events do not trigger the strike-limit zero.

**Severe Discomfort** (`severe=true`):
- Calls `RegisterSevereStrike(weight=0.5)` (chatbot is softer than sensor, per Q4).
- Sensor-derived spikes use weight=1.0 (line 304).

---

### Strike Logic: Halving and Zeroing

**Offset Backoff**:
1. When a strike fires and `_severeStrikeCount < SevereStrikeLimit - epsilon`:
   - `_offset *= 0.5` (halve on strike, line 401).
   - Increments `_severeStrikeCount` by weight (1.0 sensor, 0.5 chatbot).

2. When `_severeStrikeCount >= SevereStrikeLimit - 1e-9`:
   - `_offset = 0` (zero on overflow, line 396).
   - `_severeStrikeCount` resets to 0.

**Profile Strike Limits**:
- **RESILIENT/TYPICAL**: limit=2.0 (4 chatbot events at weight 0.5 each, or 2 sensor spikes).
- **SENSITIVE**: limit=1.0 (first strike post-reset zeros offset immediately).
- Override via `SevereStrikeLimitOverride` (e.g., vestibular diagnosis sets limit=1 unconditionally, line 390).

**Strike Counter Reset Paths**:
- When `_stableElapsed >= _effectiveWindow` (full calm window passes, line 326): earned a "clean slate".
- On profile change (line 221): strikes reset, profile-scoped per Q6.
- On `ResetTransientState()` (line 470): transient session state cleared, but **offset is deliberately preserved** (Q4.5).

---

### Spike Detection and Post-Spike Pause

**Spike Recurrence (Sensor-Derived)**:
- **Edge-triggered** (not level-triggered, EM1 defense): `_pressureWasAtRecurrence` state flag (line 65, 301–307).
- Condition: `pressure >= 60% × TriggerPressure` (HabituationRecurrencePressureFraction=0.60, line 272).
- Fires only on the rising crossing, preventing strike storms during sustained high pressure.

**Pause Entry**: `EnterPause(vimsTier)` (line 494–510):
- Resets `_stableElapsed=0` and `_stepAccumulator=0` (suppress earning during recovery).
- Looks up pause duration from profile via `GetPostSpikePauseMs(tier)`:
  - **tier ≤6**: PostSpikePauseLow (90s RESILIENT/TYPICAL, 180s SENSITIVE).
  - **tier 7–9**: PostSpikePauseMid (120s RESILIENT, 180s TYPICAL, 300s SENSITIVE).
  - **tier ≥10**: returns -1, sets `_awaitingUserConfirm=true`, `_pauseCooldown=PositiveInfinity`.
- Sets phase=*Paused* (line 509).

**Pause Guard** (lines 280–285): While `_pauseCooldown > 0` or `_awaitingUserConfirm`, engine returns early from `Step()` after updating the spike-state flag. No further strikes can fire, no offset changes, no accruals.

**High-Tier Resume**: `AcknowledgeAndResume()` (line 239–246) clears both flags and resets `_stableElapsed` + `_stepAccumulator`, forcing calm re-confirmation before earning resumes.

---

### Persistent Storage

**Offset only** (legacy HabituationState, file `habituation.json`):
- Loaded on boot and restored via `Restore(state)` (line 438–445), clamped to [0, profile.HabOffsetCapPp].
- Never re-read during the session; updates persist to RAM.
- Transient state (`_stableElapsed`, `_stepAccumulator`, `_pauseCooldown`, `_severeStrikeCount`) **all reset on load** (line 441–442).

**Profile Snapshot** (v2 schema, file `habituation_profile.json`):
- Comprehensive JSON (schema_version=2) written atomically (temp + File.Replace).
- Carries calibration metadata, longitudinal observations, personal pressure scale, and medical advisory fields.
- Updated on significant state changes via `MaybePersistProfileSnapshot()` (dirty=true when level delta >5pp, strike count changes, engine state transitions, profile changes, or spike fires).

**Reset Semantics**:
- `ResetTransientState()` (line 463–477): zeroes session counters and pauses **but preserves `_offset`** (Q4.5 invariant, line 476 explicit comment).
- `ResetPostCalibration()` (line 482–490): full reset including `_offset=0` (only called on explicit user re-calibration).
- Chatbot paths use `ResetTransientState()` (Adaptive Fit SetLevel line 537, ApplySetMove line 647–652).

---

## 3. Locked Decisions / Invariants

### Q4.5 (ALPHA_HANDOVER §3.1)
Chatbot-initiated resets (shader, FOV offset, personalization) clear **only transient session state**. The `_offset` (weeks of habituation therapy progress) is **never zeroed** except by severe-strike overflow logic (`_severeStrikeCount >= SevereStrikeLimit` → immediate zero). This is enforced by `ResetTransientState()` explicitly preserving `_offset` and by test coverage (HabituationTests.cs).

### Reading A (Recommended Sever)
The chatbot can **no longer switch the habituation profile live**. `SwitchProfile` proposals are rejected at the Processor **early gate** (HabituationOverrideProcessor.cs:101–105) with reason: *"switch_profile is not supported — the habituation profile is set during calibration"*. The engine's `ApplyChatbotOverride()` implements a defensive no-op (line 950–957) that never calls `SetProfile()`. Profile changes **only** originate from `ApplyCalibrationResult()` (calibration_demo source). This is a one-way sever — no live path re-activates it without code change.

### Cross-Axis Coupling: Tier → Pause Duration Only
`GetPostSpikePauseMs(vimsTier)` in HabituationProfile.cs (line 197–206) is the **sole bridge** between tier and habituation:
- Tier ≤6 → low pause (auto-resume).
- Tier 7–9 → mid pause (auto-resume).
- Tier ≥10 → infinite pause (requires `AcknowledgeAndResume()` to exit).

No habituation earning logic, strike accounting, or offset magnitude depends on tier. Tier is pressure-discretized only.

### Vestibular Safety Intercept
**Three apply-time rules** (HabituationOverrideApplicator.cs:143–186):
1. Vestibular + ANY relaxation → block.
2. Vestibular + `AdjustStrikeLimit` where current limit == 1 → hard-cap block.
3. `HighRiskCombo` + significant (≥15% relative) relaxation → block.

Vestibular users have `SevereStrikeLimitOverride=1` (first post-reset spike zeros offset immediately) and receive a medical advisory on first calibration. These three intercepts re-fire on every `Resume` after a pause, protecting against approval-creep during extended sessions.

---

## 4. Open Concerns to Audit

### A. Per-Tick Strike Storm (EM1)

**What**: Old code level-triggered `RegisterSevereStrike()` on every tick during sustained high pressure, firing ~15×/sec in Passenger mode video playback.

**Why it matters**: Repeated halving of offset erases habituation progress; repeated strikes quickly overflow the counter and zero the offset.

**Current status**: **Mitigated by design**. Rising-edge detection via `_pressureWasAtRecurrence` state flag (line 301–307) fires only on the upward crossing, not on every tick the level stays high. Test EM1 (HabituationTests.cs:335–351) verifies: 100 ticks of sustained high pressure fires the same strike count as a single rising edge.

**Question for advisor**: Can you confirm that the edge-trigger logic is robust? Specifically, does the `_pressureWasAtRecurrence` flag correctly reset on the downward crossing, and is there any risk of a missed transition under concurrent reads (if the engine is multithreaded)?

---

### B. Offset Composition Saturation

**What**: Final FOV radius is `Clamp(pressure_radius + _userFovOffset + _habituation.HabituationOffset, 15, 100)`. If both `_userFovOffset` (≤0) and `_habituation.HabituationOffset` (≥0) are at extremes (e.g., offset at cap 30pp, user at -60pp), they may saturate the floor or ceiling.

**Why it matters**: Clamping introduces non-linearity. A user who earns full habituation may find their earned relaxation "hidden" by a tight user preference, or a clamped floor blocks further learning.

**Current status**: No documented constraint that `_userFovOffset + _habituation.HabituationOffset ≤ 85` (room to clamp 100). Compositional order is: pressure-driven + user + habituation, then clamp. The clamp is applied uniformly.

**Question for advisor**: Should there be a documented maximum composition (e.g., sum of user and habituation offsets ≤ 85pp) to ensure the clamping is predictable? Or is the current "first come, first served" clamping acceptable because the FOV floor (15%) is a hard safety limit?

---

### C. Non-Atomic Persistence (Profile Snapshot)

**What**: `BuildProfileSnapshot()` reads existing snapshot from disk (line 1166 of MockInferenceEngine.cs), computes fresh engine state in RAM, merges the two, then writes. If the read succeeds but the write crashes (or the file is locked), the snapshot is stale by one cycle.

**Why it matters**: Strike count, habituation level, or medical advisory changes mid-write could be lost or rolled back, leaving the user with inconsistent state on boot.

**Current status**: Partial **EM2 hardening** (line 1166): Read-modify-write carries forward `LongitudinalObservations` and calibration fields to avoid wiping them. But `MaybePersistProfileSnapshot()` and `LongitudinalObservationWriter.Append()` can race if both run concurrently; whichever writes second overwrites the other's changes.

**Question for advisor**: Should the profile snapshot write be fully atomic (temp file + File.Replace) like HabituationState (lines 73–74 of FileHabituationStore.cs)? Or is the current best-effort acceptable because observations are append-only and non-critical metadata?

---

### D. Schema Downgrade Nulling Safety Flags (H14)

**What**: Defense against hand-editing `schema_version:1` back into a snapshot after the app has written v2. H14 drops a sibling `schema_floor_v2.flag` file on every v2 Save. On Load, if the file is v1 **and** the flag exists, the snapshot is refused (returns null).

**Why it matters**: `MedicalAdvisoryShown` and `SevereStrikeLimitOverride` are safety-critical flags persisted in v2. A downgrade-attack that nulls them could bypass vestibular intercepts.

**Current status**: **Defense is loose**. If the flag file is deleted or becomes unreadable, a hand-edited v1 file will load on the next boot, null-filling safety fields. No cryptographic binding or checksum.

**Question for advisor**: Is a sibling flag file sufficient, or should the defense be stronger (e.g., checksum, schema version in the filename)? What is the recovery path if the flag is accidentally deleted?

---

### E. Fail-Open on Null Snapshot Load

**What**: When `FileHabituationProfileStore.Load()` returns null (unknown schema, file missing, malformed JSON, or refused v1 downgrade), `MockInferenceEngine` constructor (line 169) silently defaults to RESILIENT + source='default'.

**Why it matters**: A corrupted or future-incompatible snapshot causes **silent loss** of calibration state (CalibrationCount, PersonalPressureScale, MedicalAdvisoryShown, vestibular override). No event log, no telemetry, no UI warning.

**Current status**: **By design per Q2** (notes this is "the right cost/benefit"). The recovery path is to let the user re-calibrate manually.

**Question for advisor**: Should the app emit a non-blocking telemetry event or UI toast when recovering from a missing/corrupted profile? Would that help surface the recovery without alarming the user?

---

### F. Offset Preservation on Profile Downgrade

**What**: `SetProfile()` re-clamps `_offset` to the new profile cap if lower (HabituationEngine.cs:228–229). A downgrade from RESILIENT (cap 30) to SENSITIVE (cap 15) immediately halves earned progress.

**Why it matters**: Users experience sudden habituation backslip when switching to a stricter profile. The offset is preserved in value, but the cap is tighter, so visible progress (level = 100×offset/cap) is halved.

**Current status**: **Intentional per Q6** (offset preserved, cap enforced). No code comment at the clamp site (line 229) explains this to maintainers.

**Question for advisor**: Should there be a UX warning when a user switches to a stricter profile, explaining the cap change? Or is the silent clamp acceptable because profile switches are calibration-only (reading A sever)?

---

### G. Strike Counter Floating-Point Equality (Epsilon Tolerance)

**What**: `StrikeLimitEpsilon = 1e-9` (line 55) guards the comparison `_severeStrikeCount >= effectiveLimit - epsilon` at line 394. Sensor spikes weight 1.0, chatbot severities weight 0.5. The 4×0.5=2.0 sum is bitwise on IEEE 754, but epsilon defends against future weight changes (e.g., 0.33, 0.66).

**Why it matters**: A future calibration that changes chatbot weights could silently miss the strike limit without epsilon, allowing unbounded offset.

**Current status**: **Defensive by design**. Current weights produce clean sums (4×0.5=2.0 exactly). The comment at line 49–54 labels this "defensive-by-design, not accidental."

**Question for advisor**: Is the epsilon value (1e-9) correct for the precision of the comparison? Should it be revisited if weights are changed (e.g., 0.25, 0.33 instead of 0.5)?

---

### H. Unbounded Earning Without Decay

**What**: Offset earning runs every tick during relaxation phase with no built-in time-limit per user. Earning continues indefinitely until the profile cap is hit.

**Why it matters**: A user could theoretically earn max offset (30pp RESILIENT) in a few days of continuous calm, then maintain it indefinitely without further effort.

**Current status**: **By design per Q1** (scaling personalization, not hard caps). Profile selection is the mechanism — SENSITIVE users earn slower (1.0pp/step, 60s interval, 360s window) than RESILIENT (2.0pp/step, 30s interval, 120s window). No absolute lifetime bound on earning.

**Question for advisor**: Should the app track total lifetime earned offset (as a telemetry metric) to detect outliers? Or is the current system of profile-based pacing sufficient?

---

### I. Chatbot Weight Baked at 0.5

**What**: `NotifyDiscomfort(severe:true)` hardcodes weight=0.5 (line 370 of HabituationEngine.cs) per Q4 (chatbot signals are softer than sensor). If future calibration needs different weights (e.g., confidence-scaled 0.3..0.7), the engine API cannot express it — weights must be computed externally and `RegisterSevereStrike` called with explicit weight.

**Why it matters**: The engine is inflexible to future LLM confidence variations or ensemble models.

**Current status**: No API for weight tuning. The weights are locked in the enum and the hardcoded call.

**Question for advisor**: Should the habituation engine expose a `RegisterDiscomfortWithWeight(weight)` method to allow external calibration? Or is baking the weight acceptable because Q4 established a design policy ("LLM signals are softer")?

---

### J. Mild Discomfort Does Not Increment Strike Counter

**What**: `NotifyDiscomfort(severe:false)` applies back-off (-4.0pp) but does NOT increment `_severeStrikeCount` (line 373). A user can report mild discomfort infinitely without triggering the strike-limit zero.

**Why it matters**: Strike counter measures ONLY severe events (sensor or chatbot), not mild recurrences. This creates two separate "upset" categories: mild (pressure recurrence via sensor edge-trigger, or chatbot mild complaint) and severe (chatbot severe complaint or sensor extreme spike).

**Current status**: **By design** (strikes measure severe escalation, not ambient pressure). Mild events apply a small penalty (-4pp) and pause (cooling period) but do not escalate the long-term counter.

**Question for advisor**: Does the distinction between mild and severe align with the therapeutic intent? Should mild reports eventually trigger a strike if they occur repeatedly, or is the current approach (separate counters) better?

---

## 5. File Map

| File | Owns |
|------|------|
| `src/SenseEase.Core/Inference/HabituationEngine.cs` (511 lines) | Core state machine: offset earning, strike registration, pause entry/exit, profile changes, transient vs. post-calibration resets. Implements the invariants (offset clamping, strike counter reset, pause guards). |
| `src/SenseEase.Core/Inference/HabituationProfile.cs` (207 lines) | Three-tier profile definitions (RESILIENT, TYPICAL, SENSITIVE) with all time-constants and caps. `GetPostSpikePauseMs()` implements the tier→pause mapping (sole cross-axis coupling). |
| `src/SenseEase.Core/Configuration/SenseDefaults.cs` | Global defaults: `TriggerPressure`, `HabituationRecurrencePressureFraction`, `HabituationStablePressureFraction`, `HabituationMidBandDecayRate`, `HabituationMildBackoffPercent`, `HabituationSpikeBackoffMultiplier`, `StrikeLimitEpsilon`, `HabituationPersonalizationRefreshSeconds`. |
| `src/SenseEase.Core/Models/HabituationState.cs` | Minimal DTO: `Offset` field only. Restored on boot. |
| `src/SenseEase.Core/Models/HabituationProfileSnapshot.cs` (206 lines) | v2 schema DTO: profile, offset_pp, strike_count, engine_state, spike timestamp, calibration metadata, personal_pressure_scale, medical_advisory, longitudinal_observations, wipe-recovery flag. |
| `src/SenseEase.Core/Inference/HabituationOverrideParser.cs` (175 lines) | Extracts `[PROFILE_OVERRIDE]` blocks from LLM response. Accepts `adjust_sensitivity` \| `switch_profile` \| `adjust_strike_limit`. Rejects malformed/unknown intents. Sanitizes rationale (H7 injection guard). |
| `src/SenseEase.Core/Inference/HabituationOverrideProcessor.cs` (159 lines) | Validates proposals against 5 rules. **Reading A gate (line 101–105)**: early-rejects `SwitchProfile` with reason "not supported." Enforces range checks, confidence thresholds, and safety intercepts. |
| `src/SenseEase.App/ViewModels/HabituationOverrideApplicator.cs` (231 lines) | Phase 7f+7d+7g: re-checks safety intercepts before engine write, routes user choice (accept/decline/expire) to `ApplyChatbotOverride()`, logs to event store. Three blocking rules. |
| `src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs` (1300+ lines) | Engine facade: reads calibration results and chatbot overrides, composes FOV via `ComposeFovRadius()` (line 1241–1243). Persists snapshot via `MaybePersistProfileSnapshot()`. Implements `ApplyUserFeedback()` (Q4.5 gate at line 419). `ApplyChatbotOverride()` (924–986): `SwitchProfile` is a defensive no-op. |
| `src/SenseEase.Infrastructure/Storage/FileHabituationProfileStore.cs` (212 lines) | Loads/saves v2 profile snapshot. Schema detection: v2 direct, v1 with null-fill + downgrade guard H14. Atomic writes via `AtomicJsonWriter`. |
| `src/SenseEase.Infrastructure/Storage/FileHabituationStore.cs` (74 lines) | Legacy offset-only store. Reads `habituation.json`, restores via `Restore(state)`, clamped. Writes via `AtomicJsonWriter` (NE15 hardening). |
| `src/SenseEase.Infrastructure/Storage/LongitudinalObservationWriter.cs` (183 lines) | Appends dated observation to snapshot. Read-modify-write (carries forward calibration fields, EM2 hardening). Dedup vs. last 5, sanitization (H5 injection guard), FIFO-evict past 50. |
| `src/SenseEase.Infrastructure/Nlp/FeedbackLexicon.cs` (230 lines) | Severity bucketing: adverb scoring (1.0 extreme → 0.25 mild), bucket table (1–5 severity), FOV magnitude table (severity 1→-6pp, 5→-30pp). |
| `src/SenseEase.App/ViewModels/MainViewModel.cs` | UI layer: `EaseView` / `WidenView` call `ApplyUserFeedback(..., isDeliberatePreference=true)`. Q4.5 gate ensures deliberate adjustments do not trigger strike logic. |
| `tests/SenseEase.Tests/HabituationTests.cs` | EM1 test (335–351): 100 ticks high pressure = same strike count as a single edge. Offset clamping, profile persistence, pause auto-resume, clean-slate earning. |
| `tests/SenseEase.Tests/HabituationStrikeAndStateTests.cs` | Strike logic: halving, zeroing on limit, weights (1.0 sensor, 0.5 chatbot), epsilon, profile-scoped reset, reset on full window. |
| `tests/SenseEase.Tests/HabituationProfileStoreTests.cs` | Schema v1/v2 round-trip, downgrade guard H14, wipe-recovery synthesis, one-shot flag semantics. |
| `tests/SenseEase.Tests/HabituationOverrideProcessorTests.cs` | Early-gate test for `SwitchProfile` (Reading A sever). |
| `tests/SenseEase.Tests/HabituationOverrideApplicatorTests.cs` | Safety intercepts: vestibular + relaxation block, high-risk-combo block, decline/expire logging. |

---

## 6. Verification Notes

- **Tier scale**: Code comments vary (1–10 vs. 1–12). Native scale is 12-level (HabituationEngine.cs line 72–73); docstrings not uniformly updated.
- **Reading A vs. B**: Coded assuming **Reading A** (remove chatbot profile_override from selector, keep offset earning). The masterplan §13.5 notes this awaits Kirby's 2nd confirm before Stage 1-b lands.
- **EM2 persistence race**: Non-atomic read-modify-write in `BuildProfileSnapshot()` and `LongitudinalObservationWriter.Append()` can race; mitigated by carrying forward fields, but not bulletproof (see Concern C).
- **Vestibular hard-cap scope**: Applied at three re-firing intercept points and at engine registration (`SevereStrikeLimitOverride=1`). Orthogonal to the habituation sever; applies throughout the lifecycle.
