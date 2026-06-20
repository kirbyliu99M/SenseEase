# B6 Stage 4 — Bidirectional Elasticity (redesign)

**Status:** Sign-off-ready design. DESIGN ONLY — no code this run.
**Supersedes:** the one-way "protection-floor" ratchet draft (RT-3.1 floor-removal, RT-3.2 sticky-promotion notice, forbid-downgrade forks — all DROPPED, built on the wrong premise).
**Author lens:** FINALIZER, folding the draft + 3 red-team reports.
**Scope axis:** the **FOV-OFFSET ELASTICITY** axis (the profile / `HabOffsetCapPp` lever) — a DIFFERENT lever from the already-shipped B6 Stages 1–3 **TRIGGER** axis (`EffectiveTrigger` auto-delta). The two must stay orthogonal and non-compounding.

---

## 0 · Finalized decisions (Kirby sign-off 2026-06-21)

**APPROVED — proceed through the stages (4a → 4b → 5), report at each boundary.** Build with Opus agents, maximize workflow parallelism under server constraints.

**F1 — names/order (low→high elasticity), CHOSEN = `Reactive` / `Adapting` / `Resilient`:**

| Int order | Member | = old member | Params (byte-identical) | Identifier change? |
|---|---|---|---|---|
| 0 | **`Reactive`** | `Sensitive` | cap 15, step 1.0, window 360k, limit 1 | **RENAME** Sensitive→Reactive |
| 1 | **`Adapting`** | `Typical` | cap 25, step 1.5, window 180k, limit 2 | **RENAME** Typical→Adapting |
| 2 | **`Resilient`** | `Resilient` | cap 30, step 2.0, window 120k, limit 2 | **KEEPS NAME**, int 0→2 |

So only TWO identifiers are renamed (`Sensitive`→`Reactive`, `Typical`→`Adapting`); `Resilient` keeps its name but its INT moves 0→2. The int REORDER still hits every order-dependent consumer (§3.3) — `Resilient` references compile unchanged but their *value* flips, so the int-cast audit (`IsLessRestrictive.Order()` etc.) is mandatory.

**Persistence alias map** (read-side, every `Enum.TryParse` site): `SENSITIVE`→`Reactive`, `TYPICAL`→`Adapting`, `RESILIENT`→`Resilient` (the last is a direct parse — kept in the map for explicitness). Write-side emits the NEW canonical strings (`REACTIVE/ADAPTING/RESILIENT`).

**Other forks:** F2 = write NEW strings (alias old on read). F4 = UP needs **2** consecutive qualifying evals. F5 = DOWN = **10-day MAX, no debounce**. F6 = **leave shader untouched on AUTO moves** (explicit/calibration still re-seed). F7 = **one-line** prompt glossary.

**F3 — RESOLVED: the dashboard profile pills are being RETIRED.** Kirby: the dashboard is being dismantled (console → SettingsWindow consolidation). So the LIVE `OnUserProfileSelected`/`SelectProfileCommand` path the red-team found is on its way out, and the "auto vs explicit-user precedence" fork largely dissolves. **Plan impact (Stage 4a must verify):** (a) while the pills still exist in the tree, the rename MUST still update their labels/command-args; (b) keep the `_profileSource` distinction (`auto_elasticity` vs `user_dashboard`/calibration) so Reset semantics (§6.2) and any surviving explicit path stay coherent — auto never overrides an explicit `user_dashboard`/calibration source within the cooldown, harmless if the pills are gone; (c) confirm at 4a whether the pill code is still present or already removed by concurrent settings work, and note it. No design conflict — the bidirectional auto-classifier simply becomes closer to the sole live writer, which is the intended end-state.

> **NOTE for the body below:** §1–§10 were authored with the placeholder names `Guarded/Adapting/Adapted`; they have been updated to the chosen `Reactive/Adapting/Resilient`. Where a sentence's rationale was written for "Guarded = we guard exposure", read it as "Reactive = high reactivity / low adaptive capacity".

---

## 1 · Problem & the corrected elasticity model

