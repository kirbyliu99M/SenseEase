using System;
using SenseEase.Core.Inference;

namespace SenseEase.Core.Adaptation;

/// <summary>Which way the bidirectional elasticity classifier wants to move the habituation profile.</summary>
public enum ProfileMove { Hold, Up, Down }

/// <summary>The classifier's verdict. <paramref name="QualifiesUp"/> is the durable-streak signal: it is
/// true whenever this eval MET the UP bar (gate relax-permit + at-cap), even when the move itself is Hold
/// because the streak has not yet reached the required consecutive count. The App advances/resets the
/// persisted streak from this flag (never from an in-memory streak).</summary>
public sealed record ProfileVerdict(ProfileMove Move, HabituationProfile Target, bool QualifiesUp);

/// <summary>B6 Stage 4b policy knobs. Mirrors the <see cref="AdaptationGateConfig"/> record pattern
/// (pure data, a <see cref="Default"/> the wiring layer may later promote to SenseDefaults).</summary>
/// <param name="UpAtCapLevelThreshold">HabituationLevel (0-100) the user must reach — i.e. they have
/// consumed ~all of their current elasticity and stayed tolerant — before an UP-move qualifies.</param>
/// <param name="UpRequiredConsecutiveEvals">Consecutive qualifying evals (spaced ≥ RelaxMinIntervalDays
/// apart, enforced by the App's durable store) before an UP-move fires. UP is the SLOW direction.</param>
/// <param name="DownFmsThreshold">A fresh worst-case ON-state measured FMS at/above this fires a DOWN-move
/// immediately, no debounce. DOWN is the FAST/protective direction (asymmetry is the safety property).</param>
public sealed record ProfileElasticityConfig(
    int UpAtCapLevelThreshold, int UpRequiredConsecutiveEvals, double DownFmsThreshold)
{
    public static ProfileElasticityConfig Default => new(
        UpAtCapLevelThreshold: 90,        // top ~10% of earned offset (open fork — owner confirm)
        UpRequiredConsecutiveEvals: 2,    // F4
        DownFmsThreshold: 6.0);           // == AdaptationGateConfig.RelaxFmsLowThreshold (symmetric boundary, F5)
}

/// <summary>The request the App hands to <c>MockInferenceEngine.ApplyGateOutcome</c> — the off-lock
/// snapshot the verdicts were computed from (re-checked live inside the lock), plus both axis decisions.</summary>
public sealed record AdaptationCommitRequest(
    HabituationProfile SnapProfile, double SnapAutoDelta, bool SnapVestibular,
    int SnapHabituationLevel, double SnapStrikeCount,
    ProfileMove ProfileMove, HabituationProfile ProfileTarget, GateDecision TriggerDecision);

/// <summary>The outcome of the single locked commit.</summary>
public sealed record AdaptationCommitResult(bool ProfileMoved, bool TriggerMoved, string Reason)
{
    public static AdaptationCommitResult Hold(string r) => new(false, false, r);
}

