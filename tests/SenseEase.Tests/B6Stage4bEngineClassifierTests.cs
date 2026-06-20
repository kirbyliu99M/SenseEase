using System;
using System.IO;
using SenseEase.Core.Adaptation;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Inference;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// B6 Stage 4b — the bidirectional elasticity ENGINE + CLASSIFIER + STREAK-STORE safety properties (the
/// pure, lock-free core). The single-locked-commit (ApplyGateOutcome) + the App rewire are covered in
/// B6Stage4bCommitTests. Every test pins one invariant from docs/B6_STAGE4B_IMPLEMENTATION.md §10.
/// </summary>
public sealed class B6Stage4bEngineClassifierTests
{
    private static void Calm(HabituationEngine e, int seconds, int tier = 1)
    {
        for (int i = 0; i < seconds; i++) e.Step(10.0, false, tier, 1.0);
    }

    // ════════════════════════ ENGINE: AutoReclassifyProfile + the Step cap-bleed ════════════════════════

    [Fact] // STRIKE_TARE_PRESERVE — a relax-direction auto-move keeps in-flight strike evidence.
    public void AutoReclassify_TaresNotZerosStrikes_AndKeepsOffset()
    {
        var e = new HabituationEngine();
        Calm(e, 420);                                   // earn offset
        e.SetProfile(HabituationProfile.Adapting, "calibration_demo");  // limit 2; resets strikes
        e.NotifyDiscomfort(severe: true);               // 0.5
        e.NotifyDiscomfort(severe: true);               // 1.0
        e.NotifyDiscomfort(severe: true);               // 1.5  (< limit 2 ⇒ offset halved, not zeroed)
        Assert.Equal(1.5, e.SevereStrikeCount);
        int offsetBefore = e.HabituationOffset;
        Assert.True(offsetBefore > 0);

        e.AutoReclassifyProfile(HabituationProfile.Resilient);   // limit 2 ⇒ 1.5 < 2 ⇒ no zero

        Assert.Equal(1.5, e.SevereStrikeCount);         // PRESERVED (not zeroed like SetProfile)
        Assert.Equal(offsetBefore, e.HabituationOffset); // offset untouched
    }

    [Fact] // STRIKE_RECHECK_ZERO — the tared count is re-checked vs the NEW (stricter) limit.
    public void AutoReclassify_AtOrOverNewLimit_ZerosOffsetAndCount()
    {
        var e = new HabituationEngine();
        Calm(e, 420);
        e.SetProfile(HabituationProfile.Resilient, "calibration_demo"); // limit 2
        e.NotifyDiscomfort(severe: true);  // 0.5
        e.NotifyDiscomfort(severe: true);  // 1.0
        e.NotifyDiscomfort(severe: true);  // 1.5
        Assert.Equal(1.5, e.SevereStrikeCount);

        e.AutoReclassifyProfile(HabituationProfile.Reactive);   // limit 1 ⇒ 1.5 >= 1 ⇒ zero

        Assert.Equal(0, e.HabituationOffset);
        Assert.Equal(0.0, e.SevereStrikeCount);
    }

    [Fact] // STRIKE_OVERRIDE_HONORED — the re-check uses SevereStrikeLimitOverride, not the profile default.
    public void AutoReclassify_RecheckHonorsStrikeLimitOverride()
    {
        var e = new HabituationEngine();
        e.SetProfile(HabituationProfile.Resilient, "calibration_demo"); // limit 2, NO override yet
        Calm(e, 420);
        e.NotifyDiscomfort(severe: true);  // 0.5
        e.NotifyDiscomfort(severe: true);  // 1.0
        e.NotifyDiscomfort(severe: true);  // 1.5  (< limit 2 ⇒ sits here, not zeroed)
        Assert.Equal(1.5, e.SevereStrikeCount);

        // Now apply a vestibular-style override of 1 and auto-reclassify into Adapting (whose DEFAULT limit
        // is 2). The recheck must use the OVERRIDE (1): 1.5 >= 1 ⇒ zero. If it wrongly used Adapting's
        // default (2), 1.5 < 2 ⇒ no zero and this test fails.
        e.SevereStrikeLimitOverride = 1;
        e.AutoReclassifyProfile(HabituationProfile.Adapting);

        Assert.Equal(0.0, e.SevereStrikeCount);
        Assert.Equal(0, e.HabituationOffset);
    }