### 1.1 What the profile actually encodes (corrected 2026-06-28)
`HabituationProfile` encodes a patient's **neural learning elasticity** — how fast they can adapt to optical-flow / motion and *earn* relaxation — NOT "protection intensity". Code + `docs/HABITUATION-PROFILE.md` confirm: "how aggressively the habituation engine RELAXES the FOV mask over time". Concretely the profile sets `HabOffsetCapPp` (the ceiling on the earned `_offset`), `StepMagnitudePp`/`StepIntervalMs`/`StableWindowMs` (relax cadence), `MinSamplesForFullWeight`, and `SevereStrikeLimit`.

- **RESILIENT** = low reactivity / **fast** habituation → cap **30**, step 2.0, window 120k, strike-limit 2.
- **TYPICAL** = moderate → cap **25**, step 1.5, window 180k, strike-limit 2.
- **SENSITIVE** = high reactivity / cautious → cap **15**, step 1.0, window 360k, strike-limit 1.

A patient's elasticity genuinely **rises** (habituation succeeds → more adapted) and **falls** (bad spells). The learning loop should therefore move the profile **both ways** — self-correcting — not a one-way ratchet. Because it self-corrects, the prior "silent + irreversible" worry dissolves; disclosure lives at the `AdaptiveAutoTune` opt-in.

### 1.2 Why the prior premise was wrong
The one-way draft treated a lower-cap profile as a "protection floor" and treated downgrade as "removing protection". But the cap is an **elasticity ceiling on earned relaxation**, not a protection floor. Moving a patient UP the elasticity scale on demonstrated tolerance is the *therapeutic goal*; moving DOWN on worsening is *caution*. RT-3.1 (downgrade removes a floor), RT-3.2 (sticky-promotion notice), and the forbid-downgrade forks are all DROPPED.

### 1.3 The two-axis coherence problem (the core regression risk)
Stages 1–3 already move `EffectiveTrigger` (when the mask trips) through the `AdaptationGate` + `AdaptationCoordinator` + `ApplyAutoAdaptDelta`. Stage 4 adds a SECOND auto-mutating axis (the profile / offset-cap). If both move on the same evidence in the same direction they **compound** (double-relax or double-tighten) — exactly the under/over-protection failure mode. The design's central rule (§5) makes them ONE non-compounding loop.

---

## 2 · The bidirectional auto-classifier

