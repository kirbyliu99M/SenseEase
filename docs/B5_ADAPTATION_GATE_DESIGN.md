# B5 — Chatbot Retrospective → Adaptation, via a Deterministic Gate (Design **v2**, 2026-06-17)

**Status:** DESIGN — awaiting Kirby's sign-off before any code.
**v2** rewrites v1 after a 3-angle adversarial red-team of the gate design (run *before* code, because with
no shadow mode the gate is the only pre-production safety net). The red-team found 3 HIGH design defects
that v1's reversibility/clamp/floor claims were false against the real code; §10 lists what changed.
Builds on `docs/LEARNING_LOOP_PLAN.md` (item B5) and the B1/B2/B3 work on `alpha1-w2-engine-integrity`.

## 1. The problem B5 solves

The chatbot builds a longitudinal picture of the user (durable observations + the numeric behaviour
trend) but **all of it dead-ends in the prompt** — it never changes the protection delivered. B5 routes
it into **output ①** (the overlay), like FMS (B1) and the manual nudge (B2). B5 is the FIRST path that
lets a *background* analysis change protection with no fresh user action — a wall today's architecture
raises on purpose. So B5 carries its own safety apparatus: **the gate.**

## 2. Architecture — "the LLM proposes, deterministic code disposes"

```
 STAGE 1 (UNTRUSTED)                    STAGE 2 (TRUSTED, DETERMINISTIC, PURE)
 UserModelRetrospectiveAnalyzer  ───▶   AdaptationGate.Evaluate(directionOnly, MeasuredState)
 LLM emits direction hint only           • recomputes ALL evidence from measured data
 (+ render-only rationale text)          • sets magnitude itself (min step) — never the LLM
                                         • asymmetric + composed-floor + clamp + rate + cold-start
                                         → Apply(step) | Reject(reason) | Defer
                                                        │ Apply
                                                        ▼
                              DEDICATED auto-channel field (NOT _personalPressureScale)
                              composed as a bounded factor; own persisted cumulative budget;
                              Reset zeros it toward protection; subtle in-chat notice + Undo
```