    [Fact] // DEFERRED_CLAMP_NO_TRUNCATE — a down-move does NOT instantly truncate _offset to the lower cap.
    public void AutoReclassify_DownMove_DoesNotInstantlyTruncateOffset()
    {
        var e = new HabituationEngine();
        e.SetProfile(HabituationProfile.Adapting, "calibration_demo"); // cap 25
        Calm(e, 5000);                                                 // saturate toward cap 25
        Assert.True(e.HabituationOffset > 15);

        e.AutoReclassifyProfile(HabituationProfile.Reactive);          // cap 15 — NO hard clamp

        Assert.True(e.HabituationOffset > 15);   // still above the new cap immediately after the move
    }

    [Fact] // STEP_BLEED_CONVERGES_WORSENING — the over-cap offset bleeds to cap even when NOT calm.
    public void StepBleed_ConvergesOverCapOffset_Independent_OfCalm()
    {
        var e = new HabituationEngine();
        e.SetProfile(HabituationProfile.Adapting, "calibration_demo");
        Calm(e, 5000);
        e.AutoReclassifyProfile(HabituationProfile.Reactive);          // offset now > cap 15
        Assert.True(e.HabituationOffset > 15);

        // Drive MID-BAND pressure (40 < recurrence, > stable) — never calm, so the relax branch never runs;
        // only the unconditional top-of-Step cap-bleed acts. It must converge to exactly the cap.
        for (int i = 0; i < 40; i++) e.Step(40.0, false, vimsTier: 1, dtSeconds: 1.0);

        Assert.Equal(15, e.HabituationOffset);
    }

    [Fact] // EXPLICIT_SETPROFILE_UNCHANGED — the deliberate-switch path still HARD-CLAMPS to the lower cap
           // (contrast with AutoReclassify's deferred bleed). Strike-zero on switch is pinned by
           // HabituationStrikeAndStateTests; here we contrast the offset handling.
    public void SetProfile_Explicit_StillHardClampsOffset()
    {
        var e = new HabituationEngine();
        e.SetProfile(HabituationProfile.Adapting, "calibration_demo");
        Calm(e, 5000);                                  // offset ~ cap 25
        Assert.True(e.HabituationOffset > 15);

        e.SetProfile(HabituationProfile.Reactive, "user_dashboard"); // cap 15

        Assert.Equal(15, e.HabituationOffset);          // hard-clamped immediately (NOT deferred like AutoReclassify)
    }

    [Fact] // No-op parity: a same-profile/same-source auto-reclassify is inert (no strike churn).
    public void AutoReclassify_SameProfileSameSource_IsNoOp()
    {
        var e = new HabituationEngine();
        e.SetProfile(HabituationProfile.Adapting, "auto_elasticity");
        Calm(e, 420);
        e.NotifyDiscomfort(severe: true);
        double strikes = e.SevereStrikeCount;
        e.AutoReclassifyProfile(HabituationProfile.Adapting, "auto_elasticity");
        Assert.Equal(strikes, e.SevereStrikeCount);     // untouched (early return)
    }

    // ════════════════════════════════════ CLASSIFIER ════════════════════════════════════

    private static readonly ProfileElasticityConfig P = ProfileElasticityConfig.Default;
    private static readonly AdaptationGateConfig G = AdaptationGateConfig.Default;