### 2.1 Evidence (MEASURED-ONLY, un-injectable, reuse Stage-2 plumbing)
The classifier consumes **only** `AdaptationEvidence` outputs already wired for the gate — never raw chatbot text, never `nl_daily`:
- **UP evidence (demonstrated tolerance):** the SAME bar the gate uses to *permit a relax* — `FmsEvidence` measured-ON reports with `CoverageOk`, `WorstRecentFmsOn < RelaxFmsLowThreshold`, `!MeasuredDiscomfortInWindow`, AND `ExposureEvidence.TrendStableOrImproving && ExposureAtOrAboveBaseline && !ExposureDeclining` (tolerated exposure, not avoidance). UP fires only when the patient is **at or near the current `HabOffsetCapPp`** (they've consumed the elasticity they have and stayed tolerant) — see RT Angle-1 fix.
- **DOWN evidence (sustained worsening):** `ExposureEvidence.BehaviorWorsening` OR a sustained run of measured discomfort. **DOWN recency must NOT lag** — see §2.3.

### 2.2 Hysteresis (anti-churn, both directions)
- **Durable-row evidence only** — the classifier reads the *persisted* evidence horizons, NOT any in-memory streak. No boundary-noise flip.
- **Sustained requirement:** UP needs tolerance-at-cap sustained across **multiple evaluation passes** (proposed default: 2 consecutive qualifying evals over ≥ `RelaxMinIntervalDays`). DOWN needs sustained worsening over a SHORT MAX-recency window (§2.3).
- **One move per pass, one step:** at most ONE profile step (one adjacency, e.g. Adapting→Resilient or Adapting→Reactive) per evaluation. Never skip a tier.
- **Rate-limited:** reuse the gate's `DaysSinceLastAutoChange` / `MaxAutoChangesPerWeek` budget so profile-moves and trigger-moves share ONE weekly change budget (this is the non-compounding coupling, §5).

### 2.3 DOWN must not lag UP (RT Angle-1 HIGH — ACCEPTED)
`ExposureEvidence.BehaviorWorsening` is a 28-day half-split — blind to a fresh 3-day crash for 1–2 weeks while UP could already have raised the cap. **Fix:** DOWN uses a **10-day MAX-recency window with NO debounce** (mirroring the Stage-2 `recencyWindowDays` worst-case-MAX design already in `MeasuredOnFms`): any sustained worsening in the last 10 days fires DOWN immediately; UP is the slow, multi-pass, sustained-tolerance-at-cap direction. **Asymmetry is the safety property:** tighten fast, relax slow.

### 2.4 Vestibular = hard override, NEVER auto-changed (RT Angle-1 + Angle-3 HIGH — ACCEPTED)
The classifier checks `_vestibularProtection` (via the snapshot's `Vestibular` flag) **at the engine entry point, before any move**, and HOLDs. The gate's `Cohort.Vestibular` guard is at the GATE (line 98), not at the profile mutate — so the profile move must enforce its OWN vestibular HOLD at the new engine method (§4.1). Vestibular users keep `EffectiveTrigger=50`, `SevereStrikeLimitOverride=1`, and their fixed profile.

### 2.5 Fail-closed
- Any non-finite evidence (NaN/±Inf in the FMS/exposure/trigger inputs) → **Hold** (mirrors `AdaptationGate.Evaluate`'s `non-finite-state` guard; NaN comparisons fall open toward relax, the worst mode).
- Consent OFF (`AdaptiveAutoTuneConsent != Enabled`) → the classifier never runs (same single gate as Stage 1–3, `MaybeRunAdaptationTuning` line 2395).
- Snapshot-vs-live mismatch inside the lock → Hold (§4.2 atomicity).

---

## 3 · Reorder + rename

### 3.1 New names & order (low→high elasticity)
Order the enum **low→high elasticity** to match the habituation PROGRESSION (a patient moves UP as they adapt). Names mean **adaptation capacity / elasticity**, not protection:

| Int order | New member | Old member | Meaning | Params (UNCHANGED) |
|---|---|---|---|---|
| 0 | **`Reactive`** | `Sensitive` | high reactivity, low elasticity, cautious relax | cap 15, step 1.0, window 360k, limit 1 |
| 1 | **`Adapting`** | `Typical` | moderate elasticity, mid relax | cap 25, step 1.5, window 180k, limit 2 |
| 2 | **`Resilient`** | `Resilient` | low reactivity, high elasticity, fast relax | cap 30, step 2.0, window 120k, limit 2 |

**Order INVERTS vs today** (today `Resilient=0…Sensitive=2`). The enum INT order now ASCENDS with elasticity, so "move up the elasticity scale" == "increment the enum". This is the whole point of the reorder, but it means **every order-dependent consumer must flip in the SAME commit** (§3.3).

> Names chosen so nobody re-imports the protection-floor framing. `Reactive` = "we guard this patient's exposure" (capacity-low), `Adapting`/`Resilient` = capacity rising. Final naming is an open fork (§9 F1) if Kirby prefers `Reactive/Adapting/Resilient` etc.

### 3.2 Persisted-file back-compat migration (RT Angle-2 + Angle-3 HIGH — ACCEPTED)
The on-disk string is written by `BuildProfileSnapshotCore` (line 2019) as `CurrentProfile.ToString().ToUpperInvariant()` → old files contain `"RESILIENT"/"TYPICAL"/"SENSITIVE"`. The chatbot `profile_override` strings and the `CurrentProfile` UI string (MainViewModel 364) use the same canonical upper-case.

**Value-preserving migration — a bidirectional alias map applied at EVERY `Enum.TryParse<HabituationProfile>` READ site:**

| Old persisted string | New member |
|---|---|
| `RESILIENT` | `Resilient` |
| `TYPICAL` | `Adapting` |
| `SENSITIVE` | `Reactive` |

The three READ sites that MUST get the alias (RT Angle-3 enumerated them — the draft only named one):
1. `MockInferenceEngine.cs:271` — boot restore.
2. `HabituationOverrideProcessor.cs:265-266` — chatbot `IsRelaxationProposal` proposed + current.
3. `MainViewModel.cs:592` — dashboard-pill `OnUserProfileSelected` (NOTE: this path is LIVE, see §6.1).
   Plus `HabituationOverrideProcessor.cs:183` (range-check parse).

**Write side:** keep writing a STABLE canonical string. **Decision (open fork F2, proposed default):** write the NEW names (`GUARDED/ADAPTING/ADAPTED`) and alias the OLD names on read only. This makes new files self-describing; old files still load. A round-trip test asserts a v3 file with `"SENSITIVE"` loads to `Reactive` AND maps to `Cohort.Sensitive`.

### 3.3 Order-dependent consumers that MUST flip atomically (RT Angle-3 HIGH — ACCEPTED)
1. **`HabituationProfileParams.For` / `ResolveDefaultsFor`** (HabituationProfile.cs:183, 214) — switch arms keyed by member NAME; the `!= Sensitive` shortcut at line 219 becomes `!= Reactive` (Reactive keeps the tightened shader; Adapting/Resilient keep `ShaderParams.Default`). **Params stay byte-identical per member** — only the member NAME on each arm changes.
2. **`HabituationOverrideProcessor.IsLessRestrictive` Order()** (line 287-293) — hard-codes `Resilient=0,Typical=1,Sensitive=2`. The semantic ("less restrictive" = more elastic = higher cap = LOWER `Order` rank, since `IsLessRestrictive` returns `Order(proposed) < Order(current)`) must be PRESERVED. **⚠ CORRECTION (verified against code 2026-06-21): `Order()` must NOT become `(int)p`.** `Order` ranks RESTRICTIVENESS, which is the INVERSE of elasticity; the new enum int ASCENDS with elasticity, so restrictiveness DESCENDS with the int. The correct mapping keeps the most-elastic member (`Resilient`) at `Order 0` (least restrictive): `Resilient→0, Adapting→1, Reactive→2` — i.e. `2 - (int)p`, NOT `(int)p`. A naive `(int)p` would INVERT the relaxation check (it would call `Resilient` more-restrictive than `Reactive`). Keep the explicit switch for clarity. Pin with a test: `IsLessRestrictive(proposed:Resilient, current:Reactive) == true`; `IsLessRestrictive(Reactive, Resilient) == false`.
3. **Gate Cohort mapping** (MainViewModel.cs:2424-2425) — keys on `snap.Profile == HabituationProfile.Sensitive` by VALUE. Rewrite to `== HabituationProfile.Reactive` → `Cohort.Sensitive`; Adapting/Resilient → `Cohort.Typical`; vestibular → `Cohort.Vestibular`. **If this line is missed, the most-cautious cohort silently falls through to `Cohort.Typical`, removing the no-net-relax floor — catastrophic under-protection.** This is the single highest-risk line in the rename.
4. **Engine field defaults** (HabituationEngine.cs:77-78) — `_params = Resilient`, `_currentProfile = Resilient`. These become the NEW name for the same params (`Resilient`) — see §3.4 for the seed-discrepancy resolution.
5. **Boot upgrade fallback** (MockInferenceEngine.cs:258-260) — "file absent → default" path resolves to the same member as today.
6. **Fresh-user seed** (`ApplyAutoProfilingStartDefaults`, line 862) — `SetProfile(HabituationProfile.Typical, "auto_profiling_default")` → becomes `Adapting` (the renamed `Typical`, params unchanged).

### 3.4 Seed/default discrepancy resolution (RT Angle-3 MED — ACCEPTED, no behavior change)
Docs say RESILIENT default; the engine FIELD default is `Resilient`/L… but the fresh-user SEED is `Typical`/L5 (`ApplyAutoProfilingStartDefaults`). **Resolve by documentation, NOT by changing which params a user gets:**
- The fresh-user seed STAYS `Adapting` (= today's `Typical`: window 180k, cap 25, limit 2). `AutoProfilingStartDefaultsTests` and the Merge-Point-A "RESILIENT numerics == SenseDefaults" pin must see IDENTICAL effective params.
- The engine field default (only reachable before any seed/restore) STAYS the renamed `Resilient` (= today's `Resilient`).
- Add a test: a profile-less upgrade boot lands on the SAME effective params (cap/cadence/strike-limit/cohort) as today — **no silent cohort shift for existing users.**

---

## 4 · The single engine commit method (RT Angle-3 HIGH ×2 — ACCEPTED)

The classifier MUST NOT route through the existing `SetHabituationProfile`/`SetProfile` path. Add a NEW engine entry — proposed `AutoReclassifyProfile(HabituationProfile target, string source="auto_elasticity")` — and a NEW App coordinator step that commits profile + trigger in ONE locked section.

### 4.1 Strike preservation, not zeroing (HIGH-1)
`SetProfile` (HabituationEngine.cs:221) UNCONDITIONALLY zeros `_severeStrikeCount`. That was conservative for one-way promote; for a bidirectional move it WIPES in-flight strike evidence on a relax-direction move (under-protection-near-threshold bug). `AutoReclassifyProfile` must:
- Swap `_params`/`_currentProfile`/`_profileSource` and re-baseline `_effectiveWindow`/`_effectiveStepInterval` **WITHOUT zeroing `_severeStrikeCount`** (tare-not-zero).
- Re-evaluate the count against the NEW profile's `SevereStrikeLimit` (honoring `SevereStrikeLimitOverride`) in the SAME locked body: if `_severeStrikeCount >= newLimit - StrikeLimitEpsilon`, immediately `_offset = 0.0; _severeStrikeCount = 0.0` (honor the new stricter limit) rather than carrying a stale sub-limit count.
- Explicit-user (`user_dashboard`) / calibration `SetProfile` KEEP today's zero-on-switch semantics (unchanged).
- **Test:** count=1.5 on Adapting(limit2)→Resilient(limit2) preserves 1.5; Resilient(count1.5,limit2)→Reactive(limit1) immediately zeros offset (1.5 ≥ 1).

### 4.2 Deferred offset clamp on DOWN-moves (RT Angle-3 MED — ACCEPTED, anti-churn)
A DOWN-move lowers the cap (e.g. 25→15). `SetProfile`'s existing instant re-clamp (`if (_offset > cap) _offset = cap`) would HARD-truncate a healthy earned offset on every down-flip, and the patient does NOT regain it on the up-flip (offset only re-earns via stable-window steps) → noisy classification **ratchets earned habituation downward**. **Fix:** on an AUTO down-move, do NOT hard-truncate `_offset`. Let the existing `Step()` cap-clamp (line 333, `_offset = Math.Min(cap, …)`) bleed it down only as relaxation re-evaluates. Combined with the §2.2 durable-row hysteresis, a down-move only fires on genuinely sustained worsening. **The hard clamp is KEPT for explicit-user/calibration `SetProfile`.** Test: oscillating classify in/out of Reactive does NOT monotonically erode `_offset`.

### 4.3 Atomicity — resurrect the single critical section (HIGH-2)
The draft dropped the prior `ApplyGateOutcome` single-critical-section but ADDED a second mutating axis. A separate `_gate` acquisition for the profile flip can interleave with `ApplyAutoAdaptDelta`'s absolute-overwrite (`_autoAdaptTriggerDeltaPp = newDelta`, line 1728), composing a relax against a STALE cohort. **Fix:** a single engine commit method takes `_gate` ONCE and, inside the lock:
1. Re-reads live profile / offset / auto-delta / vestibular.
2. Aborts to Hold on snapshot-vs-live mismatch.
3. Runs the classify decision against LIVE state.
4. Applies profile-flip (§4.1) + deferred offset rule (§4.2) + strike-recheck + the trigger relax/tighten delta — in that one locked body.
The pure gate (`AdaptationGate.Evaluate`) stays OFF-lock; only the APPLY is the locked, live-rechecked commit. Test: classify-down-vs-relax race resolves to the SAFER outcome (relax suppressed if a tighten/down won the live recheck).

---

## 5 · Coherence rule — profile + trigger + selector = ONE non-compounding loop

1. **ONE evidence read per pass** feeds BOTH axes (the §2.1 `AdaptationEvidence` bundle already assembled in `MaybeRunAdaptationTuning`).
2. **ONE weekly change budget** (`MaxAutoChangesPerWeek`, `DaysSinceLastAutoChange`) covers profile-moves AND trigger-moves together — a profile move and a trigger move in the same week count against the same cap, so they cannot stack into a compounded relax.
3. **Direction harmony, not summation:** in a single pass, the loop applies AT MOST one trigger step and AT MOST one profile step, and they may not BOTH relax in the same pass (a profile UP-move OR a trigger relax, never both at once). Tighten/down is allowed to fire on both axes together (safe direction).
4. **Cohort floor is taring, not auto-granting (RT Angle-3 LOW — ACCEPTED):** a DOWN-move that lowers the cohort class (Reactive→Adapting removes the SENSITIVE no-net-relax floor at `AdaptationGate.cs:155`) must NOT instantly unlock a standing-blocked positive auto-delta. The down-move evidence must independently clear the relax bar before any held +delta applies. Test: down-classify from the Sensitive cohort with a standing-blocked +delta does not immediately apply that delta.
5. **Selector (Stages 1–3) unchanged:** the trigger axis stays orthogonal — `SetProfile`/`AutoReclassifyProfile` deliberately do NOT touch `EffectiveTrigger` (HabituationEngine 212-232); the auto-delta composes via `ApplyAutoDeltaToFreshBase`. This orthogonality is PRESERVED.

---

## 6 · Do-not-break checklist

### 6.1 Existing profile-change paths (one is LIVE — corrects the brief)
- **`OnUserProfileSelected` / `SelectProfileCommand` (MainViewModel 589, 1236) → `SetHabituationProfile(source:"user_dashboard")` is a LIVE, wired path.** The brief said switch paths are "dead/severed" — only the *chatbot SwitchProfile* (rejected at `HabituationOverrideProcessor:101`) and the *calibration* path (D-13 deleted) are dead. The dashboard pills still change the profile live. ⇒ The rename MUST update the pill labels/command-arg strings, and `AutoReclassifyProfile`'s `source="auto_elasticity"` must coexist with `user_dashboard` (explicit user wins; auto never overrides an explicit-user profile within the cooldown — open fork F3).
- Chatbot `profile_override` strings (docs §6.2) must still parse via the alias map even though the live switch is severed (the parser still runs).

### 6.2 Engine loops that must NOT regress
- **`_offset` earned-progress loop** (`Step` 320-338): step-up to cap, cap-clamp. Untouched except the deferred-clamp rule (§4.2) which only RELAXES an over-eager truncation.
- **Severe-strike loop** (`RegisterSevereStrike` 383-404, halve-then-zero-at-limit, `StrikeLimitEpsilon`): untouched; `AutoReclassifyProfile` re-checks against the new limit but does not change the handler.
- **Stable-window strike reset** (`Step` 326): unchanged.
- **State machine** (`HabituationPhase` / `EngineState`, pause/cooldown/awaiting-confirm): `AutoReclassifyProfile` must NOT clear `_awaitingUserConfirm` / `_pauseCooldown` (same as `SetProfile` supplement #2).
- **`ResetTransientState` / `ResetTransientStatePreservingPause`** (Q4.5 `_offset` preservation): unchanged.
- **`ResetPersonalization` coherence (RT Angle-3 MED — ACCEPTED):** today Reset does NOT touch the profile and zeros the auto-delta toward protection. With a bidirectional auto-MOVED profile, leaving an auto-classified `Resilient` (high-elasticity) profile while zeroing the protective delta leaves the user MORE relaxed than base after a Reset meant to return toward protection. **Fix:** in `ResetPersonalization`, if the active `_profileSource == "auto_elasticity"`, revert the profile to the fresh-user seed (`Adapting`/L5) BEFORE zeroing the auto-delta, in the existing `_gate`-held body, and persist. Explicit-user (`user_dashboard`) / calibration profiles are LEFT UNTOUCHED by Reset (today's contract). Test: auto-moved-`Resilient` + Reset → profile back to seed AND auto-delta zeroed (net toward protection).

### 6.3 Stages 1–3 (TRIGGER axis)
- `AdaptationGate`, `AdaptationCoordinator`, `AdaptationEvidence`, `ApplyAutoAdaptDelta`, `ApplyAutoDeltaToFreshBase`, `SnapshotForAdaptation`, the reset-tare + cumulative net-relax logic, the FMS measured-only invariant, the worst-case MAX, the cold-start ramp — ALL unchanged. Stage 4 REUSES them read-only and adds the profile axis under the shared budget (§5).
- The gate's `Cohort` enum and `Vestibular` floor: unchanged in code; only the MainViewModel mapping that PRODUCES the cohort flips names (§3.3 item 3).

### 6.4 Persistence / back-compat
- `habituation.json` (offset-only) and `habituation_profile.json` (v3) schemas unchanged; only the profile STRING value space gains aliases (§3.2). `MergeCarryForward` / additive-field rules unchanged. Old `"RESILIENT/TYPICAL/SENSITIVE"` files load (test-pinned).

### 6.5 Test fixtures (RT Angle-2 MED — ACCEPTED)
36 test files reference the old names; ~30 pin them. **Raw-JSON fixtures are real on-disk strings and MUST keep the OLD strings and assert they LOAD to the new members** — do NOT rewrite the JSON to new names:
- `HabituationProfileStoreTests` (LegacyV1Json), `SettingsClearAndRestartChipTests`, `UserModelStoreTests`, `AutoProfilingStartDefaultsTests`, `MockInferenceEngineWaveB1aTests`, `ChatbotViewModelPhase6Tests`, `HabituationOverrideProcessorTests`, `HabituationProfileTests`, `AdaptationGateTests`, plus the rest of the 36.
- Add a back-compat cap test: old `SENSITIVE` loads → cap 15 / limit 1 / `Cohort.Sensitive`.
- Enum/display references (non-JSON) MOVE to the new names.

### 6.6 Chatbot prompt drift (RT Angle-2 LOW — ACCEPTED)
`HabituationStateBlockBuilder` emits `profile` verbatim into the prompt. A bare `profile:ADAPTED` with no glossary could soften the model's discomfort-probing. **Fix:** add a one-line prompt glossary ("ADAPTED = high adaptation capacity; still probe for discomfort") in zh + en, and keep `ChatbotViewModelPhase6Tests` / `HabituationStateBlockBuilderTests` assertions in sync.

---

## 7 · Staged plan (mechanical rename first, behavior second)

**Stage 4a — mechanical rename + migration (NO behavior change, must be byte-identical numerically):**
1. Rename enum members + flip int order; keep all three param sets byte-identical per renamed member.
2. Update `For`, `ResolveDefaultsFor` (incl. the `!= Reactive` shader shortcut), `IsLessRestrictive.Order()`, the Cohort mapping (2424-2425), engine field defaults, the fresh-user seed, the boot fallback — ALL in one commit.
3. Add the bidirectional alias map at the 4 `Enum.TryParse` read sites; write new canonical strings.
4. Move enum/display test refs; KEEP raw-JSON fixtures on old strings; add round-trip + cohort + seed-parity + back-compat-cap tests.
5. Prompt glossary + builder test sync.
6. **Gate:** full suite green, zero numeric drift, `AutoProfilingStartDefaultsTests` + Merge-Point-A pin unchanged.

**Stage 4b — bidirectional classifier (behind existing `AdaptiveAutoTune` consent):**
1. `AutoReclassifyProfile` engine method (§4.1 strike-preserve, §4.2 deferred clamp, vestibular HOLD).
2. The single locked App commit step (§4.3) folding profile + trigger under the §5 shared budget.
3. UP (multi-pass, sustained-tolerance-at-cap, slow) + DOWN (10-day MAX, no-debounce, fast) classifiers on the §2.1 evidence; fail-closed.
4. `ResetPersonalization` auto-source revert (§6.2).
5. Tests for every RT-accepted scenario.
6. **Gate:** consent-OFF path byte-identical to today; no compounding; vestibular never moves.

**Stage 5 — UVHS** (eyes-on before any protection-shipping commit). No silent ship.

---

## 8 · What the red-team changed (delta table)

| # | Red-team item (sev) | Verdict | Change folded in |
|---|---|---|---|
| A1 | DOWN lags 28d split while UP unbounded (HIGH) | ACCEPT | DOWN = 10-day MAX, no debounce; UP = sustained tolerance-AT-CAP, multi-pass (§2.3) |
| A1 | Vestibular blind at SetProfile, only guarded at gate (HIGH) | ACCEPT | Vestibular HOLD at the new engine method, before any move (§2.4, §4.1) |
| A3-1 | `SetProfile` zeros strikes → bidirectional WIPES in-flight strikes on relax-move (HIGH) | ACCEPT | `AutoReclassifyProfile` tare-not-zero + re-check vs new limit (§4.1) |
| A3-2 | Dropped `ApplyGateOutcome` atomicity while adding a 2nd axis (HIGH) | ACCEPT | Single locked, live-rechecked commit method; pure gate off-lock (§4.3) |
| A3-3 | Rename breaks `IsLessRestrictive.Order()` + value-keyed Cohort map + 3 TryParse sites (HIGH) | ACCEPT | All flipped in one commit; bidirectional alias map at 4 read sites; round-trip test (§3.2-3.3) |
| A3-4 | Instant down-move offset-truncation churns earned progress (MED) | ACCEPT | Deferred lazy clamp on auto down-moves; hard clamp kept for explicit/calibration (§4.2) |
| A3-5 | `ResetPersonalization` leaves auto-moved permissive profile = under-protective Reset (MED) | ACCEPT | Reset reverts auto-source profile to seed before zeroing delta (§6.2) |
| A3-6 | Seed/default discrepancy could regress upgraders if seed member changes (MED) | ACCEPT | Resolve by docs only; seed params byte-identical; parity test (§3.4) |
| A2 | ~30 tests pin old names; raw-JSON fixtures are real files (MED) | ACCEPT | Fixtures KEEP old strings + assert load to new members; enum refs move (§6.5) |
| A2 | Prompt emits profile verbatim → LLM softens probing (LOW) | ACCEPT | One-line zh+en prompt glossary; builder test sync (§6.6) |
| A3-7 | Down-move removes cohort floor mid-budget, unlocks standing +delta (LOW) | ACCEPT | Down-move does not auto-grant; held +delta must independently clear the bar (§5.4) |
| Draft | One-way ratchet / "downgrade removes a floor" (RT-3.1) | DROP | Wrong "protection-floor" premise; bidirectional self-corrects |
| Draft | Sticky-promotion notice (RT-3.2) | DROP | Mechanism is silent/background per Kirby; disclosure at opt-in |
| Draft | Forbid-downgrade forks | DROP | DOWN is a first-class safe direction |
| Draft | Per-change in-chat notice | DROP | Background-only; durable `Source="system"` audit + opt-in disclosure suffice |

**Nothing rejected.** Every red-team finding is either folded in or its premise was the dropped one-way model.

---

## 9 · Open forks (proposed defaults)

| ID | Fork | Options | Proposed default |
|---|---|---|---|
| F1 | Final member names | `Guarded/Adapting/Adapted` · `Reactive/Adapting/Resilient` · `Cautious/Adapting/Adapted` | **`Reactive/Adapting/Resilient`** (CHOSEN by Kirby — keeps 'Resilient' for the high-elasticity end; renames only Sensitive→Reactive, Typical→Adapting) |
| F2 | On-disk write string | write NEW names (alias old on read) · keep writing OLD names forever | **Write NEW** (self-describing files; old still load) |
| F3 | Auto vs explicit-user precedence | auto never overrides `user_dashboard` within cooldown · auto may move after N days even off an explicit pick | **Auto never overrides explicit-user within the weekly cooldown** (explicit pick is a deliberate user signal) |
| F4 | UP sustained-pass count | 2 passes · 3 passes | **2 consecutive qualifying evals over ≥ `RelaxMinIntervalDays`** |
| F5 | DOWN recency window | 7 days · 10 days · 14 days | **10 days, MAX, no debounce** (mirrors Stage-2 recency) |
| F6 | Shader on auto-move | re-seed shader on auto profile change (like A7) · leave shader, move only params | **Leave shader untouched on AUTO moves** (avoid a visible flicker from a silent mechanism); A7 re-seed stays for explicit/calibration |
| F7 | Glossary depth | one-line · full per-profile gloss | **One-line** (token budget) |

---

## 10 · Sign-off invariants (must all hold post-implementation)

1. Vestibular users: profile NEVER auto-moves; `EffectiveTrigger=50`, `SevereStrikeLimitOverride=1` preserved.
2. Consent OFF ⇒ engine state byte-identical to today (no classifier runs).
3. Fail-closed: NaN/non-finite evidence ⇒ Hold; snapshot-vs-live mismatch ⇒ Hold.
4. Strikes preserved across an auto-move (tare-not-zero); re-checked vs the new limit.
5. Auto down-move never instantly truncates earned `_offset`; explicit/calibration still clamps.
6. ONE weekly change budget across profile + trigger; never both relax in one pass; non-compounding.
7. Old `RESILIENT/TYPICAL/SENSITIVE` files load to `Resilient/Adapting/Reactive`; `SENSITIVE→Cohort.Sensitive` (floor intact).
8. Fresh-user seed + upgrade-boot land on identical effective params as today.
9. `ResetPersonalization` returns an auto-moved profile toward protection (seed), zeroes the delta.
10. Stages 1–3 trigger axis, the `_offset`/strike/state loops, and all 36 test files' raw-JSON fixtures unchanged in meaning.