/// <summary>
/// B6 Stage 4b — the BIDIRECTIONAL ELASTICITY classifier ("the loop proposes a profile direction;
/// the engine's locked commit disposes"). Pure, no I/O, no engine reference — mirrors
/// <see cref="AdaptationGate"/>. It moves the habituation PROFILE (the FOV-offset elasticity cap) one
/// adjacency per pass: UP toward faster habituation on sustained DEMONSTRATED TOLERANCE, DOWN toward
/// caution on sustained WORSENING.
///
/// <para>The two safety spines: (1) <b>asymmetry</b> — DOWN fires FAST (a single fresh ≥threshold ON-FMS,
/// no debounce); UP is SLOW (it must clear the gate's EXACT relax-permit bar AND sit at/near cap AND
/// repeat across consecutive spaced evals). (2) <b>the UP bar IS the relax bar</b> — UP calls
/// <see cref="AdaptationGate.Evaluate"/> with a synthetic Relax hint and requires <c>Apply(Relax)</c>, so
/// it can never drift looser than the trigger axis (it inherits the cold-start ramp, coverage, worst-case
/// MAX, tolerated-exposure, and the daily/weekly caps for free). Vestibular hard-holds (and banks no
/// streak); non-finite inputs fail closed to Hold.</para>
/// </summary>
public static class ProfileElasticityClassifier
{
    /// <param name="s">The measured state (same record the trigger gate consumes).</param>
    /// <param name="current">The live profile.</param>
    /// <param name="vestibular">Vestibular hard-override — never auto-moves, never banks streak credit.</param>
    /// <param name="habituationLevel">Earned offset as a fraction of the current cap (0-100).</param>
    /// <param name="offsetOverCapGuard">max(0, offset − cap) from the snapshot; &gt; 0 (a post-DOWN bleed in
    /// progress) forbids UP, so a transient level&gt;100 can't re-raise the just-lowered cap.</param>
    /// <param name="consecutiveUpQualify">The durable spaced-qualify streak (from the App's JSON store).</param>
    /// <param name="upQualifySpacingMet">True iff this pass is ≥ RelaxMinIntervalDays after the last counted
    /// qualify (the App computes it from the store's LastQualifyUtc). A within-window repeat is the SAME eval,
    /// so it must NOT advance the streak NOR fire UP — gating the fire on this here keeps the classifier and
    /// the App's streak bookkeeping in lockstep (a streak of 1 + a same-day repeat must not reach the threshold).</param>
    public static ProfileVerdict Classify(
        MeasuredState s, HabituationProfile current, bool vestibular,
        int habituationLevel, double offsetOverCapGuard, int consecutiveUpQualify,
        bool upQualifySpacingMet, ProfileElasticityConfig pcfg, AdaptationGateConfig cfg)
    {
        if (s is null || pcfg is null || cfg is null)
            return new(ProfileMove.Hold, current, QualifiesUp: false);

        // 1. VESTIBULAR HARD HOLD — QualifiesUp=false so the durable streak NEVER banks credit while
        //    vestibular (so toggling vestibular off later can't instantly fire a banked UP).
        if (vestibular) return new(ProfileMove.Hold, current, QualifiesUp: false);

        // 2. FAIL-CLOSED (mirror AdaptationGate's non-finite guard). NaN comparisons fall open toward relax,
        //    the worst mode for an under-protection-harm system → Hold, bank nothing.
        if (!double.IsFinite(s.WorstRecentFmsOn) || !double.IsFinite(s.BaseEffectiveTrigger)
            || habituationLevel < 0)
            return new(ProfileMove.Hold, current, QualifiesUp: false);

        // 3. DOWN — FAST, no debounce. WorstRecentFmsOn is the 10d-MAX (can't be mean-diluted);
        //    MeasuredDiscomfortInWindow / BehaviorWorsening corroborate. Asymmetry IS the safety property.
        bool downSignal = s.WorstRecentFmsOn >= pcfg.DownFmsThreshold
                       || s.MeasuredDiscomfortInWindow || s.BehaviorWorsening;
        if (downSignal)
        {
            if ((int)current == 0) return new(ProfileMove.Hold, current, QualifiesUp: false); // Reactive floor
            return new(ProfileMove.Down, (HabituationProfile)((int)current - 1), QualifiesUp: false);
        }

        // 4. UP — SLOW. The bar is the gate's EXACT relax-permit bar: a synthetic Relax probe that must
        //    return Apply(Relax). This carries the cold-start ramp, coverage, worst-case MAX, tolerated-
        //    exposure, AND the daily/weekly rate caps — and can never drift looser than the trigger axis.
        var relaxProbe = AdaptationGate.Evaluate(
            new AdaptationHint(AdaptDirection.Relax, string.Empty), s, cfg);
        bool relaxBarMet = relaxProbe is GateDecision.Apply ap && ap.Direction == AdaptDirection.Relax;

        // At/near cap AND offset settled at/below cap (no post-DOWN over-cap bleed in progress).
        bool atOrNearCap = habituationLevel >= pcfg.UpAtCapLevelThreshold
                        && habituationLevel <= 100
                        && offsetOverCapGuard <= 0.0;

        bool qualifiesUp = relaxBarMet && atOrNearCap;

        // At the Resilient ceiling there is nothing to move toward — don't bank a streak (QualifiesUp=false
        // so the App resets it to 0 rather than growing it unboundedly while a maxed-out user keeps qualifying).
        if (qualifiesUp && (int)current == 2)
            return new(ProfileMove.Hold, current, QualifiesUp: false);

        // Fire UP only when THIS qualify is properly spaced (a same-window repeat is the same eval) AND it
        // brings the streak to the required consecutive count.
        if (qualifiesUp && upQualifySpacingMet
            && consecutiveUpQualify + 1 >= pcfg.UpRequiredConsecutiveEvals)
            return new(ProfileMove.Up, (HabituationProfile)((int)current + 1), QualifiesUp: true);

        // Qualified-but-(streak-too-short OR unspaced), or not qualified — the App drives the streak from QualifiesUp.
        return new(ProfileMove.Hold, current, qualifiesUp);
    }
}