    /// <summary>A MeasuredState that PASSES the gate's relax-permit bar (steady-state, tolerated, no discomfort).</summary>
    private static MeasuredState RelaxPassing(
        Cohort cohort = Cohort.Typical, double worstFms = 3.0, int reports = 6,
        bool worsening = false, bool discomfortInWindow = false)
        => new(
            Cohort: cohort,
            MeasuredOnReportCount: reports,
            WorstRecentFmsOn: worstFms,
            FmsCoverageOk: true,
            TrendStableOrImproving: true,
            ExposureAtOrAboveBaseline: true,
            ExposureDeclining: false,
            BehaviorWorsening: worsening,
            BehaviourDays: 30,
            BaseEffectiveTrigger: 85.0,
            CurrentAutoDeltaPp: 0.0,
            HoursSinceLastDiscomfort: 100.0,
            DaysSinceLastAutoChange: 14.0,
            AutoChangesThisWeek: 0,
            NetRelaxTodayPp: 0.0,
            HasEverAutoRelaxed: true,
            MeasuredDiscomfortInWindow: discomfortInWindow);

    private static ProfileVerdict Classify(MeasuredState s, HabituationProfile current, bool vest = false,
        int level = 95, double overCap = 0.0, int streak = 1, bool spaced = true)
        => ProfileElasticityClassifier.Classify(s, current, vest, level, overCap, streak, spaced, P, G);

    [Fact] // VESTIBULAR_NEVER_MOVES + VESTIBULAR_NO_STREAK_BANK
    public void Classifier_Vestibular_HoldsAndBanksNothing_EvenWhenQualifying()
    {
        var v = Classify(RelaxPassing(), HabituationProfile.Adapting, vest: true);
        Assert.Equal(ProfileMove.Hold, v.Move);
        Assert.False(v.QualifiesUp);   // never banks a streak while vestibular
    }

    [Theory] // FAILCLOSED_NAN — non-finite inputs Hold + bank nothing.
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Classifier_NonFiniteWorstFms_FailsClosed(double bad)
    {
        var v = Classify(RelaxPassing(worstFms: bad), HabituationProfile.Adapting);
        Assert.Equal(ProfileMove.Hold, v.Move);
        Assert.False(v.QualifiesUp);
    }

    [Fact] // DOWN_FAST_NO_DEBOUNCE — one fresh ≥threshold worst-case FMS tightens a tier immediately.
    public void Classifier_FreshHighWorstFms_FiresDown_NoDebounce()
    {
        var v = Classify(RelaxPassing(worstFms: 8.0), HabituationProfile.Adapting);
        Assert.Equal(ProfileMove.Down, v.Move);
        Assert.Equal(HabituationProfile.Reactive, v.Target);   // one tier more cautious
    }

    [Fact] // DOWN also fires on a discomfort-in-window flag or behaviour-worsening (corroborators).
    public void Classifier_DiscomfortInWindow_OrWorsening_FiresDown()
    {
        Assert.Equal(ProfileMove.Down, Classify(RelaxPassing(discomfortInWindow: true), HabituationProfile.Resilient).Move);
        Assert.Equal(ProfileMove.Down, Classify(RelaxPassing(worsening: true), HabituationProfile.Resilient).Move);
    }

    [Fact] // Reactive floor: DOWN can't go below the most-cautious tier.
    public void Classifier_Down_AtReactiveFloor_Holds()
        => Assert.Equal(ProfileMove.Hold, Classify(RelaxPassing(worstFms: 8.0), HabituationProfile.Reactive).Move);

    [Fact] // UP_USES_GATE_RELAX_BAR — silence (too few measured reports) blocks UP via the shared gate bar.
    public void Classifier_Up_RequiresTheGateRelaxBar_SilenceBlocks()
    {
        // Steady-state needs >= MinFmsForRelaxSteadyState (5); 2 reports fails the gate ⇒ no UP qualify.
        var v = Classify(RelaxPassing(reports: 2), HabituationProfile.Adapting);
        Assert.Equal(ProfileMove.Hold, v.Move);
        Assert.False(v.QualifiesUp);
    }

