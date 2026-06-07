# Alpha-2 Core Reconstruction — Implementation Architecture (2026-06-06)

Adaptive Fit Layer Stage 2 (P3 + P4). Built on the landed native 12-level scale. **Selector ACTIVATED 2026-06-07** (`AppConfig.AdaptiveFitSelectorEnabled = true`): the *core* is built from the 12-level SSOT and is now live, acting on the same provisional OFI/pressure norm-factors the live overlay already uses; M1 capture (real WGC data) VALIDATES/refines those factors as a follow-up, not a prerequisite gate. **No code fabricates calibration data.** Source spec: `SenseEase-Adaptive-Fit-Layer-v1.md` §1–§12; plan: `Alpha_1_MASTERPLAN.md` §13.2 (P3/P4).

## Fork defaults (resolved under locked Q1–Q8; flagged for Kirby override)
1. **Grid = 10×5 = 50 sets**, set#0 = `(Band 5, Shape 2)` median (default L5).
2. **Set-move proposal = a PARALLEL `IntensitySetProposal` type + Applicator method** — does NOT mutate the locked 3-member `OverrideIntent` enum / its exhaustive switches. Cleaner separation, protects the override pipeline.
3. **Durable stats read = extend `IEventLogStore` with `ReadDurable(since)`** over the 7-day JSONL (the in-memory `Recent()` keeps only 20 — too small).
4. **Stats horizon = 7-day** (current `RetentionDays`); extend retention later if the selector needs longer "confidence on the coarse band first."
5. **Response-shape S = shader/overlay-render-only (Q4=shader-only)** — S does NOT modulate the global LeakyIntegrator decay. Visual/mask-shape only.

## Net-new (all `src/SenseEase.Core/AdaptiveFit/`)
- `GridCoord` (`record struct(Band,Shape)`, Manhattan dist, von-Neumann adjacency, `Set0`), `SetCurve(coord, LevelParams[12])`, `LevelParams(PeakMaskAlpha, FovFloorPercent, Scale, GridVisibility)`, `SetGrid` (builds 50 sets from the SSOT; `Curve`/`AdjacentCells`/`Segment`/`Set0`).
- `UserIntensityStats` (PressureDistribution p50/p90, SpikeRecurrenceRate, CalmRatio, FmsAggregate) + `IntensityStatsBuilder` (reads the **durable** event log — NET-NEW, NOT `HabituationTrajectoryBuilder`).
- `DeterministicSetSelector` + `SelectorWeights` + `SelectorDecision{Hold|MoveTo}`:
  `score = w1·fit(pressure,band) + w2·fit(spike,shape) + w3·fit(calm,band) − w4·dist − w5·recentManualConflict`;
  candidates = adjacent ∪ {current}; **confidence gate** (move only if argmax−hold ≥ margin).
- `CooldownState{Default,Settling,Active,Cooldown}` (dedicated — NOT `HabituationEngine.EngineState`) + `AdaptiveFitStateMachine`: Default(set0,L5)→Settling→Active⇄Cooldown; dual-trigger (manual adjust RESETS timer; set-change ENTERS cooldown; decline DOUBLES). Q4 felt-snap clears chatbot delta BEFORE snapping (Q5).