**The security comes entirely from Stage 2.** Two hard invariants make that real, and the red-team
showed v1 violated both:
- **The LLM influences DIRECTION ONLY.** Not magnitude, not which axis, not timing. The gate sets the
  step size (always the minimum meaningful step) and picks the single axis. (v1 let the LLM "accelerate
  within clamps" → it would always pick max relax. Removed.)
- **Evidence is recomputed from MEASURED data the gate trusts — never the LLM's claim** — and "measured"
  is defined precisely (§4) so it can't be poisoned through chat.

## 3. The dedicated auto-channel (fixes the v1 Apply-seam defect)

v1 routed Apply through `ApplyChatbotOverride(AdjustSensitivity)`, which **absolute-sets the shared
`_personalPressureScale`**, persists only in the profile snapshot, and writes nothing to the adjustment
log. That made v1's reversibility, cumulative-clamp, and no-overwrite guarantees false. v2 introduces a
**separate auto-channel**:

- A new persisted field — `AutoAdaptTriggerDeltaPp` (the auto-channel's signed contribution to
  `EffectiveTrigger`, in pressure points) — **distinct from `_personalPressureScale`** (the user/load
  channel). Composed: `EffectiveTrigger = clamp(base/userScale) + AutoAdaptTriggerDeltaPp`, then the
  cohort floor (§5) is re-asserted. The user channel and the auto channel never share a field, so they
  can't overwrite each other (fixes the stale-proposal-erases-user-tighten bug).
- **Its own cumulative budget**, anchored to a **persisted opt-in-time baseline** (a fixed reference),
  not the live value — so percentage-of-moving-target drift and restart/Reset budget-refill are
  impossible. `|AutoAdaptTriggerDeltaPp| ≤ AutoTriggerSpanPp` at all times, both directions.
- **Reversibility is explicit, not inherited.** The AdjustmentRecord (`Source="auto-gate"`) is
  **audit-only** — `FileAdjustmentLog.WeightFor` sums `Source=="user"` only, so v1's "Reset tares it
  like a nudge" was false. Instead **Reset explicitly zeros `AutoAdaptTriggerDeltaPp` toward more
  protection** (and re-derives `EffectiveTrigger` + re-asserts the cohort floor), in `ResetPersonalization`.
  Undo from the in-chat notice does the same for the single most-recent change.
- **Re-composed + re-floored at every boot** from the persisted field, so the displayed state always
  matches reality (no "notice says eased, but restart silently reverted it" inconsistency).

## 4. Evidence model — "positive evidence of tolerated exposure", measured-only

The red-team's deepest finding: a relax gate built on *absence of distress* is fooled by avoidance and
under-reporting. v2 reframes relax to require **positive evidence the user tolerated real load** — not
merely that nothing looked bad.

- **Measured-only.** The FMS corroboration uses `Measured==true` (`fms_report`) rows **only**;
  `nl_daily`/`fms_estimate` rows (chat-derived, prompt-injectable) are **excluded** from both the count
  and the value checks. This is a hard invariant enforced in the `MeasuredState` assembly contract via a
  single shared helper (so it can't be re-derived wrong). Test: N `nl_daily` low rows + 0 measured ⇒
  relax REJECTED.
- **ON-state + worst-case, not blended mean.** Relax keys on FMS captured under protection-ON, and on
  the **worst recent report** (p75/max over the window), so one fresh bad episode blocks relax and a
  "low because protection was on" mean can't argue for removing protection.
- **Tolerated-exposure requirement (anti-avoidance).** Relax requires the late-window load
  (`SessionMinutes`, `OfiP90`) to be **at or above the user's own established baseline**. A *declining*
  exposure/OFI profile (the avoidance signature) **blocks** relax instead of reading as "improving."
  `spike_rate=improving` with simultaneously falling OFI + session minutes = avoidance, not adaptation.
- **Coverage.** Reject relax if reports cluster in time (e.g. all in one day) or if silent days
  dominate the window — silence ≠ comfort.
- The behaviour-trend arm (`ofi_p90_trend`, `spike_rate` from `behavior_log.jsonl`) is sensor-derived
  and **un-injectable** — relax requires it *in addition* to the measured FMS arm.

## 5. Floor — profile-aware, on the composed result, vestibular excluded from auto-relax

v1 cited `PassesProtectionFloorGuard`, which only inspects `GridCoord.Band` — the pressure-scale axis
v2 moves is orthogonal and was ungated. v2:

- **Vestibular: NO auto-relax at all.** The gate replicates the existing `HabituationOverrideApplicator`
  vestibular-relaxation intercept (the gate is a new path that would otherwise bypass it). And the
  composed `EffectiveTrigger` for a vestibular user is hard-capped `≤ VestibularTriggerPressureOverride
  (50)` after any auto-change and at boot.
- **SENSITIVE: the auto-channel may never net-increase `EffectiveTrigger`** (no net relax).
- **The floor is checked on the COMPOSED protection** (user scale + auto delta + selector band-bias +
  user nudge), not per-channel — so two individually-legal relaxations can't sum to an illegal one.

## 6. Direction policy, magnitude, rate

- **TIGHTEN — data-driven, independent of the LLM.** The gate applies a small clamped tighten when
  *measured* evidence worsens (`ofi_p90_trend` rising / `spike_rate` worsening / fresh discomfort),
  **regardless of whether the LLM proposed it** — mirroring B1's monotone-up selector term. This closes
  the "inject a HOLD to suppress a needed tighten" hole and makes tighten a data floor, not an LLM gate.
- **RELAX — high bar, all required:** §4 evidence (measured-only, ON-state worst-case, tolerated-exposure,
  coverage) AND behaviour trend stable/improving AND a **single minimum step** AND §5 floor AND rate (below)
  AND no recent discomfort/tighten (§7).
- **Magnitude is the gate's, not the LLM's:** every auto-change is one minimum step. A single **combined
  protection budget** spans both axes (pressure-scale + band-bias in a common protection-equivalent unit);
  only **one** axis moves per change — they can't stack.
- **Rate:** no auto-**relax** within `RelaxMinIntervalDays` of **any** prior auto-change (either
  direction); a real per-week max-change counter; a per-day **net-relax cap** so tighten→relax can't
  net-weaken inside one interval. The **user's own** manual relax/Reset is **never** rate-limited by the
  auto-channel.
- **Composed slew limit (visual-transient safety):** an auto trigger-shift counts against the same
  visual-transient budget as a mask flip — the gate caps per-smoothing-window composed FOV change and
  tier change attributable to an auto change (≤1 tier, ≤ N pp FOV), since lowering `EffectiveTrigger`
  also makes the mask appear sooner and the FOV collapse faster (the back-door v1 ignored).

## 7. Cold-start + suppression (persisted, not session-only)

- **Cold-start as a ramp, not a cliff.** The **first** auto-relax requires a margin above steady-state
  minimums (≥ 14 behaviour days AND ≥ 8 measured reports), decaying to the steady-state minimums after.
  The trend check refuses to declare "stable" until each half-split has ≥ N days. (v1's exact-equality
  day-7/count-5 cliff on co-aligned windows is gone.)
- **Suppression on persisted wall-clock instants.** The 48h post-discomfort relax suppression keys on
  **persisted** timestamps — last `fms_report` above a discomfort threshold, and last manual tighten —
  written into the profile snapshot. (v1 relied on a session-only `_lastManualFeedbackAtUtc` and an
  aggregate that had discarded the timestamp; a restart cleared the suppression.)

## 8. Transparency, scope, consent (Kirby decisions)

- **Subtle in-chat notice** (matching the screenshot), e.g. *"I eased your overlay a little — your recent
  check-ins have been comfortable. Undo?"* — zero friction, one-tap reversible, reusable system-notice
  pattern (hard policy for all chatboxes; honoured by the UI reconstruction). The rationale string is
  **render-only**, `SanitizeRationale`'d, and **never re-enters any computation** (the Accept-gate it was
  designed for is gone, so it's untrusted display text only).
- **Scope:** auto-apply touches only the pressure axis (`AutoAdaptTriggerDeltaPp`) and a bounded ±1
  selector band-bias — **not** the mask/shader directly (but see the §6 slew limit for the indirect path).
- **Consent:** disclosed opt-in, default OFF.

## 9. Implementation seams + test plan

**Seams:** Stage 1 extends `UserModelRetrospectiveAnalyzer` to emit a typed direction hint (reuse
`HabituationOverrideParser`; control fields are enums/numbers, never trusted for magnitude/target).
`AdaptationGate` is a new **pure** type in `SenseEase.Core` (no I/O); `MeasuredState` is assembled by the
App and handed in (mirrors B1's FMS push), through the §4 measured-only helper. Apply writes the new
`AutoAdaptTriggerDeltaPp` (NOT `ApplyChatbotOverride`), re-composes `EffectiveTrigger`, re-asserts the
§5 floor, logs the audit record, and raises the in-chat notice.

**Test plan (gate is the safety net ⇒ exhaustive pure-function coverage):**
- measured-only: `nl_daily` rows never corroborate relax;
- self-selection/worst-case: a window of low means + one fresh high report ⇒ relax rejected;
- avoidance: falling OFI + falling session minutes ⇒ relax rejected (read as avoidance, not improvement);
- asymmetry + independent tighten: worsening data tightens even with no/HOLD LLM proposal; monotonicity (discomfort never relaxes);
- floor: vestibular auto-relax rejected outright + composed `EffectiveTrigger ≤ 50` for vestibular; SENSITIVE net-trigger never rises; floor checked on composed value;
- magnitude/axis: a change moves exactly one axis by one step; LLM-supplied magnitude is ignored;
- budget: |auto delta| ≤ span vs the **fixed opt-in baseline** across N consecutive relaxes; Reset between two relaxes does NOT refill the budget;
- rate: no auto-relax within the interval of any prior auto-change; per-day net-relax cap holds across tighten→relax;
- reversibility: Reset zeros `AutoAdaptTriggerDeltaPp`, re-derives `EffectiveTrigger`, re-asserts floor, survives save/load; Undo reverts the last change;
- composition: auto + B1 FMS term + B2 nudge + load path don't double-apply, don't breach the composed floor, and don't exceed the slew limit;
- restart: persisted auto delta re-composes consistently; vestibular boot cap re-asserts;
- anti-injection: a proposal whose direction contradicts the measured data is rejected (data wins).
Plus the established adversarial review on the diff before commit.

## 10. What changed from v1 (red-team deltas)

| v1 claim | Red-team finding | v2 fix |
|---|---|---|
| Apply via `ApplyChatbotOverride(AdjustSensitivity)` | absolute-set on shared, log-invisible field | dedicated `AutoAdaptTriggerDeltaPp` channel (§3) |
| "Reset tares it like a nudge" | `WeightFor` is user-only; Reset never touches scale | Reset explicitly zeros the auto field toward protection (§3) |
| reuse `PassesProtectionFloorGuard` | band-axis only; scale axis ungated; even max-protective scale under-protects vestibular | profile-aware floor on composed result; vestibular excluded from auto-relax (§5) |
| "FMS low" via mean | self-selection + no recency + ignores ON/OFF + includes `nl_daily` | measured-only, ON-state, worst-case, anti-injection (§4) |
| "trend stable/improving" | reads calm during avoidance | positive tolerated-exposure required (§4) |
| LLM "accelerates within clamps" | LLM owns magnitude/target; dual-channel stacking | LLM = direction only; gate sets min step; single combined budget; one axis (§2,§6) |
| tighten "low bar" on LLM proposal | injectable HOLD suppresses needed tighten | independent data-driven tighten authority (§6) |
| ±15% cumulative clamp | percentage-of-moving-target drift; Reset/restart refill | fixed persisted opt-in baseline; budget survives Reset (§3) |
| session-only suppression | cleared by restart | persisted discomfort/tighten timestamps (§7) |
| mask scoped out | EffectiveTrigger drop makes mask/FOV snap anyway | composed slew limit counts auto-shift against the transient budget (§6) |

## 11. Open items for sign-off (proposed defaults; change any)

| Knob | Proposed default |
|---|---|
| `MinFmsForRelax` (steady state) | ≥ 5 measured ON-state reports |
| first-relax cold-start margin | ≥ 14 behaviour days AND ≥ 8 measured reports |
| relax FMS "low" statistic | window p75 (worst-case), under protection-ON |
| tolerated-exposure rule | late-window OfiP90 + SessionMinutes ≥ user baseline; reject if declining |
| `RelaxMinIntervalDays` | 7 (vs any prior auto-change) |
| per-week max auto-changes | 2 |
| `RelaxSuppressAfterDiscomfortHrs` | 48 (persisted timestamps) |
| `AutoTriggerSpanPp` (cumulative, both directions, vs opt-in baseline) | ±12 pp of `EffectiveTrigger` |
| per-change step | 1 minimum step, one axis only |
| composed slew cap per window | ≤ 1 tier, ≤ N pp FOV |
| vestibular | no auto-relax; composed `EffectiveTrigger ≤ 50` |
| scope | pressure axis + ±1 band-bias; no direct shader |
| consent | disclosed opt-in, default OFF |