    [Fact] // UP_STRICT_FMS_BOUNDARY — worst-case exactly at the threshold is NOT relax-permitted (gate >=).
    public void Classifier_Up_WorstFmsAtThreshold_DoesNotQualify()
    {
        var v = Classify(RelaxPassing(worstFms: G.RelaxFmsLowThreshold), HabituationProfile.Adapting);
        // worstFms == 6 also trips DOWN (>= DownFmsThreshold 6) — the protective direction wins, as designed.
        Assert.NotEqual(ProfileMove.Up, v.Move);
    }

    [Fact] // UP_REQUIRES_AT_CAP — relax bar met but level below the at-cap threshold ⇒ no qualify.
    public void Classifier_Up_BelowCapLevel_DoesNotQualify()
    {
        var v = Classify(RelaxPassing(), HabituationProfile.Adapting, level: 50);
        Assert.False(v.QualifiesUp);
        Assert.Equal(ProfileMove.Hold, v.Move);
    }

    [Fact] // UP_FORBIDDEN_OVER_CAP — a post-DOWN over-cap offset (level>100 / overCap>0) forbids UP.
    public void Classifier_Up_OverCap_DoesNotQualify()
    {
        var v = Classify(RelaxPassing(), HabituationProfile.Adapting, level: 110, overCap: 10.0);
        Assert.False(v.QualifiesUp);
    }

    [Fact] // UP fires when at-cap + relax-bar + spaced streak reaches the threshold.
    public void Classifier_Up_AtCap_SpacedStreakReachesThreshold_FiresUp()
    {
        var v = Classify(RelaxPassing(), HabituationProfile.Adapting, level: 95, streak: 1, spaced: true);
        Assert.Equal(ProfileMove.Up, v.Move);
        Assert.Equal(HabituationProfile.Resilient, v.Target);
        Assert.True(v.QualifiesUp);
    }

    [Fact] // UP_SAME_DAY_NO_INFLATE — an unspaced qualify does NOT fire UP even at streak threshold-1.
    public void Classifier_Up_UnspacedQualify_DoesNotFire_ButStillQualifies()
    {
        var v = Classify(RelaxPassing(), HabituationProfile.Adapting, level: 95, streak: 1, spaced: false);
        Assert.Equal(ProfileMove.Hold, v.Move);   // same eval — no premature fire
        Assert.True(v.QualifiesUp);               // but it DID meet the bar (App keeps the streak, advances LastQualify only when spaced)
    }

    [Fact] // ADJACENCY_ONE_STEP — Resilient UP holds (ceiling) and banks nothing.
    public void Classifier_Up_AtResilientCeiling_Holds_NoBank()
    {
        var v = Classify(RelaxPassing(), HabituationProfile.Resilient, level: 95, streak: 1, spaced: true);
        Assert.Equal(ProfileMove.Hold, v.Move);
        Assert.False(v.QualifiesUp);   // nothing to move toward ⇒ don't grow the streak
    }

    // ════════════════════════════════════ STREAK STORE ════════════════════════════════════

    [Fact]
    public void EvalHistoryStore_RoundTrips_AndClears()
    {
        string path = Path.Combine(Path.GetTempPath(), $"se_eval_{Guid.NewGuid():N}.json");
        try
        {
            IProfileEvalHistoryStore store = new FileProfileEvalHistoryStore(path);
            Assert.Equal(0, store.Load().ConsecutiveUpQualify);   // missing ⇒ Empty

            var when = new DateTime(2026, 6, 21, 8, 0, 0, DateTimeKind.Utc);
            store.Save(new ProfileEvalHistory(2, when));
            var loaded = store.Load();
            Assert.Equal(2, loaded.ConsecutiveUpQualify);
            Assert.Equal(when, loaded.LastQualifyUtc);

            store.Clear();
            Assert.Equal(0, store.Load().ConsecutiveUpQualify);   // cleared ⇒ Empty
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void EvalHistoryStore_CorruptFile_FailsClosedToEmpty()
    {
        string path = Path.Combine(Path.GetTempPath(), $"se_eval_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ this is not valid json ");
            IProfileEvalHistoryStore store = new FileProfileEvalHistoryStore(path);
            Assert.Equal(0, store.Load().ConsecutiveUpQualify);   // fail-closed
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