## Extended
- `IInferenceEngine`: add `ResolveLevel(int)→LevelParams`, `SetLevel(int, LevelChangeCause)`, `CurrentLevel`. (Set-move application stays on concrete `MockInferenceEngine`, mirroring `ApplyChatbotOverride`.)
- `MockInferenceEngine`: `_activeSet` (default Set0), **flag-gated** branch in `Update` (ON → mask/FOV from `ResolveLevel(_vimsTier)`; OFF → today's path byte-identical), `ResolveLevel`/`SetLevel`/`ApplySetMove`, Q4/Q5 ordering (clear delta via the `ResetPersonalization` shader path **preserving `_offset`**, then snap on new curve).
- `SenseDefaults`: w1..w5, `SelectorConfidenceMargin`, `SelectorFitTolerance`, `SelectorManualConflictRecencyMinutes`, `SelectorMinSamplesBeforeAnyMove`, `SetCooldownSeconds`, `SettlingMinSeconds`, `CooldownDeclineMultiplier` (all conservative/provisional).
- `IEventLogStore`/`FileEventLogStore`: `ReadDurable(since)` + add `"AdaptiveFit."` to durable prefixes; emit `AdaptiveFit.Sample` when flag ON.
- `HabituationProfileSnapshot`: `CooldownState` + `ActiveSet` fields (backward-compat default Default/Set0).
- `AppConfig`: `AdaptiveFitSelectorEnabled = false`.
- `MainViewModel`/Applicator/`IChatbotActionLog`: route cross-segment set-move proposals (parallel type), new action kind.

## Guardrails (carry every phase)
Vestibular: propose-stronger-only (block weaker-band set moves), resolved level clamped **≥ L9**, the 3 intercepts re-fire on apply, mask floor **0.20**, never touch `_offset` (C5), atomic writes (H16). §6 non-medical red line on all new copy. Fully local, no egress, NOT collaborative filtering.

## Fan-out groups (disjoint; sequence A → B‖C → D)
- **A** Grid + ResolveLevel: `AdaptiveFit/Grid*`, `LevelParams`, `SetCurve`, `IInferenceEngine`, `ResolveLevel`/`SetLevel` in `MockInferenceEngine`. **Pin: set0 ≡ today.**
- **B** Stats pipeline: `UserIntensityStats`, `IntensityStatsBuilder`, `IEventLogStore`/`FileEventLogStore` durable read.
- **C** Selector + state machine: `DeterministicSetSelector`, `SelectorWeights/Decision`, `CooldownState`, `AdaptiveFitStateMachine`, `SenseDefaults` selector region.
- **D** Propose/confirm + vestibular + flag: parallel proposal type + Applicator method, `IChatbotActionLog` kind, `MainViewModel` routing, `AppConfig` flag, snapshot DTO, Q4/Q5 ordering. (Touches `MockInferenceEngine` alongside A → sequence after A.)

## Test plan (`tests/.../AdaptiveFit*Tests.cs`)
no-move-on-noise · adjacency-only · Q4-before-Q5 · dual-trigger-cooldown-precedence · no-oscillation · confidence-gate · **flag-off parity (set0 output ≡ baseline; extend D6 L1-alpha=0)** · vestibular (weaker-band blocked, ≥L9, mask≥0.20, `_offset` untouched) · silent-vs-propose.

## Alpha-2 addendum — cancel explicit user calibration → auto-profiling (Kirby 2026-06-06)

**Status: ACTIVATED 2026-06-07 (`AppConfig.AutoProfilingEnabled` default `true`, paired with
`AppConfig.AdaptiveFitSelectorEnabled`, per Kirby's "兩個flag同時來" decision). M1 capture
(`baseline/M1-CALIBRATION-README.md`) now VALIDATES/refines the provisional norm-factor as a
follow-up — it is no longer a prerequisite gate. Setting the flag `false` reverts to the explicit
calibration flow byte-identically (the calibration code is suppressed-not-removed).**
The code is flag-gated; with the flag OFF the CURRENT explicit-calibration flow
(`docs/CALIBRATION.md`) remains fully in effect. New persisted field:
`habituation_profile.json:vestibular_protection_enabled` carries the auto-profiling
vestibular signal. The explicit-calibration CODE is **suppressed-not-removed** (the entry is
hidden behind the flag, not deleted) so the legacy path stays intact and revertible.

**Why Alpha-2 (not merely deferred).** The starting-level "gradual convergence" only has
meaning once auto-adaptation (P3/P4, the Adaptive Fit selector + state machine above) is
live. Cancelling explicit calibration BEFORE auto-adapt would strand new users at a default
they cannot get adjusted away from. So this lands WITH auto-adaptation, after M1 — it is
coupled to the same M1 gate as the rest of this document, not an independent toggle.

**Direction.** Cancel EXPLICIT user calibration (the 5-card questionnaire / demo-reaction
form and its LLM-dialog sibling) → the engine auto-profiles from usage behaviour instead.
Goal: a more immersive, zero-setup start.

**Start defaults (after calibration is cancelled).**
- **Starting level → neutral L5 "中"** (the Set0 median already pinned as set#0 above).
  Auto-adaptation gradually converges from there; a first-time user with no data starts at
  this standard value rather than at a self-reported profile.
- **Habituation profile initial → TYPICAL.** The user may still change it via the
  propose-confirm path — this is the 1-b-preserved *user-initiated* "change profile via
  confirmation" entry, NOT an auto-adaptive dynamic profile switch. (Auto-adaptation moves
  the level/set; the profile band stays a deliberate user-confirmed choice.)

**Vestibular safety (replaces the cancelled `Q5RiskFactors.VestibularDiagnosed` detection —
CRITICAL, must not be lost).** The system CANNOT infer a vestibular diagnosis from
optical-flow / behaviour — it is a medical fact, not a usage pattern. So after explicit
calibration is cancelled, this detection MUST be preserved in another form; otherwise the
entire vestibular-protection logic loses its trigger condition (the L9 floor /
SevereStrikeLimit=1 / TriggerPressure-50 hard-override would have nothing to fire on).
- **Form** — on first use, quietly place a ONE-TIME field at the bottom of the UI asking
  「是否有前庭/眩暈相關診斷?」. Non-blocking; it is NOT a calibration gate.
- **Trigger / dismiss** — after ANY response (yes / no / close) it never appears again. If
  the user closes the app WITHOUT any interaction at all, it appears again next launch.
  Implementation must ensure it is genuinely asked ONCE (not flashed-and-counted: a flash
  that the user never actually engaged must not consume the one-time ask).
- **Default** — skip / unanswered → treated as NON-vestibular (normal logic). Rationale:
  vestibular users are a minority; treating all silent users as vestibular would impose the
  L9 floor on the majority. Real vestibular users typically know their diagnosis and will
  actively answer.
- **Answer "yes"** → activate vestibular protection: **L9 floor, SevereStrikeLimit = 1,
  TriggerPressure 50 hard-override** — all unchanged from today's behaviour.
- **Regret mechanism (required)** — this vestibular setting MUST be permanently changeable
  in Settings (placed next to reset) so a user who skipped can enable protection later.
  This permanent-changeability is precisely what makes "ask once + skip = non-vestibular"
  safe: there is no permanent loss of protection for a user who dismissed the one-time ask.

**UI knock-on (Alpha-2, hand to ui-ux).** Remove the Main Console calibration entry;
simplify the console toward its final form (5級 動態感 status + +/- fine-tune + chatbot
input + 停用); move reset into Settings; place the vestibular setting in Settings next to
reset.

**Clarification (do not conflate two different "calibrations").** This cancels USER
calibration (personal profiling — the questionnaire/LLM-dialog described in
`docs/CALIBRATION.md`). The **M1 ENGINE calibration** (MAD→OFI physical scale) is
UNAFFECTED — it remains the Alpha-2 hard prerequisite for auto-adaptation, exactly as this
document already states. Two different things; do not merge them.
