# B6 — Unified Personalization via the LLM+Gate brain

**Status:** DESIGN — sign-off-ready, **awaiting Kirby's sign-off before any code** (per the execution protocol). Supersedes the B6 draft after a 3-angle red-team (15 defects: 6 HIGH all fixed, 6 MED accepted-with-mitigation, 3 LOW; see §8). Authored 2026-06-21 via workflow `w9jo4s3nm`.
**Spine invariant:** UNDER-PROTECTION is the asymmetric harm — a user cannot visually self-verify an eased mask, so every ambiguous case fails toward protection.
**Authoritative refs:** `AdaptationGate.cs`, `AdaptationEvidence.cs`, `MainViewModel.cs:940-2444`, `MockInferenceEngine.cs`, `UserModelRetrospectiveAnalyzer.cs`, `docs/B5_ADAPTATION_GATE_DESIGN.md`, `docs/LEARNING_LOOP_PLAN.md`, `docs/HABITUATION-PROFILE.md`.

---

## 1. Problem

SenseEase has ONE correct, fully-built deterministic adaptation channel — the B5 `AdaptationGate` + `AutoAdaptTriggerDeltaPp` auto-channel. It is **half-dead by wiring**: TIGHTEN is live and data-driven (the gate's own measured-worsening authority fires it); RELAX is severed at three independent points:

- **(a)** the UM4 analyzer prompt (`UserModelRetrospectiveAnalyzer.cs:47-53`) never elicits an `adjust_sensitivity` proposal;
- **(b)** `MainViewModel.cs:2424` hardcodes `hint = new AdaptationHint(AdaptDirection.Hold, string.Empty)` (the W-E cut);
- **(c)** even once wired, the cold-start evidence bar (8 ON-state measured FMS inside a 7-day window) is unreachable at the real engaged-user supply of ~1-3 FMS/week.

A fourth axis — the protection-bearing cohort (`Typical`/`Sensitive`/`Vestibular`) — is frozen at a static `TYPICAL`/L5 seed (`MockInferenceEngine.cs:862`, `auto_profiling_default`) because its only manual setter (`SelectProfileCommand → OnUserProfileSelected`) is dead UI (pill deleted 2026-06-09).

B6 unifies all four into one **"LLM proposes DIRECTION ONLY / the gate disposes on measured-only evidence"** brain **without adding any new actuator**. The engine actuator (`ApplyAutoAdaptDelta`), the parser (`HabituationOverrideParser`), the gate, and the cohort setter (`SetHabituationProfile`) are all built and correct.

> **What the red-team forced (executive view):** the draft's *headline* Part-2 safety argument — "widening the FMS window 7d→28d is monotone toward protection on every value/suppression check" — is **FALSE against the code**. `WorstRecentFmsOn` is a **p75 percentile** (`AdaptationEvidence.cs:65,70`), not a max; adding calm reports *dilutes* a bad episode out of the tail. Part 2 is redesigned below: **widen the COUNT/coverage horizon, never the worst-case/discomfort horizon, and switch the relax worst-case to a true MAX.** Part 3's "floor re-asserts automatically" and "reversible via chatbot" claims are also false against the code (`SetHabituationProfile` does NOT recompose the trigger; the chatbot `SwitchProfile` path is a defensive no-op). Both are now explicit, coded seams below.

---

## 2. The three parts (seam-level)

### Part 1 — Re-activate RELAX (LLM proposes direction, gate disposes)

Four additive-only seams. Nothing about the gate, actuator, or evidence math changes.

- **SEAM 1 (prompt, distinct grammar):** extend `UserModelRetrospectiveAnalyzer.AnalysisSystemPrompt` (`cs:47-53`) to OPTIONALLY emit an **`[ADAPT_DIRECTION]{intent:adjust_sensitivity, proposed_value, rationale, expires_at_iso}`** block — **a NEW block name, NOT `[PROFILE_OVERRIDE]`** (red-team fix RT-1b). The `[OBSERVATION_ADD]` instructions stay VERBATIM. Add 1-2 few-shot examples showing BOTH directions so a 3B model doesn't only ever propose relax.
- **SEAM 2 (thread the dead capture + freshness):** the analyzer captures `LastProposedSensitivityScale` after `RunAsync` (`MainViewModel.cs:2337-2338`) but `MaybeRunAdaptationTuning` (`cs:2342`) never reads it. Thread the captured scale **stamped with the analyzer run timestamp** + the sanitized rationale into `MaybeRunAdaptationTuning`. **Treat the hint as Hold if the proposal is older than 72h** (RT-2s staleness fix).
- **SEAM 3 (the W-E cut):** replace `MainViewModel.cs:2424` `new AdaptationHint(AdaptDirection.Hold, string.Empty)` with `AdaptationGate.DirectionFromSensitivityProposal(proposedScale, snap.PersonalPressureScale)` + the sanitized rationale. `snap` already carries `PersonalPressureScale` (captured-but-unused-for-the-hint today).
- **SEAM 4 (honest notice, re-confirmed at render):** restore the direction-aware bilingual `AdaptationNoticeText` keyed on `apply.Direction` (`Relax ⇒ "I eased it"`, `Tighten ⇒` existing "strengthened"), via `Loc`. **The notice must RE-READ the live auto-delta at render time** (RT-relax-notice fix) and downgrade "I eased it" to a conservative phrasing if a concurrent protective event clawed the relax back between Apply and notice-marshal.

**Safety model:** the LLM gains DIRECTION ONLY — `DirectionFromSensitivityProposal` collapses `proposed_value` to a sign (`AdaptationGate.cs:197-203`); magnitude/axis/timing stay the gate's. An `[ADAPT_DIRECTION]` relax proposal contradicting measured data is rejected by `RelaxReject` (data wins; the gate recomputes from un-injectable sensor + measured-FMS rows). Rationale is `SanitizeRationale`'d (≤120 chars, injection-stripped) and render-only. Monotone-up holds: a worsening signal still independently TIGHTENS (`AdaptationGate.cs:64-65`). With `AdaptiveAutoTuneConsent` unset, this path is inert — protection is byte-identical to today.

### Part 2 — Evidence-reachability re-tune (REDESIGNED after RT)

**The blocker is the report-COUNT supply, not any safety floor.** `MeasuredOnFms` is called with `AdaptiveAutoTuneWindowDays = 7.0` (`MainViewModel.cs:952`, single-use named const, used only at `cs:2394` — verified, so the "is 7.0 reused?" pre-work is dropped per RT-low). Cold-start needs `OnReportCount ≥ 8` ⇒ 8 ON-state measured FMS in a trailing 7 days = ~3-8× the real ~1-3/week supply.

**THE CRITICAL RED-TEAM CORRECTION:** the draft's "widen the whole FMS horizon to 28d" is **REJECTED**. `WorstRecentFmsOn = Percentile(values, 0.75)` is a rank statistic; widening the value window lets calm reports push a genuine bad episode out of the p75 tail (worked example in §5). **Decouple the two horizons:**

| Horizon | Knob | New value | Rationale |
|---|---|---|---|
| COUNT / coverage accumulation | `AdaptiveAutoTuneFmsCountWindowDays` (NEW const) | **28d** | makes 8 ON-reports reachable at ~2/week |
| WORST-CASE value + discomfort clock | `AdaptiveAutoTuneFmsRecencyWindowDays` (NEW const) | **10d** (≤ count window) | a recent bad episode always dominates; cannot be diluted by old calm reports |

Plus three guard upgrades inside `AdaptationEvidence.MeasuredOnFms`:

1. **Worst-case = MAX, not p75** (RT-1 + RT-6). Change `WorstRecentFmsOn` for the relax gate from `Percentile(values, 0.75)` to `Max(values)` computed over the **recency** window. A max is genuinely monotone-up — no number of calm reports can dilute a bad one out. (Belt-and-suspenders: an explicit "any single ON report ≥ threshold in the recency window ⇒ hard Hold" check, so point-wise and aggregate can never disagree in the relax-permitting direction — RT-eq.)
2. **Window-proportional coverage** (RT-2c). Replace `distinctDays >= 2` with: `distinctDays >= ceil(countWindowDays / 7)` (≥4 distinct days over 28d) **AND** no single day contributes >50% of the count **AND** at least one ON report in the most recent 7 days (recency term — an old calm streak cannot carry a stale relax).
3. **Discomfort clock spans the COUNT window, never shorter** (RT-3d). `HoursSinceLastDiscomfort` is already computed over `windowDays` (`AdaptationEvidence.cs:41-56`); bind its window to the **28d count window** so a bad report that padded the count can NEVER simultaneously be invisible to suppression. Additionally: **any FMS ≥ threshold anywhere in the count window forces a Hold** (a longer "recent bad episode" bar, not only the 48h acute one).

**UNTOUCHED floors:** `RelaxFmsLowThreshold = 6.0`, `ExposureAtOrAboveBaseline`/`!ExposureDeclining`, the 48h suppression, `RelaxMinIntervalDays = 7`, `MaxAutoChangesPerWeek = 2`, `MaxNetRelaxPerDayPp = 3`, `AutoTriggerSpanPp = 12`, `AutoTriggerStepPp = 3` (do NOT raise — reopens the §6 composed-slew invariant). Reachability is bought from the COUNT time-horizon only; the worst-case and discomfort axes get **stricter**, not looser.

**Selective-reporting residual (RT-5, accepted + documented):** a user who logs only on calm days can clear count + max + coverage while bad days go unlogged. This is intrinsic to measured-only evidence and cannot be fully closed. **Mitigation:** the behaviour/exposure SENSOR arm (un-injectable, not self-selected) must independently corroborate — already required via `ExposureTrend` — and its thin-data floor (`MinHalfDays`) must scale proportionally to the 28d window so 8 reports can't come from a handful of cherry-picked good days. Document in `B5 §4` that selective non-reporting of bad days is the reason the sensor arm is mandatory and must stay un-self-selectable.

### Part 3 — Cohort/profile gate-linkage (ONE-WAY-SAFE, REDESIGNED after RT)

Add a deterministic auto-classifier as a sibling to `AdaptationGate.Evaluate` returning **`PROMOTE_TO_SENSITIVE | HOLD`** — never a downgrade. Wire into `MaybeRunAdaptationTuning` behind the SAME `AdaptiveAutoTuneConsent`. **Promotion trigger** (composite, measured-only, STRICTER than the per-eval tighten bar): `(sustained WorstRecentFmsOn-max ≥ 10/20 across the recency window)` OR `(BehaviorWorsening sustained over the 28d behaviour half-split)` OR `(SevereStrikeCount ≥ 2 in-window)`.

**Three red-team corrections that were false-premises in the draft:**

- **RT-3.1 — the floor does NOT auto-re-assert.** `ApplyAutoDeltaToFreshBase` runs ONLY at boot (`MockInferenceEngine.cs:351`), `SetVestibularProtection` (`cs:906`), and `AdjustSensitivity` (`cs:1651`). `SetHabituationProfile` (`cs:1456-1469`) calls `_habituation.SetProfile` which **deliberately does NOT touch `EffectiveTrigger`** (`HabituationEngine.cs:212-232`). So a TYPICAL→SENSITIVE promotion would leave a standing positive (relaxed) auto-delta intact under the new floor. **FIX:** add an explicit `TareAutoRelaxForSensitive()` invoked at the promotion site, under `_gate`: if new cohort is `Sensitive` and `_autoAdaptTriggerDeltaPp > 0`, set it to `≤ 0`, recompose against the (unchanged, correct) base, write the audit record, and re-assert the floor. **Test:** TYPICAL with +6 delta, promote → assert `delta ≤ 0` AND `EffectiveTrigger ≤ base`.
- **RT-3.2 — the "reversible via chatbot" route is DEAD.** `ApplyChatbotOverride`'s `SwitchProfile` case is a defensive no-op (`MockInferenceEngine.cs:1656-1661`); the Settings pill is deleted. The draft's promotion notice would point at a dead route — a W-E-class false claim. **FIX (chosen default, see fork F5):** make the notice HONEST about stickiness ("I've increased your protection level; this stays on") and drop the reversible-path language. (Alternative: build a real gated explicit-user SENSITIVE→TYPICAL setter first — heavier, deferred.)
- **RT-3.5 — "sustained / consecutive" needs DURABLE state.** The tuning pass is stateless across runs; an in-memory streak counter resets every app restart, making promotion unreachable (the same class of bug Part 2 fixes for relax). **FIX:** derive "sustained" from DURABLE rows — count `SevereStrike` audit entries in the adjustment log within the window, and derive sustained-worsening from the `behavior_log` half-split over the already-loaded 28d horizon. No in-memory streak. Reachability test (engaged-user promotes; transient single-bad-day does NOT).

**Atomicity (RT-2.5 + RT-3.4):** the classify + promote + tare + relax-dispose MUST be ONE critical section. Today `MaybeRunAdaptationTuning` snapshots under `_gate`, releases, then re-acquires inside `ApplyAutoAdaptDelta` (absolute-delta overwrite at `cs:1713`). Adding `SetHabituationProfile` as a SECOND separate acquisition opens a classify-vs-relax race where a relax evaluates against a stale `cohort=Typical` and skips the floor. **FIX:** add an engine method `ApplyGateOutcome(profilePromotion, newDelta)` that takes `_gate` once, **re-reads live state inside the lock, classifies FIRST, applies the profile flip + tare, THEN sets the delta**, aborting (Hold) on snapshot/live mismatch. The gate EVALUATION stays pure/off-lock; only the APPLY is the single locked, live-rechecked commit.

**One-way ratchet:** PROMOTION (TYPICAL→SENSITIVE) only ADDS protection (engages the no-net-relax floor, `HabOffsetCapPp 25→15`, `SevereStrikeLimit 2→1`, re-seeds the SENSITIVE opening overlay unless `_shaderUserModified`). DOWNGRADE (SENSITIVE→TYPICAL) is **FORBIDDEN as an auto move** — it removes a floor = under-protection. `RESILIENT`+`TYPICAL` both map to `Cohort.Typical`, so a Resilient→Typical move has no gate-floor effect (excluded from auto-promotion). FAIL-CLOSED: a NaN/garbage worst-case must HOLD, never promote (promotion on garbage is over-protection but still an unrequested sticky flip — RT-3.3). Persistence: extend `profile_source` with `'auto_classified'` + boot-load whitelist. `ResetPersonalization` stays profile-untouching (reverting a promotion would remove a floor) — with a documenting comment.

---

## 3. Orphan ledger

| Signal | Decision | Disposition |
|---|---|---|
| `AutoAdaptTriggerDeltaPp` RELAX arm (severed at `MainViewModel.cs:2424` + UM4 prompt + dead capture) | **wire-in** | Part 1 (3 seams + honest notice) |
| `AdaptationNoticeText` relax branch (removed at W-E cut) | **wire-in** | Part 1 SEAM 4, render-time re-confirmed |
| `AdaptiveAutoTuneWindowDays=7.0` × `ColdStartMinFmsReports=8` (unreachable bar) | **fix** | Part 2 — split into count(28d)/recency(10d) windows |
| `HabituationProfile` cohort frozen at TYPICAL seed | **wire-in** | Part 3 — one-way-safe auto-classifier |
| `overlay_consent` rows → ClinicalExport phantom FMS=0 (`ClinicalExport.cs:160-161`, no Kind filter) | **fix-bug** | Stage 0 — add `Kind==FmsReport\|\|FmsEstimate` filter before the measured/estimated split; gate already immune |
| `UserPreferences.OverlayDeclineCount` (write-only tally) | **document-reserved** | resist-protection signal; if ever wired, tighten/promotion BIAS only, never a relax input, and only gated behind a working downgrade path + alongside a measured symptom term (RT-decline) |
| `DailyBehaviorAggregate.StableWindowsCompleted` | **document-reserved** | positive evidence but duplicates `OffsetDeltaPp`; do NOT wire v1; if ever, CORROBORATING-ONLY (cannot bypass worst-case/48h/SENSITIVE floors) |
| `DailyBehaviorAggregate.MaskActivations` | **document-reserved** | exposure/severity proxy — a TYPICAL→SENSITIVE *tighten* term at most, never relax |
| `DailyBehaviorAggregate.OfiP50` | **document-reserved** | central tendency; gate correctly keys on the P90 tail; no safety value |
| `EventLogEntry.ForegroundKey` (B3.0) | **document-reserved** | reserved scaffolding behind paused B3.2 per-app bucketing; do NOT conflate with `SymptomEvent.ForegroundKey` (which IS consumed) |
| `UserModelIndex.BucketManifest` + `BucketMeta` | **retire** | write-never/read-never; zero behavioural risk; Stage 0 cleanup |
| `UserIntensityStats.PressureP50` | **document-reserved** | never scored by the P90-only selector; cosmetic; selector out of B6 scope |
| `SevereClearMode` enum + `Zero` | **document-reserved** | dormant policy switch (all profiles hardcode `Halve`); reachable via `SevereStrikeLimit=1`; not a personalization signal |
| `OffsetDeltaPp` / `TierModal` (→ chatbot prompt only) | **keep** | advisory LLM context, not the gate; measured-only NOT violated — confirmed |
| `SessionMinutes` / `OfiP90` / `SpikeCount` | **keep** | CORRECTION to the task's known-orphan list: these ARE live protection-bearing gate inputs (`AdaptationEvidence.cs:111-114`), NOT orphans — do not retire |

---

## 4. Unified safety model + invariants

1. **LLM owns DIRECTION ONLY.** Hint is enum + render-string; the gate sets magnitude (one ±3pp step), axis, timing. The auto-classifier is a deterministic gate computation, NOT an LLM output.
2. **Evidence is MEASURED-ONLY, recomputed, un-injectable.** `MeasuredOnFms` hard-filters `e.Measured && Kind==FmsReport` (`AdaptationEvidence.cs:47`); `nl_daily`/`fms_estimate` excluded BY CONSTRUCTION. Widening the count window must NOT relax this filter.
3. **FAIL-CLOSED on non-finite.** NaN/-Inf must Hold, never leak a relax (a NaN "falls open toward relax" in IEEE compares — the worst failure). A wider/emptier window still yields finite aggregates; the count check fails first.
4. **MONOTONE-UP.** Any discomfort (FMS ≥ threshold in the count window) or behaviour-worsening forbids relax AND independently TIGHTENS. A decline/worsening/mask-activation signal can only ADD protection.
5. **VESTIBULAR never auto-relaxes; composed `EffectiveTrigger` ≤ 50** (3 redundant layers). Untouched by the re-tune; out of the auto-classifier's scope.
6. **SENSITIVE never NET-relaxes.** The floor is **explicitly re-asserted and any residual positive delta tared at the promotion site** (`TareAutoRelaxForSensitive`, RT-3.1) — NOT relied upon to auto-re-assert.
7. **Cumulative |auto delta| ≤ 12pp** vs the FIXED opt-in baseline. Engine literal `AutoAdaptTriggerSpanPp=12.0` and `AdaptationGateConfig.Default` move in lock-step.
8. **RESET zeros the auto channel TOWARD protection.** An `auto_classified` SENSITIVE promotion SURVIVES Reset (reverting removes a floor); `ResetPersonalization` stays profile-untouching.
9. **DEFAULT-OFF behind `AdaptiveAutoTuneConsent==Enabled`.** Consent-off ⇒ protection byte-identical to today.
10. **ONE-WAY ratchet toward protection.** Cohort auto-moves UP only (Resilient→Typical→Sensitive). Auto-downgrade FORBIDDEN.
11. **LOCKED OFI 0-150 scale + `TriggerPressure=85`** never edited; auto-channel composes as a signed pp delta; auto-apply record is `Source='system'`/audit-only.
12. **Worst-case is a MAX over the recency window, not a dilutable percentile** (RT-1). No number of calm reports can bury a bad episode.
13. **The composed floor is 2-channel (base + auto), NOT 4-channel.** Per RT-3.3, **the invariant text is CORRECTED**: the SENSITIVE/vestibular floor sees `BaseEffectiveTrigger + CurrentAutoDeltaPp` only. The selector band-bias and user-nudge axes are out of Stage-1 scope and are gated separately by the selector's own L9/propose-stronger guards. **Do not claim a 4-channel composed floor the code does not implement.** (Threading band-bias into `MeasuredState` is a deferred fork, not B6.)
14. **UNDER-PROTECTION is the ASYMMETRIC HARM** — the spine. It dictates sustained-MAX-proof relax, the up-only cohort ratchet, NaN-holds, and add-only orphan signals.
15. **§6 composed slew** satisfied by `≤3pp` step + `UpdateVimsTier` hysteresis — ONLY while `AutoTriggerStepPp ≤ 3`. Not raised.
16. **The `[OBSERVATION_ADD]` contract stays intact**, and the relax elicitation uses a **DISTINCT block name** (`[ADAPT_DIRECTION]`) so it cannot bleed into the live-chat `[PROFILE_OVERRIDE]` Accept-card path (RT-1b).

---

## 5. Evidence-reachability re-tune — the feasibility math

**Supply:** a real engaged user logs ~1-3 measured FMS/week. The ON-state filter (`AdaptationEvidence.cs:59-62`) keeps only mask-up reports — a further multiplier on the cost.

**Old bar (unreachable):** `OnReportCount ≥ 8` within `7d` ⇒ 8 ON-reports/week = 8/7 ≈ 1.14/day = **~4-8× supply.** Inert.

**New bar (reachable + safe):** `OnReportCount ≥ 8` within the **28d count window** ⇒ 8/28 ≈ 0.29/day ≈ **2/week — inside supply**, first auto-relax in ~4 weeks of steady cadence (the most protective ramp). Worst-case + discomfort run on the **10d recency window** and are MAX-based, so reachability does NOT touch any value floor.

**Why p75-widening was rejected — worked counterexample (RT-1):** over 28d a user logs seven calm ON-reports `=2` and ONE genuine bad episode `=7`. Sorted `[2,2,2,2,2,2,2,7]`, n=8. `p75` rank `= 0.75×(8-1) = 5.25` → interpolate index5(2)/index6(2) = **2.0**, far below `RelaxFmsLowThreshold=6`. **The FMS=7 is invisible; the gate would fire a relax.** Switching the relax worst-case to `Max = 7.0 ≥ 6` ⇒ **hard Hold.** This is the single change that makes the count-window widening safe.

**Coverage counterexample (RT-2c):** 8 reports clustered into one calm 2-day island inside a rough month clears `distinctDays ≥ 2`. New rule: `distinctDays ≥ ceil(28/7)=4` AND no day >50% of count AND ≥1 ON report in the last 7 days ⇒ the 2-day island is rejected `fms-coverage-insufficient`.

**Fail-closed preserved:** an emptier/wider window still yields finite aggregates (`Max=0` only when `Count==0`, and the count check fails first at `AdaptationGate.cs:105/108` before the value check at `:120`). The behaviour arm returns `TrendStableOrImproving=false` when `rows < MinHalfDays*2` (`AdaptationEvidence.cs:102`), blocking relax `trend-not-stable` — verified no fail-open path.

**Rejected alternative:** lowering `ColdStartMinFmsReports` alone (e.g. 8→4) is insufficient (4-in-7 is still >2/week) AND erodes the cold-start ramp margin. Reachability must be bought from the TIME HORIZON, never from lowering a count or value floor.

---

## 6. The cohort one-way-safe rule

```
ALLOWED  (auto): Resilient → Typical → Sensitive      (ratchet UP — adds protection)
FORBIDDEN(auto): Sensitive → Typical → Resilient      (removes a floor = under-protection)
```

- **Promotion** fires only on the SAFE (tighten) side; composes with the auto-delta only toward protection (no double-relax risk on the up path — both push the same way).
- **Promotion site** runs `TareAutoRelaxForSensitive()` (RT-3.1) so a pre-promotion positive delta cannot survive under the new floor.
- **Atomicity:** classify→promote→tare→dispose in ONE `_gate` critical section, live-rechecked (RT-3.4), so a relax can never read a stale `Typical` cohort.
- **Downgrade** is forbidden as auto; the explicit user route is currently DEAD (`SwitchProfile` no-op) so the promotion notice is HONEST about stickiness rather than promising a dead reversal (RT-3.2).
- **Misfire containment:** the classifier consumes the SAME fail-closed non-finite discipline as the gate — a NaN/garbage worst-case HOLDs, never promotes (RT-3.3).

---

## 7. Staged plan

**STAGE 0 — independent cleanups (no protection change, ship first):**
(a) FIX `ClinicalExport` contamination — add `Kind==FmsReport||FmsEstimate` filter in `ClinicalExportBuilder.Build` before the measured/estimated split. (b) RETIRE `UserModelIndex.BucketManifest` + `BucketMeta`. (c) DOCUMENT-RESERVED one-line comments on each reserved orphan. Build + full suite + a new `ClinicalExport` regression test asserting `overlay_consent` rows no longer inflate the estimated bucket. Review gate. No consent surface touched.

**STAGE 1 — Part 1 (re-activate relax, behind consent):** SEAM 1-4 with the **distinct `[ADAPT_DIRECTION]` block** + 72h freshness + render-time notice re-confirm. Evals: (1) prompt-elicitation emits a parseable direction-honest block in BOTH senses WITHOUT dropping `[OBSERVATION_ADD]`; (2) injection — a relax proposal contradicting measured data is rejected (data wins); (3) **live-chat Accept-card frequency for `adjust_sensitivity` does NOT increase** after the prompt change (RT-1b generalization guard); (4) apply-then-protective-event ⇒ no false "eased" claim. NOTE: relax is wired but still UNREACHABLE at the evidence layer until Stage 2 — intentional; real users' protection envelope unchanged.

**STAGE 2 — Part 2 (reachability re-tune):** introduce `AdaptiveAutoTuneFmsCountWindowDays=28` + `AdaptiveAutoTuneFmsRecencyWindowDays=10`; switch relax worst-case to MAX over recency + the single-report hard-block; window-proportional coverage; bind discomfort clock to the count window; scale `MinHalfDays` to the window. Reachability regression PAIR: engaged-user fixture (2 ON-FMS/week × 4wk, stable at-baseline, no discomfort) reaches `Apply(Relax)` under the NEW windows AND the same cadence is rejected `cold-start-fms-reports` under the OLD 7d; the **p75-dilution adversarial fixture** `[2×7, 7]` over 28d MUST reject `recent-fms-too-high` (MAX guard); 2-reports-in-2-days fixture rejects `fms-coverage-insufficient`; plus exposure-declining / discomfort-within-window / rate-cap rejects. Document in `B5 §11` that "8 measured reports" means "8 ON-state" and that count/recency are split. Review gate.

**STAGE 3 — Part 3 (cohort auto-classifier):** widen `SnapshotForAdaptation` to return `SevereStrikeCount` under `_gate`; add the `PROMOTE_TO_SENSITIVE|HOLD` classifier with durable-row sustained evidence (RT-3.5); add `ApplyGateOutcome` single-critical-section commit (RT-3.4); add `TareAutoRelaxForSensitive` (RT-3.1); extend `profile_source` with `'auto_classified'` + boot whitelist; leave `ResetPersonalization` profile-untouching (comment); emit an HONEST bilingual sticky-promotion notice (RT-3.2). Evals: one-way safety (downgrade forbidden, promote liberal, survives save/load + Reset); promotion tares a pre-existing +6 relax delta (`delta≤0 ∧ EffectiveTrigger≤base`); classify-vs-relax race resolves to the tightened cohort; reachability pair (engaged promotes, transient single-bad-day does NOT); NaN worst-case HOLDs. Review gate.

**STAGE 4 — UVHS (human-verified before any protection-shipping commit):** DEV force-path (stripped before commit) drives each visible surface — the in-chat adaptation notice in BOTH directions bilingual; an auto-relax overlay-ease being subtle/non-jarring (the §6 slew claim made real); the cohort promotion notice + SENSITIVE opening-overlay re-seed not jumping mid-relaxation; the Settings "Adaptive auto-tune" consent toggle; a Reset visibly returning protection to base. Only after Kirby signs off do the protection-changing stages commit.

---

## 8. What the red-team changed (delta table)

| # | Severity | Defect (verified against code) | Resolution |
|---|---|---|---|
| RT-1 | HIGH | Draft's load-bearing claim "widening 7d→28d is monotone-protective; a stale bad episode blocks LONGER" is FALSE — `WorstRecentFmsOn` is **p75** (`AdaptationEvidence.cs:65,70`), a rank stat; calm reports dilute a bad one out | **ACCEPTED.** Split count(28d)/recency(10d) windows; switch relax worst-case to **MAX**; added p75-dilution regression `[2×7,7]→reject` |
| RT-2c | HIGH | `CoverageOk = distinctDays>=2` (`:71`) doesn't scale; a 2-day calm island inside a bad month clears it | **ACCEPTED.** Window-proportional coverage + per-day cap + last-7d recency term |
| RT-3d | HIGH | Discomfort clock can age a count-padding bad report out of suppression | **ACCEPTED.** Bind discomfort clock ≥ count window; any FMS≥thr in count window ⇒ Hold |
| RT-1b | MED | Dual-consumer grammar collision — eliciting `[PROFILE_OVERRIDE]{adjust_sensitivity}` bleeds into the live Accept-card path (`ChatbotViewModel.cs:800-808`) | **ACCEPTED.** UM4 uses DISTINCT `[ADAPT_DIRECTION]` block; live grammar byte-identical; Accept-card-frequency eval |
| RT-2s | MED | UM4 relax bypasses the processor's expiry/spam/decline guards; a 30-day-old chat phrase is a standing relax vote | **ACCEPTED.** 72h freshness stamp on the captured proposal; default-require `expires_at_iso` |
| RT-eq | MED | Point-wise discomfort vs p75 value-check can disagree in the relax-permitting direction | **RESOLVED by the MAX switch**; belt-and-suspenders single-report hard-block added |
| RT-3.1 | HIGH | "SENSITIVE floor re-asserts automatically (`ApplyAutoDeltaToFreshBase` already runs)" is FALSE — `SetHabituationProfile` (`:1456-1469`) does NOT recompose `EffectiveTrigger`; a +6 relax survives promotion | **ACCEPTED.** Explicit `TareAutoRelaxForSensitive()` at promotion site + test |
| RT-3.2 | HIGH | "Reversible via chatbot `SwitchProfile`" is FALSE — that path is a defensive no-op (`:1656-1661`); pill deleted | **ACCEPTED.** Notice is HONEST about stickiness; reversible-path language dropped (building a real setter is a deferred fork) |
| RT-3.3 | HIGH | Invariants claim a 4-channel composed floor; gate's `MeasuredState` carries only base+auto | **ACCEPTED.** Invariant 13 corrected to 2-channel; band-bias gated separately; 4-channel claim removed |
| RT-3.4 | MED | Classify + relax in two separate `_gate` acquisitions ⇒ relax reads stale `Typical` cohort | **ACCEPTED.** Single-critical-section `ApplyGateOutcome` with live re-check |
| RT-3.5 | MED | "sustained/consecutive" promotion needs durable state or it's unreachable across restarts | **ACCEPTED.** Derive sustained from durable audit/behaviour rows, not in-memory streak |
| RT-relax-notice | MED | Restored "I eased it" notice re-opens the W-E false-claim under an apply-then-clawback race | **ACCEPTED.** Notice re-reads live delta at render; conservative phrasing on partial claw-back |
| RT-low-7.0 | LOW | "Verify 7.0 not reused / ALT B mandatory" is moot — already a dedicated single-use const | **ACCEPTED.** Dropped from Stage 2; attention redirected to the MAX/recency decision |
| RT-fail-closed | LOW | Confirmed NaN/empty-window fail-closed holds today | **ACCEPTED as-is** + added pinning regression tests so a future re-tune can't silently open it |
| RT-decline | LOW | `OverlayDeclineCount`-as-promotion-bias + irreversible ratchet + dead downgrade = users stuck for clicking "not now" | **ACCEPTED.** Kept document-reserved; any future wire-in gated behind a working downgrade path + a measured corroborating term |

**Defects NOT fixed (justified):** RT-5 selective-reporting is **intrinsic to measured-only evidence** — not closable; mitigated by the mandatory un-self-selectable sensor arm + documented as a known residual (the under-protection asymmetry is why the sensor arm exists). The 4-channel composed floor is **not built** and B6 does not build it; the invariant is corrected to match the implemented 2-channel scope rather than expanding gate scope mid-sprint.

---

## 9. Open forks (proposed defaults)

| # | Fork | Proposed default | Alternative |
|---|---|---|---|
| F1 | First-relax latency (Part 2) | **Count window 28d, cold-start count 8** (~4wk, most protective ramp) | 21d count + cold-start 6 (~3wk, two knobs move) |
| F2 | Recency window length (Part 2) | **10d** for worst-case/discomfort (≤ count window, MAX-based) | 7d (tighter recency, slightly fewer reports in-band) |
| F3 | ON-state count policy | **Count ON-state-only** (honest tolerated-protected-load; matches B5) | count all-measured for the COUNT, value-check ON-only (more reachable, counts unprotected reports) |
| F4 | Knob home | **Hoist `AdaptationGateConfig.Default` + the two window consts into `SenseDefaults`** (single locked source; unifies the engine span mirror) | leave in the record, change the literal (fewer files, desync risk persists) |
| F5 | Cohort downgrade (Part 3) | **Forbid auto-downgrade; promotion notice is honest about stickiness** (no dead-route promise) | build a real gated explicit-user SENSITIVE→TYPICAL setter first so "reversible" is true |
| F6 | Cohort consent (Part 3) | **Ride existing `AdaptiveAutoTuneConsent`** (one surface) | separate consent — a profile flip is heavier than one auto-delta step |
| F7 | RESILIENT in auto-promotion | **Exclude** (Resilient→Typical has no gate-floor effect) | include on first mild evidence (cosmetic at the gate) |
| F8 | `StableWindowsCompleted` as relax corroboration | **Document-reserved, not wired v1** (duplicates `OffsetDeltaPp`) | fold into `TrendStableOrImproving` as CORROBORATING-ONLY (cannot bypass the floors) |
| F9 | `OverlayDeclineCount` | **Document-reserved** (ambiguous UI-friction vs symptom) | wire as tighten/promotion BIAS only — but only behind a working downgrade path + a measured corroborating term |