# B6 Stage 4b — Bidirectional Elasticity Classifier (implementation)

**Status:** CODE-READY. Design signed off in `docs/B6_STAGE4_BIDIRECTIONAL_ELASTICITY.md` (§2/§4/§5/§6/§10). Stage 4a committed (`294ebb1`). All file:line refs verified against the post-4a tree this session.

**Behind the existing default-OFF `AdaptiveAutoTune` consent.** Consent-OFF must stay byte-identical to today: the sole chokepoint is the early-return at `MainViewModel.cs:2411` (`if (UserPreferences.Current.AdaptiveAutoTuneConsent != ProactiveCheckInConsent.Enabled) return;`). Every 4b code path is downstream of it; no engine-side consent read; no streak/log write when consent is OFF.

> **⚙ KIRBY RATIFICATIONS (2026-06-21) — applied below:**
> 1. **F6 = AUDIT-ONLY (overrides §9/§13 "fully silent").** An auto-move DOES emit the durable `Habituation.Profile` event into the Event Log (for diagnostics/audit) — do NOT suppress it at `WriteHabituationLifecycleEvents`. Instead, EXCLUDE `source=="auto_elasticity"` profile events from the **chatbot trajectory builder** only, so the move is auditable but chat stays silent. (Build Step 9 + the `LIFECYCLE_EVENT_SUPPRESSED` test change accordingly: assert the event IS in the Event Log but NOT in the chatbot trajectory.)
> 2. **DOWN stays FAST/protective** (`DownFmsThreshold=6.0`, no debounce) — confirmed.
> 3. **Accept the earned post-UP relax** (the residual in §12 item 2 / §4 RT Angle-3 HIGH #1) — confirmed as the intended therapeutic end-state, bounded to ≤1 relax/weekly-budget.

---

## 0 · The one non-negotiable code fact (verified)

`HabituationEngine.Step()`'s relax loop is `while (_stepAccumulator >= _effectiveStepInterval && _offset < _params.HabOffsetCapPp)` at **HabituationEngine.cs:329-330**; the body's `_offset = Math.Min(cap, _offset + StepMagnitudePp)` is at **333-334**. When `_offset > cap` the loop guard `_offset < cap` is **false**, the body never runs, and `Math.Min` never executes. **Step() does NOT bleed an over-cap offset.** The design §4.2's claim that the existing Step() clamp "bleeds it down" is false. Deferring the clamp on a DOWN-move with no new bleed path strands `_offset > cap` permanently → `HabituationLevel` (`HabituationEngine.cs:101-102`, `100*_offset/cap`) reads >100 and `ComposeFovRadius` over-relaxes (under-protection). **An explicit cap-bleed is therefore MANDATORY (Build Step 2), and per RT it must NOT be gated on the calm/Relaxing branch** (a worsening user — the only one who triggers DOWN — never reaches that branch).

---

## 1 · `HabituationEngine.AutoReclassifyProfile` (new)

Insert after `SetProfile` (which ends at **HabituationEngine.cs:232**). `SetProfile` stays **byte-identical** (still zeros strikes unconditionally at L221, still hard-clamps at L228-229).

```
/// <summary>B6 Stage 4b — AUTO bidirectional profile move. Distinct from SetProfile:
/// tare-not-zero strikes, NO hard offset clamp (Step bleeds it), re-check strikes vs the
/// NEW limit. Caller (ApplyGateOutcome) holds _gate AND enforces the vestibular hold.</summary>
public void AutoReclassifyProfile(HabituationProfile target, string source = "auto_elasticity")
{
    if (target == _currentProfile && source == _profileSource) return;   // no-op parity (cf SetProfile L214)

    _params         = HabituationProfileParams.For(target);
    _currentProfile = target;
    _profileSource  = source;
    // OMIT SetProfile L221 strike-zero — TARE-NOT-ZERO; in-flight strike evidence survives a relax move.

    _effectiveWindow       = _params.StableWindowSeconds;       // identical to SetProfile L225-226
    _effectiveStepInterval = _params.StepIntervalSeconds;

    // Strike RE-CHECK vs the NEW limit, replicating RegisterSevereStrike L390/L394 EXACTLY:
    double effectiveLimit = SevereStrikeLimitOverride ?? _params.SevereStrikeLimit;   // int? ?? int → double
    if (_severeStrikeCount >= effectiveLimit - StrikeLimitEpsilon)   // StrikeLimitEpsilon = L55
    {
        _offset = 0.0;             // the ONLY place this method writes _offset
        _severeStrikeCount = 0.0;
    }
    // OMIT SetProfile's hard clamp (L228-229). An over-cap _offset is bled by Step() (Build Step 2), not truncated.

    ProfileChanged?.Invoke(target, source);   // Action<HabituationProfile,string> (L193)
}
```

**Must NOT touch:** `_awaitingUserConfirm` (L62), `_pauseCooldown` (L60), `_externallyPaused` (L61), `_stableElapsed` (L57), `_stepAccumulator` (L58), `_phase` (L73), `_lastSpikeAtUtc` (L64), `_pressureWasAtRecurrence` (L65), `EffectiveTrigger` (L180). Vestibular is NOT checked here — the caller hard-holds (single lock owner).

---

## 2 · `HabituationEngine.Step()` unconditional cap-bleed (MANDATORY)

Place **near the top of `Step()`, AFTER the pause-guard early-returns** (i.e. after the `if (_externallyPaused || _pauseCooldown > 0.0 || _awaitingUserConfirm) { … return; }` block at L280-285) and **before** the recurrence/stability accounting. Runs every live tick, independent of calm credit (**RT Angle-2 HIGH #3 fix** — convergence must not depend on the user being calm, because DOWN fires precisely when they are not):

```
// B6 Stage 4b: lazy convergence after an AUTO down-move left _offset above the lowered cap.
// Explicit/calibration SetProfile hard-clamps on the flip (L228-229), so this only ever bleeds
// the auto-deferred case. Gradual (one StepMagnitudePp/tick), never an instant truncate, so a
// noisy classifier cannot ratchet earned habituation down.
if (_offset > _params.HabOffsetCapPp)
    _offset = Math.Max(_params.HabOffsetCapPp, _offset - _params.StepMagnitudePp);
```

This is the only edit to the existing earned-progress loop and it only RELAXES an over-eager truncation; the existing `_offset >= cap → _stepAccumulator = 0.0` (L336-337) stays. Because the bleed is `Math.Max(cap, …)` it converges to exactly `cap`, never below, never stuck >100% `HabituationLevel`.

> Per-tick `StepMagnitudePp` (1.0–2.0 pp/tick) at the engine tick rate converges within seconds, so over-protection-direction concern does not apply and convergence is prompt even mid-worsening. (RT Angle-2 HIGH #3's "arbitrarily slow" was conditioned on the calm-branch gating, which this top-of-Step placement removes.)

---

## 3 · `MockInferenceEngine.ApplyGateOutcome` — the single locked commit (new)

Place near `ApplyAutoAdaptDelta` (**MockInferenceEngine.cs:1712**). Folds profile flip + trigger delta under ONE `_gate` acquisition, re-reading live state, aborting to Hold on a protective live change. **Does NOT call the public `ApplyAutoAdaptDelta`/`SetHabituationProfile`** (they self-lock AND self-persist → double-persist + a second consistency view). The trigger-delta arithmetic is inlined from `ApplyAutoAdaptDelta` L1716-1737 WITHOUT the re-lock.

```
public AdaptationCommitResult ApplyGateOutcome(AdaptationCommitRequest req)
{
    // Cheap pre-lock no-op (match today's no-op cost).
    if (req.ProfileMove == ProfileMove.Hold && req.TriggerDecision is GateDecision.Hold)
        return AdaptationCommitResult.Hold("no-op");

    lock (_gate)
    {
        // (1) LIVE RE-READ
        var liveProfile = _habituation.CurrentProfile;
        var liveDelta   = _autoAdaptTriggerDeltaPp;
        var liveVest    = _vestibularProtection;
        int liveLevel   = _habituation.HabituationLevel;     // RT Angle-2 HIGH #1: widen the recheck
        double liveStrk = _habituation.SevereStrikeCount;    // RT Angle-2 HIGH #1

        // (2) MISMATCH ABORT (fail-closed) — the snapshot the verdict was computed from must still hold.
        if (liveProfile != req.SnapProfile
            || Math.Abs(liveDelta - req.SnapAutoDelta) > 1e-9
            || liveVest != req.SnapVestibular)
            return AdaptationCommitResult.Hold("snapshot-vs-live-mismatch");

        // (2b) UP-ONLY PROTECTIVE-RACE ABORT (RT Angle-2 HIGH #1, RT Angle-1 MED #4):
        // an UP verdict's justification is "at/near cap + demonstrated tolerance". A severe strike
        // (RegisterSevereStrike, HabituationEngine.cs:383-404) between snapshot and commit collapses
        // _offset / bumps the strike count WITHOUT touching profile/delta/vestibular, so the 3-field
        // check passes. Abort UP (not DOWN — DOWN fails toward protection) on any protective drift.
        if (req.ProfileMove == ProfileMove.Up
            && (liveLevel < req.SnapHabituationLevel || liveStrk > req.SnapStrikeCount + 1e-9))
            return AdaptationCommitResult.Hold("up-stale-protective-change");

        // (3) LIVE BUDGET RE-DERIVE (RT Angle-2 MED #5, RT Angle-3 HIGH #2): the off-lock budget read
        // can tear vs a racing budget/eval row. Re-derive the RELAX-only weekly count from the live log
        // inside the lock and HOLD a relax-direction move if the budget is already spent.
        var liveTiming = AdaptationEvidence.DeriveAutoChannelTiming(
            (_adjustmentLog as FileAdjustmentLog)?.All(), DateTime.UtcNow,
            AutoAdaptParameter, AutoReclassifyProfileParameter);   // extended signature, Build Step 6
        bool budgetSpent = liveTiming.AutoChangesThisWeek >= AdaptationGateConfig.Default.MaxAutoChangesPerWeek;

        // (4) PROFILE FLIP (vestibular hard-hold; adjacency already enforced by the classifier).
        // An UP move consumes a relax slot; a DOWN move is protective and budget-exempt (RT Angle-1 MED #6 /
        // RT Angle-3 LOW). So only UP is blocked by budgetSpent.
        bool profileMoved = false;
        bool profileWouldRelax = req.ProfileMove == ProfileMove.Up;
        if (req.ProfileMove != ProfileMove.Hold && !liveVest
            && !(profileWouldRelax && budgetSpent))
        {
            _habituation.AutoReclassifyProfile(req.ProfileTarget, "auto_elasticity");  // NO SeedShaderFromProfile (F6)
            profileMoved = true;
        }

        // (4b) DOWN-INTO-SENSITIVE RETRACT (RT Angle-3 MED): a DOWN into Reactive (→ Cohort.Sensitive)
        // lowers the floor the standing +delta was never permitted under. DOWN must tighten NET protection,
        // so retract any standing positive auto-delta in the same locked body (mirrors the vestibular retract
        // at ApplyAutoDeltaToFreshBase L1756-1757).
        if (profileMoved && req.ProfileMove == ProfileMove.Down
            && req.ProfileTarget == HabituationProfile.Reactive && _autoAdaptTriggerDeltaPp > 0.0)
        {
            _habituation.EffectiveTrigger -= _autoAdaptTriggerDeltaPp;
            _adjustmentLog?.Append(new AdjustmentRecord {
                Parameter = "AutoAdaptTrigger", Delta = -_autoAdaptTriggerDeltaPp, Source = "system" });
            _autoAdaptTriggerDeltaPp = 0.0;
        }

        // (5) PROFILE BUDGET AUDIT ROW — only a relax-direction (UP) profile move consumes the shared
        // weekly budget (RT Angle-1 MED #6 / RT Angle-3 LOW: DOWN is protective, budget-exempt). Delta=0,
        // distinct Parameter so it never pollutes net-relax / HasEverAutoRelaxed.
        if (profileMoved && req.ProfileMove == ProfileMove.Up)
            _adjustmentLog?.Append(new AdjustmentRecord {
                Parameter = AutoReclassifyProfileParameter, Delta = 0.0, Source = "system" });

        // (6) COHERENCE GUARD for the trigger axis.
        var td = req.TriggerDecision;
        bool wantTriggerApply = td is GateDecision.Apply;
        var apt = td as GateDecision.Apply;
        bool triggerIsRelax = apt is not null && apt.Direction == AdaptDirection.Relax;
        // §5.3 never-both-RELAX (UP) + §5.4 down-move-no-auto-unlock (DOWN). Fire only on a TRUE net-relax,
        // not a benign unwind of a prior tighten (RT Angle-2 MED #6): the unwind keeps the user MORE protected,
        // so let it stack with a profile move.
        bool triggerIsNetRelax = triggerIsRelax && apt!.NewAutoDeltaPp > 0.0
                                 && apt.NewAutoDeltaPp > _autoAdaptTriggerDeltaPp;
        if (wantTriggerApply && triggerIsNetRelax
            && (req.ProfileMove == ProfileMove.Up || req.ProfileMove == ProfileMove.Down))
            wantTriggerApply = false;
        // A relax-direction trigger move also consumes a relax slot — block on the live budget too.
        if (wantTriggerApply && triggerIsNetRelax && budgetSpent)
            wantTriggerApply = false;

        // (7) TRIGGER DELTA inline (mirror ApplyAutoAdaptDelta L1716-1737, NO re-lock, NO self-persist).
        bool triggerMoved = false;
        if (wantTriggerApply)
        {
            double nd = Math.Clamp(apt!.NewAutoDeltaPp, -AutoAdaptTriggerSpanPp, AutoAdaptTriggerSpanPp);
            if (liveVest && nd > 0.0) nd = 0.0;
            double diff = nd - _autoAdaptTriggerDeltaPp;
            if (Math.Abs(diff) >= 1e-9)
            {
                _habituation.EffectiveTrigger += diff;
                if (liveVest && _habituation.EffectiveTrigger > CalibrationProcessor.VestibularTriggerPressureOverride)
                    _habituation.EffectiveTrigger = CalibrationProcessor.VestibularTriggerPressureOverride;
                _autoAdaptTriggerDeltaPp = nd;
                _adjustmentLog?.Append(new AdjustmentRecord {
                    Parameter = "AutoAdaptTrigger", Delta = diff, Source = "system" });
                Current.FovRadiusPercent = ComposeFovRadius(Current.Pressure);
                triggerMoved = true;
            }
        }

        // (8) PERSIST ONCE for both axes. NOTE the F6 lifecycle-event decision (§ Persistence/consent):
        // MaybePersistProfileSnapshot routes through WriteHabituationLifecycleEvents, which emits a durable
        // "Habituation.Profile" event. Suppression for source=="auto_elasticity" is applied in Build Step 8.
        if (profileMoved || triggerMoved)
        {
            Current.HabituationPaused = _habituation.IsPaused;
            _profileSnapshotDirty = true;
            MaybePersistProfileSnapshot();
        }

        return new AdaptationCommitResult(profileMoved, triggerMoved,
            triggerMoved ? "applied" : (profileMoved ? "profile-only" : "hold"));
    }
}
```

Add `private const string AutoReclassifyProfileParameter = "AutoReclassifyProfile";` (exposed as a shared const for the App + `DeriveAutoChannelTiming`).

---

## 4 · `ProfileElasticityClassifier` (new pure type)

`SenseEase.Core/Adaptation/ProfileElasticityClassifier.cs` — static, no I/O, no engine ref, mirrors `AdaptationGate`. Shared types live in the same file or `ProfileElasticity.cs` (namespace `SenseEase.Core.Adaptation`, next to `GateDecision` — keeps Core WPF-free; the App just constructs them).

```
public enum ProfileMove { Hold, Up, Down }
public sealed record ProfileVerdict(ProfileMove Move, HabituationProfile Target, bool QualifiesUp);
public sealed record ProfileElasticityConfig(
    int UpAtCapLevelThreshold, int UpRequiredConsecutiveEvals, double DownFmsThreshold)
{
    public static ProfileElasticityConfig Default => new(
        UpAtCapLevelThreshold: 90,        // top 10% of earned offset (open fork — owner confirm)
        UpRequiredConsecutiveEvals: 2,    // F4
        DownFmsThreshold: 6.0);           // == RelaxFmsLowThreshold (symmetric boundary, F5)
}
public sealed record AdaptationCommitRequest(
    HabituationProfile SnapProfile, double SnapAutoDelta, bool SnapVestibular,
    int SnapHabituationLevel, double SnapStrikeCount,
    ProfileMove ProfileMove, HabituationProfile ProfileTarget, GateDecision TriggerDecision);
public sealed record AdaptationCommitResult(bool ProfileMoved, bool TriggerMoved, string Reason)
{ public static AdaptationCommitResult Hold(string r) => new(false, false, r); }
```

**The classifier (RT Angle-1 HIGH #1/#2/#3, LOW #7 folded — share the gate's predicate, never copy it):**

```
public static ProfileVerdict Classify(
    MeasuredState s, AdaptationHint relaxHint, HabituationProfile current, bool vestibular,
    int habituationLevel, double offsetOverCapGuard, int consecutiveUpQualify,
    ProfileElasticityConfig pcfg, AdaptationGateConfig cfg)
{
    // 1. VESTIBULAR HARD HOLD — also forces QualifiesUp=false so the streak NEVER banks credit
    //    while vestibular (RT Angle-1 HIGH #3).
    if (vestibular) return new(ProfileMove.Hold, current, QualifiesUp: false);

    // 2. FAIL-CLOSED (mirror AdaptationGate L48-52). NaN comparisons fall open toward relax = worst mode.
    if (!double.IsFinite(s.WorstRecentFmsOn) || !double.IsFinite(s.BaseEffectiveTrigger)
        || !IsFiniteLevel(habituationLevel))
        return new(ProfileMove.Hold, current, QualifiesUp: false);

    // 3. DOWN — FAST, 10d MAX, no debounce. Asymmetry is the safety property.
    //    WorstRecentFmsOn = 10d MAX (MeasuredOnFms); MeasuredDiscomfortInWindow = 28d count-window flag;
    //    BehaviorWorsening = lagging 28d half-split (corroboration only, never the primary edge).
    bool downSignal = s.WorstRecentFmsOn >= pcfg.DownFmsThreshold
                   || s.MeasuredDiscomfortInWindow || s.BehaviorWorsening;
    if (downSignal)
    {
        if ((int)current == 0) return new(ProfileMove.Hold, current, false);  // Reactive floor
        return new(ProfileMove.Down, (HabituationProfile)((int)current - 1), QualifiesUp: false);
    }

    // 4. UP — SLOW. The bar is the gate's EXACT relax-permit bar (RT Angle-1 HIGH #1 + LOW #7):
    //    call AdaptationGate.Evaluate with a synthetic Relax hint and require Apply(Relax). This carries
    //    MeasuredOnReportCount mins, cold-start ramp, FmsCoverageOk, the <=/>= boundaries, AND the
    //    daily/weekly caps — and can never drift looser than the gate.
    var relaxProbe = AdaptationGate.Evaluate(
        new AdaptationHint(AdaptDirection.Relax, string.Empty), s, cfg);
    bool relaxBarMet = relaxProbe is GateDecision.Apply ap && ap.Direction == AdaptDirection.Relax;

    // At/near cap AND offset settled at/below cap (RT Angle-3 MED: forbid UP while a post-DOWN bleed
    // leaves _offset > cap, which would read level>100 and re-raise the cap just lowered).
    bool atOrNearCap = habituationLevel >= pcfg.UpAtCapLevelThreshold
                    && habituationLevel <= 100 && offsetOverCapGuard <= 0.0;

    bool qualifiesUp = relaxBarMet && atOrNearCap;

    if (qualifiesUp && consecutiveUpQualify + 1 >= pcfg.UpRequiredConsecutiveEvals)
    {
        if ((int)current == 2) return new(ProfileMove.Hold, current, QualifiesUp: true);  // Resilient ceiling
        return new(ProfileMove.Up, (HabituationProfile)((int)current + 1), QualifiesUp: true);
    }

    // qualified-but-streak<threshold, OR not qualified — caller uses QualifiesUp to drive the durable streak.
    return new(ProfileMove.Hold, current, qualifiesUp);
}
```

Notes:
- `relaxProbe` uses the SAME `cfg` (`AdaptationGateConfig.Default`) the trigger axis uses, so the UP bar IS the relax bar — including the `AutoChangesThisWeek` / `DaysSinceLastAutoChange` rate limits already inside `RelaxReject` (AdaptationGate.cs:131-136). This subsumes RT Angle-3 HIGH #1's cross-axis cooldown *for UP-qualify* (a relax-direction trigger move logged this week makes `RelaxReject` return `weekly-change-cap`/`relax-min-interval`, so UP can't also qualify). The reciprocal (a profile UP making `AutoChangesThisWeek` block a later trigger relax) is enforced by widening `DeriveAutoChannelTiming` to count the UP audit row (Build Step 6) + the in-lock budget block in §3 (6).
- `offsetOverCapGuard` = `max(0, _offset - cap)` passed from the snapshot; `> 0` forbids UP.
- `IsFiniteLevel` guards the int level (always finite unless cap≤0 made the producer NaN/clamp; see Build Step 6 clamp).

**RT Angle-1 HIGH #2 (UP removes the Sensitive floor → unlocks a later trigger relax):** an UP from Reactive (`Cohort.Sensitive`) to Adapting (`Cohort.Typical`) removes the no-net-relax floor. The symmetric guard: since the UP audit row consumes the shared weekly relax budget (Build Step 6), and the next pass's `RelaxReject` sees `AutoChangesThisWeek >= MaxAutoChangesPerWeek` for a full week, a floor-freed relax cannot apply within the budget window. **Additionally**, if the standing delta was positive and the cohort was Sensitive, that delta could only be ≤0 under the floor — so an UP out of Sensitive cannot retroactively "free" a *positive* standing delta (there was none). The remaining hole — a *zero* standing delta becoming relaxable a week later under the new looser cohort — is the *intended* therapeutic end-state (the user genuinely demonstrated tolerance for a week at the higher tier) and is bounded to one relax per budget window. **Not separately fixed; justified as bounded + intended (see delta table).**

---

## 5 · `MaybeRunAdaptationTuning` rewire (`MainViewModel.cs:2407-2467`)

- **KEEP** the consent early-return (L2411) VERBATIM.
- **KEEP** evidence/snapshot/cohort/baseTrigger/state/hint assembly (L2420-2453) unchanged except the snapshot now also carries `HabituationLevel` + the over-cap guard (Build Step 6).
- **REPLACE** the `_adaptationCoordinator.RunOnce(...)` block (L2455-2464):

```
// Both decisions OFF-lock against the SAME snapshot. Consent already gated at L2411.
var triggerDecision = AdaptationGate.Evaluate(hint, state, cfg);

var hist = _profileEvalHistory.Load();                  // Build Step 7 (durable streak)
double overCap = Math.Max(0.0, snap.OffsetPp - snap.CapPp);  // from the extended snapshot
var verdict = ProfileElasticityClassifier.Classify(
    state, hint, snap.Profile, snap.Vestibular, snap.HabituationLevel, overCap,
    hist.ConsecutiveUpQualify, ProfileElasticityConfig.Default, cfg);

// Durable streak update BEFORE the commit, with explicit per-eval SPACING (RT Angle-3 HIGH #3):
// only a qualify that is >= RelaxMinIntervalDays after the last counted qualify advances the streak;
// a repeat within the window is the SAME eval. Reset to 0 on any non-qualify or a fired UP.
if (verdict.QualifiesUp
    && (now - hist.LastQualifyUtc).TotalDays >= cfg.RelaxMinIntervalDays)
{
    hist.ConsecutiveUpQualify += 1;
    hist.LastQualifyUtc = now;
}
else if (!verdict.QualifiesUp)
{
    hist.ConsecutiveUpQualify = 0;
}
if (verdict.Move == ProfileMove.Up) hist.ConsecutiveUpQualify = 0;  // consumed
_profileEvalHistory.Save(hist);

var result = mock.ApplyGateOutcome(new AdaptationCommitRequest(
    snap.Profile, snap.AutoDeltaPp, snap.Vestibular, snap.HabituationLevel, snap.StrikeCount,
    verdict.Move, verdict.Target, triggerDecision));
```

- Drop the `applyAutoDelta`/`onApplied` callbacks — the engine commit owns the apply (no double-apply). `AdaptationCoordinator.RunOnce` stays for pure-trigger tests/callers but is no longer the apply path.
- The `catch { }` (L2466) stays.
- The streak `Save` must also run on the fail-closed path — fold the streak update into a `finally`-style position so a NaN-Hold still resets the streak to 0 (RT Angle-3 HIGH #3). Practically: compute `verdict` (which returns `QualifiesUp=false` on fail-closed) and the update block above before the commit; since both are inside the same `try`, a throw before `Save` is acceptable only if the next pass re-evaluates from a stale streak — guard by performing `Save` immediately after the streak math, before `ApplyGateOutcome`.

---

## 6 · Snapshot / `MeasuredState` / `DeriveAutoChannelTiming` extensions

**`SnapshotForAdaptation` (MockInferenceEngine.cs:1768):** extend the tuple, read under the SAME `_gate` lock:
```
public (double EffectiveTrigger, double AutoDeltaPp, double PersonalPressureScale, bool Vestibular,
        HabituationProfile Profile, int HabituationLevel, double OffsetPp, double CapPp, double StrikeCount)
        SnapshotForAdaptation()
```
returning `_habituation.HabituationLevel`, `(double)_habituation.HabituationOffset`, `_habituation.CurrentParams.HabOffsetCapPp`, `_habituation.SevereStrikeCount`. Update the sole consumer destructure (`MainViewModel.cs:2439-2442`) in the same change — the cohort map (L2441) + baseTrigger (L2442) reads stay byte-identical.

**`MeasuredState` (AdaptationGate.cs:226-266):** no new field needed for the gate — the classifier receives `habituationLevel`/`overCap` as explicit `Classify` params (keeps `MeasuredState` unchanged → B5 gate tests + every positional ctor stay valid). *(Simpler than the draft's trailing-field add; the gate never reads level.)*

**`DeriveAutoChannelTiming` (AdaptationEvidence.cs:216-255):** add `string profileParameter`. Change ONLY the TIMING budget filter (L231-235) to count BOTH params for `daysSinceLast`/`thisWeek`:
```
var countedForBudget = live
    .Where(r => r.Parameter == autoParameter || r.Parameter == profileParameter)
    .OrderBy(r => r.TimestampUtc).ToList();
double daysSinceLast = countedForBudget.Count > 0
    ? Math.Max(0.0, (nowUtc - countedForBudget[^1].TimestampUtc).TotalDays) : double.PositiveInfinity;
int thisWeek = countedForBudget.Count(r => (nowUtc - r.TimestampUtc).TotalDays <= 7.0);
```
KEEP `netRelaxToday` (L238), the cumulative/`everNetRelaxed` loop (L243-245), and `manualTightens` (L247-252) on the TRIGGER param (`auto`) ALONE — profile rows carry `Delta=0` and no trigger sign, so they must not skew net-relax / `HasEverAutoRelaxed`. Reset-tare (L222-229) unchanged (a user Reset auto-clears the profile-budget rows too).

> **Profile budget rows are written ONLY for UP moves** (§3 step 5), so DOWN/tighten never consume the relax budget — `thisWeek` counts UP profile rows + all trigger rows. (RT Angle-1 MED #6 / RT Angle-3 LOW: protective moves are budget-exempt, mirroring AdaptationGate's independent-tighten bypass at L65-66.) A relax-direction trigger row IS counted (today's behavior, unchanged). This means `thisWeek` slightly over-counts a trigger *unwind* (a positive diff that is not net-relax) — acceptable; it only makes the relax budget marginally stricter, never looser.

**Caller (`MainViewModel.cs:2435`):**
```
var timing = AdaptationEvidence.DeriveAutoChannelTiming(records, now, AutoAdaptParameter, AutoReclassifyProfileParameter);
```
Expose `AutoReclassifyProfileParameter` as a shared const (App + engine read the same string).

---

## 7 · Durable consecutive-UP-eval store (`profile_eval_history.json`)

**Chosen: option (a)** JSON, NOT the adjustment-log reconstruction (option b). RT Angle-3 HIGH #3 proved option (b) cannot enforce the per-eval spacing (a qualify-but-Hold writes no auto-change row, so `DaysSinceLastAutoChange` never advances and multiple same-afternoon passes satisfy "2 consecutive evals" in minutes) and is crash-inflatable. Option (a) carries its own `LastQualifyUtc`:

`%LOCALAPPDATA%\SenseEase\profile_eval_history.json`:
```
record ProfileEvalHistory(int ConsecutiveUpQualify, DateTime LastQualifyUtc);  // default (0, DateTime.MinValue)
```
- `Load()` returns the default on missing/corrupt file (fail-closed → streak 0).
- `Save()` is an atomic write (temp + rename), like the other `%LOCALAPPDATA%\SenseEase` stores.
- The streak is RECONSTRUCTED from disk each pass, NEVER held in memory (F4 invariant).
- Spacing is enforced in the App (Build Step 5): increment only when `now - LastQualifyUtc >= RelaxMinIntervalDays`, so 2 qualifies span ~14d.
- **MUST be explicitly cleared in `ResetPersonalization`** (Build Step 8) — option (a) does not get reset-tare for free.
- Written ONLY on a consent-ON pass (downstream of L2411) → no file when consent OFF.

---

## 8 · `ResetPersonalization` auto-source revert (`MockInferenceEngine.cs:713-751`)

Insert the revert **BEFORE `SeedShaderFromProfile(_habituation.CurrentProfile)` at L721** (RT Angle-1 MED #5 / RT Angle-2 MED #4: seeding from the pre-revert permissive profile leaves the opening overlay one tier too relaxed). Inside the existing `lock(_gate)`:

```
// B6 Stage 4b: an auto-moved profile must return toward the fresh-user seed on Reset, BEFORE the shader
// is seeded (so the shader reflects the reverted profile) and BEFORE the auto-delta is zeroed (so the
// cohort is already at-seed when the delta is removed — no permissive-cohort + nonzero-delta transient).
if (_habituation.ProfileSource == "auto_elasticity")
    _habituation.AutoReclassifyProfile(HabituationProfile.Adapting, "auto_profiling_default");
_profileEvalHistory.Clear();   // option-(a) streak store — clear unconditionally on Reset

SeedShaderFromProfile(_habituation.CurrentProfile);   // existing L721 — now seeds from the reverted profile
… (existing L722-740 unchanged: ResetTransientState, auto-delta zero, persist) …
```

Explicit-user (`user_dashboard`) / calibration sources are untouched (guard is `== "auto_elasticity"` only). Re-entrancy is safe (C# locks recurse same-thread). `AutoReclassifyProfile` here fires `ProfileChanged` + sets dirty; the trailing `MaybePersistProfileSnapshot` (L739) / `PersistHabituation(force:true)` (L741) cover it. **The `Habituation.Profile` event for the `auto_elasticity → auto_profiling_default` revert is intentional and correct** (a Reset is a deliberate user action; auditing the revert is desired) — distinct from the F6 background-suppression decision below.

---

## 9 · Persistence / consent / F6 lifecycle-event decision

**`profile_source="auto_elasticity"` already round-trips** via `HabituationProfileSnapshot.ProfileSource` + `BuildProfileSnapshotCore` + the boot restore (Stage 4a aliases the profile string). No schema change.

**Boot-load source whitelist (`MockInferenceEngine.cs:273-275`)** — wrap the `SetProfile` call inside the successful alias-parse branch:
```
var src = KnownProfileSources.Contains(savedSnapshot.ProfileSource) ? savedSnapshot.ProfileSource : "default";
_habituation.SetProfile(prof, src);
```
`KnownProfileSources` (verified exhaustive by grep this session — see test `BOOT_WHITELIST_KNOWN`): `{ "default", "calibration_demo", "chatbot_override", "auto_profiling_default", "user_dashboard", "chatbot_adjust_sensitivity", "chatbot_adjust_strike_limit", "auto_elasticity" }`. Unknown → `"default"` LABEL only (`prof` already parsed, so params stay byte-identical). **RT Angle-2 LOW caveat (forward-compat):** the relabel must affect ONLY the in-memory label used for the Reset `== "auto_elasticity"` guard and explicit-vs-auto precedence — it must NOT rewrite a legitimately-newer on-disk source. Implement via `MergeCarryForward` treating an unknown source as carry-forward (preserve the original on-disk string), not engine-authoritative. **Re-verify the set mechanically (grep every live `SetProfile`/`SetHabituationProfile` source string) before shipping** — a missed legitimate source silently relabels an explicit profile to `default` on restart, corrupting the Reset guard + precedence.

**F6 lifecycle-event suppression (RT Angle-2 HIGH #2 — ACCEPTED, sign-off decision):** `MaybePersistProfileSnapshot` → `WriteHabituationLifecycleEvents` (MockInferenceEngine.cs:1865) unconditionally emits `AppendHabituationEvent("Habituation.Profile", …)` on any profile/source change (L1922-1926). This durable event is read by the chatbot trajectory builder and shown in the EventLog UI — breaking F6's background-silent premise (the design only worried about the shader re-seed). **Fix: gate the `Habituation.Profile` emission on `source != "auto_elasticity"`** at L1922:
```
if ((profile != _lastSavedProfile || source != _lastSavedProfileSource)
    && source != "auto_elasticity")              // F6: AUTO elasticity moves are background-silent
{
    AppendHabituationEvent("Habituation.Profile", …);
}
```
The snapshot is still persisted (the durable JSON carries the source — that is the F6 "durable `Source="system"` audit" the design intends); only the user-visible/chatbot-visible *event-log* entry is suppressed for auto moves. The Reset revert (Build Step 8) writes `auto_profiling_default`, which is NOT `auto_elasticity`, so the revert event still emits (correct). **Confirm the `profile_eval_history.json` writes never touch the event log** (they don't — separate store). *This is a design-invariant change to F6's rationale: auto moves are durably audited in the snapshot but NOT announced in the event stream — surface to Kirby at sign-off.*

---

## 10 · Do-not-break invariants

1. **Vestibular NEVER auto-moves.** Classifier hard-holds on `vestibular` at entry (QualifiesUp=false → no streak banking, RT Angle-1 HIGH #3); `ApplyGateOutcome` re-reads live `_vestibularProtection` and skips ANY profile move + zeros a positive trigger relax; the snapshot-non-vestibular-but-live-vestibular case is caught by the mismatch abort. `EffectiveTrigger=50` + `SevereStrikeLimitOverride=1` preserved.
2. **Consent OFF ⇒ engine byte-identical.** Every 4b path is downstream of `MainViewModel.cs:2411`; no engine-side consent read; no `profile_eval_history.json` written; no profile/eval log rows.
3. **Fail-closed.** NaN `WorstRecentFmsOn`/`BaseEffectiveTrigger`/non-finite level ⇒ classifier Hold; snapshot-vs-live mismatch ⇒ Hold; UP stale-protective-change ⇒ Hold.
4. **Strikes tare-not-zero across an auto-move,** re-checked vs the NEW limit (`SevereStrikeLimitOverride ?? _params.SevereStrikeLimit`, `StrikeLimitEpsilon`). Explicit/calibration `SetProfile` still zeros unconditionally.
5. **Auto down-move never instantly truncates `_offset`;** the new unconditional Step() bleed (top-of-Step, `StepMagnitudePp`/tick) converges to cap. Explicit/calibration still hard-clamps.
6. **ONE shared weekly relax budget across profile + trigger** (UP profile rows + relax trigger rows counted; DOWN/tighten budget-exempt). Never both NET-relax in one pass. In-lock live budget re-derive blocks an over-budget relax.
7. **DOWN-into-Sensitive retracts a standing positive delta** (NET-protective). DOWN-move-no-auto-unlock holds (the §5.4 held +delta only moves on a future independently-clearing pass).
8. **Shader untouched on auto moves (F6);** explicit `SetHabituationProfile` (L1472-1473) keeps its A7 re-seed.
9. **Single critical section:** profile flip + trigger delta under ONE `_gate`; pure gate + classifier OFF-lock; never call the self-locking/self-persisting public `ApplyAutoAdaptDelta`/`SetHabituationProfile` from inside; `RunOnce` no longer applies the delta.
10. **`ResetPersonalization` returns an auto-moved profile toward protection** (revert to `Adapting`/`auto_profiling_default` BEFORE shader-seed AND BEFORE delta-zero); explicit/calibration untouched; streak cleared.
11. **Orthogonality + no-regress:** `AutoReclassifyProfile` does not touch `EffectiveTrigger`/`_awaitingUserConfirm`/`_pauseCooldown`/`_externallyPaused`/`_stableElapsed`/`_stepAccumulator`/`_phase`/`_lastSpikeAtUtc`/`_pressureWasAtRecurrence`; Stages 1-3, the `_offset`/strike/state loops, and the JSON schemas are unchanged (only the `profile_source` value space + the new audit Parameter string + one new tiny JSON store are added).
12. **One step per pass, adjacency only:** UP=`(int)+1` capped at Resilient(2); DOWN=`(int)-1` floored at Reactive(0); never skip a tier.
13. **Durable streak only;** reconstructed from disk each pass with `LastQualifyUtc` spacing; reset on disqualify/DOWN/fired-UP; cleared by Reset.

---

## 11 · Test matrix

| # | Test | Asserts |
|---|---|---|
| VESTIBULAR_NEVER_MOVES | vestibular snapshot that would otherwise yield Up/Down | zero profile change, zero `AutoReclassifyProfile` call, classifier Hold + QualifiesUp=false |
| VESTIBULAR_LIVE_FLIP_ABORT | off-lock non-vestibular, live vestibular before commit | mismatch abort, Reason=snapshot-vs-live-mismatch |
| VESTIBULAR_NO_STREAK_BANK | vestibular-ON passes where measured data would qualify | `ConsecutiveUpQualify` stays 0; toggling vestibular off does NOT fire UP next pass |
| CONSENT_OFF_BYTE_IDENTICAL | consent != Enabled | early-return; zero `AutoReclassifyProfile`, zero profile/eval rows, no `profile_eval_history.json`, engine identical to no-classifier baseline |
| FAILCLOSED_NAN | WorstRecentFmsOn / level / BaseEffectiveTrigger = NaN (each) | classifier Hold; streak reset to 0 |
| MISMATCH_ABORT_DELTA | live `_autoAdaptTriggerDeltaPp` differs from snap by >1e-9 | Hold, no profile/trigger move |
| UP_STALE_STRIKE_ABORT | UP verdict, then `RegisterSevereStrike` collapses `_offset`/bumps count before commit | UP aborts (level<snap OR strike>snap), Reason=up-stale-protective-change; DOWN in the same race does NOT abort |
| BASE_SHIFT_NO_ABORT | concurrent base-trigger move only (profile/delta/vestibular unchanged) | NO abort; inline diff recomposes against live delta → EffectiveTrigger lands at base+delta |
| STRIKE_TARE_PRESERVE | count=1.5 Adapting(limit2)→Resilient(limit2) | `_severeStrikeCount`==1.5, `_offset` not zeroed |
| STRIKE_RECHECK_ZERO | Resilient(count=1.5,limit2)→Reactive(limit1) | `_offset`==0 AND count==0 (1.5≥1−eps) |
| STRIKE_OVERRIDE_HONORED | `SevereStrikeLimitOverride=1`, count=1.0, any auto-reclassify | recheck uses override, zeros offset+count |
| DEFERRED_CLAMP_NO_TRUNCATE | `_offset`≈24 Adapting(cap25)→Reactive(cap15) | `_offset` >15 immediately after `AutoReclassifyProfile` (not truncated) |
| STEP_BLEED_CONVERGES_WORSENING | after the above, user keeps reporting discomfort (never calm) | Step() still bleeds `_offset` toward 15 (top-of-Step, calm-independent), converges to exactly 15 |
| OSCILLATION_NO_EROSION | classify in/out of Reactive repeatedly | `_offset` does not monotonically erode |
| EXPLICIT_SETPROFILE_UNCHANGED | `SetProfile("user_dashboard")` | strikes zeroed, `_offset` hard-clamped, wrapper re-seeds shader (byte-identical) |
| SHADER_UNTOUCHED_ON_AUTO | auto flip via `ApplyGateOutcome` | shader knobs unchanged (no `SeedShaderFromProfile`); explicit still re-seeds |
| LIFECYCLE_EVENT_SUPPRESSED | auto flip persists | NO `Habituation.Profile` event for source=auto_elasticity; explicit flip + Reset revert DO emit |
| DOWN_FAST_NO_DEBOUNCE | single fresh `WorstRecentFmsOn`(10d MAX)≥6.0 | fires Down with no debounce; `BehaviorWorsening` alone not required |
| UP_USES_GATE_RELAX_BAR | relax bar fails ONLY on `MeasuredOnReportCount < 5` (silence/avoidance), all else qualifying | UP does NOT qualify (probe returns Hold/insufficient-measured-reports) — RT Angle-1 HIGH #1 |
| UP_COLDSTART_GATED | first-ever relax, BehaviourDays<14 or reports<8, otherwise at-cap | UP does NOT qualify (cold-start) |
| UP_STRICT_FMS_BOUNDARY | `WorstRecentFmsOn`==6.0 exactly | UP relax bar NOT met (gate `>=` reject) |
| UP_DISCOMFORT_HRS_BOUNDARY | `HoursSinceLastDiscomfort`==48.0 exactly | UP NOT met (gate `<=` suppress) — RT Angle-1 LOW #7 |
| UP_SLOW_TWO_EVALS_SPACED | qualifying UP pass1 (Hold, streak0→1); pass2 < RelaxMinIntervalDays later | streak does NOT advance (same eval); pass2 ≥ interval later → streak→2 → Up |
| UP_SAME_DAY_NO_INFLATE | three qualifying passes in one afternoon | fires at most the streak the spacing allows (no minutes-apart UP) — RT Angle-3 HIGH #3 |
| UP_STREAK_RESET_ON_MISS | qualify once, then a non-qualify | streak→0; later single qualify does not fire |
| UP_REQUIRES_AT_CAP | relax bar met but level<90 | no UP qualify |
| UP_FORBIDDEN_OVER_CAP | post-DOWN, `_offset`>cap (level>100) | UP does NOT qualify (over-cap guard) — RT Angle-3 MED |
| ADJACENCY_ONE_STEP | Reactive UP→Adapting; Resilient UP→Hold; Reactive DOWN→Hold |
| NEVER_BOTH_NET_RELAX | UP + gate net-relax Apply same pass | trigger relax suppressed; `_autoAdaptTriggerDeltaPp` unchanged |
| UNWIND_STACKS_WITH_UP | UP + gate Relax that is an UNWIND of a prior tighten (positive diff, not net-positive) | unwind APPLIES alongside UP — RT Angle-2 MED #6 |
| TIGHTEN_MAY_STACK | DOWN + gate Tighten Apply | BOTH apply |
| DOWN_NO_AUTO_UNLOCK | Sensitive cohort, standing-blocked +delta, DOWN classify | +delta not applied this pass |
| DOWN_INTO_SENSITIVE_RETRACTS | Adapting/Typical, standing +6 delta, DOWN→Reactive | delta retracted to 0 in the same commit (NET-protective) — RT Angle-3 MED |
| SHARED_BUDGET_COUPLING | UP move bumps `AutoChangesThisWeek` to cap | next trigger relax held on weekly-cap; symmetrically a trigger relax row blocks UP qualify |
| BUDGET_LIVE_REDERIVE | off-lock budget=1, racing eval/budget row pushes live to cap before commit | relax-direction commit HELD by in-lock budget re-derive — RT Angle-2 MED #5 / RT Angle-3 HIGH #2 |
| DOWN_BUDGET_EXEMPT | budget spent, DOWN needed | DOWN profile move + tighten still apply (not blocked) — RT Angle-1 MED #6 |
| PROFILE_ROW_NO_NETRELAX_POLLUTION | UP audit row (Delta=0) | does not contribute to cumulative net-relax / HasEverAutoRelaxed |
| RESET_AUTO_REVERT_ORDER | auto-Resilient + standing +delta → Reset | profile→Adapting BEFORE shader-seed (shader=Adapting knobs) AND before delta-zero; delta==0; streak cleared |
| RESET_EXPLICIT_UNTOUCHED | source user_dashboard/calibration → Reset | profile UNCHANGED (only delta zeroed) |
| BOOT_WHITELIST_KNOWN | snapshot source=auto_elasticity | boots source preserved + correct params |
| BOOT_WHITELIST_UNKNOWN | hand-edited unknown source | in-memory label→default, params byte-identical; original on-disk string preserved via carry-forward (forward-compat) — RT Angle-2 LOW |
| BOOT_WHITELIST_LEGACY | legacy SENSITIVE/user_dashboard file | NOT relabeled |
| PERSIST_ROUNDTRIP | auto-reclassify→Resilient/auto_elasticity | reloads to Resilient + source auto_elasticity |
| SINGLE_PERSIST | commit moving both axes | persists exactly once (no double `MaybePersistProfileSnapshot`) |
| NOOP_LOCK_FREE | consent-ON, ProfileMove.Hold && trigger Hold | cheap early-out, no lock |

---

## 12 · What the red-team changed (delta table)

| # | RT item (angle/sev) | Verdict | Change folded in (this spec) |
|---|---|---|---|
| 1 | UP bar omits MeasuredOnReportCount/cold-start floor → silence reads as tolerance (A1 HIGH) | ACCEPT | Classifier calls `AdaptationGate.Evaluate` with a synthetic Relax hint and requires `Apply(Relax)` — shares the exact `RelaxReject` bar, never copies it (Build Step 4) |
| 2 | UP removes Sensitive cohort floor → unlocks a later trigger relax (A1 HIGH) | PARTIAL-ACCEPT | UP audit row consumes the shared weekly relax budget → `RelaxReject` blocks a floor-freed relax for the budget window; a positive standing delta couldn't exist under the floor. Residual (a zero delta becoming relaxable a week later under the genuinely-earned looser cohort) is intended + bounded to 1 relax/window — justified, not separately gated |
| 3 | Durable UP-streak banks credit during vestibular (A1 HIGH) | ACCEPT | `Classify` returns `QualifiesUp=false` on vestibular/fail-closed; App uses that exact value, never recomputes (Build Steps 4-5) |
| 4 | Live re-read too narrow — racing severe strike doesn't abort a stale UP (A2 HIGH) | ACCEPT | `ApplyGateOutcome` re-reads live level + strike count; UP-only abort on protective drift (Build Step 3, 2b) |
| 5 | Auto move emits user-visible `Habituation.Profile` event → breaks F6 silence + chatbot trajectory (A2 HIGH) | ACCEPT | Suppress the event for source=="auto_elasticity" at WriteHabituationLifecycleEvents L1922; snapshot still durably audits the source (Build Step 9) — **sign-off decision, surface to Kirby** |
| 6 | Step() bleed gated on calm/Relaxing branch strands over-cap offset during worsening (A2 HIGH) | ACCEPT | Bleed moved to UNCONDITIONAL top-of-Step (after pause guards), calm-independent (Build Step 2) |
| 7 | Budget read off-lock, profile row appended in-lock → cross-pass tear over cap (A2 MED #5 / A3 HIGH #2) | ACCEPT | In-lock live budget re-derive in `ApplyGateOutcome`; relax-direction move HELD if spent (Build Step 3 (3),(6)) |
| 8 | Within-week cross-pass compounding (profile UP day0 + trigger relax day8) (A3 HIGH #1) | ACCEPT (via #2 mechanism) | UP row in the shared budget makes `RelaxReject`'s `DaysSinceLastAutoChange`/`AutoChangesThisWeek` space the second relax ≥ RelaxMinIntervalDays — the gate's own rate limit is the cross-axis cooldown |
| 9 | Durable streak (option b) can't enforce 2-evals spacing, crash-inflatable, stale-strandable (A3 HIGH #3) | ACCEPT | Adopt option (a) JSON with `LastQualifyUtc`; increment only ≥ RelaxMinIntervalDays apart; Save before commit; reset on fail-closed (Build Steps 5,7) |
| 10 | Reset seeds shader from pre-revert permissive profile (A1 MED #5 / A2 MED #4) | ACCEPT | Revert BEFORE `SeedShaderFromProfile` at L721 (Build Step 8) |
| 11 | Protective moves (DOWN/tighten) counted against the relax cap → budget starvation (A1 MED #6 / A3 LOW) | ACCEPT | Only UP profile rows + relax trigger rows count toward the shared budget; DOWN/tighten budget-exempt (Build Steps 3,6) |
| 12 | never-both-relax over-suppresses a benign tighten-unwind (A2 MED #6) | ACCEPT | Guard fires only on a TRUE net-relax (`NewAutoDeltaPp > 0 && > current`), not any positive diff (Build Step 3 (6)) |
| 13 | DOWN into Sensitive leaves a standing positive delta the floor forbids (A3 MED) | ACCEPT | Retract a standing positive delta in the same commit on DOWN→Reactive (Build Step 3 (4b)) |
| 14 | At-cap UP gate trivially true while over-cap mid-bleed → re-raises just-lowered cap (A3 MED) | ACCEPT | `atOrNearCap` requires level≤100 AND offset settled at/below cap (Build Step 4) |
| 15 | UP discomfort-hrs boundary mismatch (`>` vs gate `<=`) (A1 LOW #7) | ACCEPT (subsumed by #1) | Sharing `Evaluate` carries the gate's exact `<=` boundary |
| 16 | Boot relabel rewrites a forward-compat newer source on disk (A2 LOW) | ACCEPT | Relabel affects in-memory label only; preserve original on-disk string via MergeCarryForward (Build Step 9) |
| 17 | F4 spacing knob unenforced/untunable; new defaults not in SenseDefaults (A3 LOW) | PARTIAL-ACCEPT | Spacing now enforced via `LastQualifyUtc` ≥ `RelaxMinIntervalDays` (reuses the existing wired knob). New `ProfileElasticityConfig.Default` follows the `AdaptationGateConfig.Default` record pattern (parity, not SenseDefaults) — a `SenseDefaults` promotion is left as an open fork per the AdaptationGateConfig precedent |

**Nothing rejected outright.** Items 2 and 17 are partial-accepts with the residual explicitly justified above.

---

## 13 · Open forks (owner confirm — non-blocking for build)

- **F6 event suppression is a design-invariant edit** (auto moves durably audited in snapshot but NOT in the event stream / chatbot trajectory). Surface to Kirby; the build can proceed with suppression on (the safer, F6-faithful default).
- `UpAtCapLevelThreshold = 90` knob value.
- `ProfileElasticityConfig.Default` vs SenseDefaults-overridable (follows AdaptationGateConfig precedent for now).
- `KnownProfileSources` exhaustiveness — mechanically re-grep before shipping.