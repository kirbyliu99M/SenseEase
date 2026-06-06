# Alpha-2 Core Reconstruction — Implementation Architecture (2026-06-06)

Adaptive Fit Layer Stage 2 (P3 + P4). Built on the landed native 12-level scale. **M1-gated:** the selector's *inputs* (OFI/pressure norm-factors) need real WGC data (days); the selector *core* is built now from the 12-level SSOT and ships **behind a default-OFF flag** (`AppConfig.AdaptiveFitSelectorEnabled = false`), anchored when M1 lands. **No code fabricates calibration data.** Source spec: `SenseEase-Adaptive-Fit-Layer-v1.md` §1–§12; plan: `Alpha_1_MASTERPLAN.md` §13.2 (P3/P4).

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
