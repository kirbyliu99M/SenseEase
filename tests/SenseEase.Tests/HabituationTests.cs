using System;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Configuration;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Unit and integration tests for the Phase-3 Habituation Engine.</summary>
public class HabituationTests
{
    // ── Helper ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drive <paramref name="e"/> with a calm pressure (10) and no mask for
    /// <paramref name="seconds"/> ticks of dt=1.0 each.
    /// </summary>
    private static void Calm(HabituationEngine e, int seconds)
    {
        for (int i = 0; i < seconds; i++)
            e.Step(10.0, false, 1.0);
    }

    // ── In-memory test double for IHabituationStore ───────────────────────────

    /// <summary>
    /// Simple in-memory store that saves exactly one <see cref="HabituationState"/>.
    /// Used by integration tests that verify persistence across engine instances.
    /// </summary>
    private sealed class InMemoryHabituationStore : IHabituationStore
    {
        private HabituationState? _stored;
        public HabituationState? Load() => _stored;
        public void Save(HabituationState state) => _stored = state;
    }

    /// <summary>In-memory IHabituationProfileStore round-trip (EM2 tests).</summary>
    private sealed class InMemoryProfileStore : IHabituationProfileStore
    {
        public HabituationProfileSnapshot? Stored;
        public int SaveCount;
        public HabituationProfileSnapshot? Load() => Stored;
        public void Save(HabituationProfileSnapshot snapshot) { Stored = snapshot; SaveCount++; }
    }

    /// <summary>Profile store that succeeds at construction then simulates a
    /// transient read failure (returns null) — for the #2 EM2-hardening test.</summary>
    private sealed class TransientFailProfileStore : IHabituationProfileStore
    {
        public bool FailLoads;
        public int SaveCount;
        public HabituationProfileSnapshot? Stored;
        public TransientFailProfileStore(HabituationProfileSnapshot seed) { Stored = seed; }
        public HabituationProfileSnapshot? Load() => FailLoads ? null : Stored;
        public void Save(HabituationProfileSnapshot snapshot) { SaveCount++; Stored = snapshot; }
    }

    // ── Unit tests ────────────────────────────────────────────────────────────

    [Fact]
    public void NewEngine_HasZeroOffsetAndNotPaused()
    {
        var engine = new HabituationEngine();

        Assert.Equal(0, engine.HabituationOffset);
        Assert.Equal(0, engine.HabituationLevel);
        Assert.False(engine.IsPaused);
        Assert.Equal(HabituationPhase.Observing, engine.Phase);
    }

    [Fact]
    public void NoRelaxation_BeforeStableWindow()
    {
        var engine = new HabituationEngine();

        Calm(engine, 119);

        Assert.Equal(0, engine.HabituationOffset);
    }

    [Fact]
    public void NoRelaxation_ExactlyAtWindow()
    {
        var engine = new HabituationEngine();

        Calm(engine, 120);

        // Window is just satisfied; the step accumulator has received 1 tick (< 30 s interval).
        Assert.Equal(0, engine.HabituationOffset);
    }

    [Fact]
    public void Relaxes_AfterWindowPlusStepInterval()
    {
        var engine = new HabituationEngine();

        // 120 s window + 30 s step interval = first step fires at call 150.
        Calm(engine, 150);

        Assert.Equal(2, engine.HabituationOffset);
    }

    [Fact]
    public void Relaxation_ProgressesAtCadence()
    {
        var engine = new HabituationEngine();

        // 150 → offset 2; 180 → 4; 210 → 6.
        Calm(engine, 210);
        Assert.Equal(6, engine.HabituationOffset);

        // 30 more seconds → one additional step → offset 8.
        Calm(engine, 30);
        Assert.Equal(8, engine.HabituationOffset);
    }

    [Fact]
    public void Relaxation_NeverExceedsMaxOffset()
    {
        var engine = new HabituationEngine();

        // Cap is 30; first step: N=150; cap reached at N=570. Drive well past that.
        Calm(engine, 900);

        Assert.Equal(30, engine.HabituationOffset);
    }

    [Fact]
    public void HabituationLevel_IsOffsetPercentOfCap()
    {
        var engine = new HabituationEngine();

        // offset 6 / 30 * 100 = 20.
        Calm(engine, 210);
        Assert.Equal(20, engine.HabituationLevel);

        // Drive to cap: offset 30 / 30 * 100 = 100.
        Calm(engine, 360); // total 570 → offset 30
        Assert.Equal(100, engine.HabituationLevel);
    }

    [Fact]
    public void Relaxing_SetsRelaxingPhase()
    {
        var engine = new HabituationEngine();

        Calm(engine, 150);

        Assert.Equal(HabituationPhase.Relaxing, engine.Phase);
    }

    [Fact]
    public void MaskActive_SuppressesCalmCredit()
    {
        var engine = new HabituationEngine();

        // Mask engaged: calm condition is pressure < 25 AND !maskActive.
        // With maskActive=true the engine is not calm, so _stableElapsed decays
        // (mid-band path at rate 0.5). After 300 calls no relaxation should occur.
        for (int i = 0; i < 300; i++)
            engine.Step(10.0, true, 1.0);

        Assert.Equal(0, engine.HabituationOffset);
    }

    [Fact]
    public void PressureSpike_HalvesOffsetAndPauses()
    {
        var engine = new HabituationEngine();

        // Build offset to 20 (N=420: 120 window + 15 steps × 2 pp each).
        Calm(engine, 420);
        Assert.Equal(20, engine.HabituationOffset); // pre-condition

        // Spike: pressure ≥ 60 (= 100 * 0.60 recurrence threshold).
        engine.Step(70.0, false, 1.0);

        Assert.Equal(10, engine.HabituationOffset);
        Assert.True(engine.IsPaused);
    }

    [Fact]
    public void MidBandPressure_DecaysStabilityCredit()
    {
        var engine = new HabituationEngine();

        // Phase A: 100 s calm → _stableElapsed = 100.
        Calm(engine, 100);

        // Phase B: 100 mid-band steps (40 < 60 threshold, not a spike; 40 > 25 threshold, not calm).
        // Each step: _stableElapsed -= 1.0 × 0.5 = 0.5, so 100 steps → _stableElapsed = 50.
        for (int i = 0; i < 100; i++)
            engine.Step(40.0, false, 1.0);

        // Phase C: 70 calm → _stableElapsed reaches min(120, 50+70) = 120.
        // Window satisfied on the 70th call; _stepAccumulator = 1 (not yet a full 30 s interval).
        Calm(engine, 70);
        Assert.Equal(0, engine.HabituationOffset); // step interval not yet reached

        // Phase D: 30 more calm → accumulator reaches 31 → step fires.
        Calm(engine, 30);
        Assert.Equal(2, engine.HabituationOffset);
    }

    [Fact]
    public void NotifyDiscomfort_SingleSevere_HalvesOffsetUnderStrikeWeight()
    {
        // Phase 3 re-pin: the old test asserted severe → zero in one call.
        // Under the strike-counter logic chatbot-derived severe events are
        // weighted 0.5 (per Q4); RESILIENT.SevereStrikeLimit = 2, so a single
        // chatbot severe halves the offset (count 0 → 0.5, below limit).
        // The "accumulates to zero on the 4th call" assertion lives in
        // HabituationStrikeAndStateTests.cs.
        var engine = new HabituationEngine();

        Calm(engine, 420);
        Assert.Equal(20, engine.HabituationOffset); // pre-condition

        engine.NotifyDiscomfort(severe: true);

        Assert.Equal(10, engine.HabituationOffset); // 20 × 0.5 = 10 (halved, not zeroed)
        Assert.True(engine.IsPaused);
        Assert.Equal(0.5, engine.SevereStrikeCount);
    }

    [Fact]
    public void NotifyDiscomfort_Mild_AppliesSmallBackoff()
    {
        var engine = new HabituationEngine();

        Calm(engine, 420);
        Assert.Equal(20, engine.HabituationOffset); // pre-condition

        // Mild: offset = max(0, 20 - 4) = 16.
        engine.NotifyDiscomfort(severe: false);

        Assert.Equal(16, engine.HabituationOffset);
        Assert.True(engine.IsPaused);
    }

    [Fact]
    public void Paused_DoesNotRelax()
    {
        var engine = new HabituationEngine();

        engine.SetPaused(true);
        Calm(engine, 600);

        Assert.Equal(0, engine.HabituationOffset);
    }

    [Fact]
    public void Resume_ContinuesRelaxation()
    {
        var engine = new HabituationEngine();

        // Build to offset 2.
        Calm(engine, 150);
        Assert.Equal(2, engine.HabituationOffset); // pre-condition

        // Pause; calm ticks do not advance the step accumulator (early return while paused).
        engine.SetPaused(true);
        Calm(engine, 100);
        Assert.Equal(2, engine.HabituationOffset); // unchanged while paused

        // Resume and drive 30 more calm ticks; accumulator reaches 30 → one more step.
        engine.SetPaused(false);
        Calm(engine, 30);

        Assert.True(engine.HabituationOffset > 2);
    }

    [Fact]
    public void ResetPostCalibration_ClearsAllState()
    {
        var engine = new HabituationEngine();
        Calm(engine, 420);

        engine.ResetPostCalibration();

        Assert.Equal(0, engine.HabituationOffset);
        Assert.Equal(0, engine.HabituationLevel);
        Assert.False(engine.IsPaused);
        Assert.Equal(HabituationPhase.Observing, engine.Phase);
    }

    // ── C5 / Q4.5: a shader / personalization reset must preserve _offset ──────

    [Fact]
    public void ResetTransientState_PreservesOffset()
    {
        var engine = new HabituationEngine();
        engine.Restore(new HabituationState { Offset = 18 });
        Assert.Equal(18, engine.HabituationOffset);   // precondition

        engine.ResetTransientState();

        Assert.Equal(18, engine.HabituationOffset);   // Q4.5: long-term progress preserved
        Assert.False(engine.IsPaused);
        Assert.Equal(HabituationPhase.Observing, engine.Phase);
    }

    [Fact]
    public void ResetPostCalibration_ZeroesOffset()
    {
        var engine = new HabituationEngine();
        engine.Restore(new HabituationState { Offset = 18 });

        engine.ResetPostCalibration();

        Assert.Equal(0, engine.HabituationOffset);
    }

    [Fact]
    public void ResetPersonalization_PreservesHabituationOffset_Q45()
    {
        // Seed a non-zero earned offset into a fresh engine via its store, then run
        // the chatbot-style personalization reset and assert the PERSISTED offset
        // survived. The old _habituation.Reset() zeroed it — this is the C5 pin.
        var store = new InMemoryHabituationStore();
        store.Save(new HabituationState { Offset = 18 });
        var engine = new MockInferenceEngine(habituationStore: store);

        engine.ResetPersonalization();

        Assert.Equal(18.0, store.Load()!.Offset, 3);
    }

    // ── EM1: severe-strike registration is edge-triggered, not level-triggered ──

    [Fact]
    public void Step_SustainedHighPressure_DoesNotStormStrikes_EdgeNotLevel()
    {
        double high = new HabituationEngine().EffectiveTrigger * 0.7; // above the 0.60 recurrence fraction

        var single = new HabituationEngine();
        single.Step(high, maskActive: true, vimsTier: 3, 1.0);
        Assert.NotNull(single.LastSpikeAtUtc);                 // a strike fired on the rising edge
        double afterSingle = single.SevereStrikeCount;

        var sustained = new HabituationEngine();
        for (int i = 0; i < 100; i++)
            sustained.Step(high, maskActive: true, vimsTier: 3, 1.0);

        // 100 ticks at a sustained high LEVEL must register the SAME strikes as a
        // single rising EDGE — not 100x. (Old code called RegisterSevereStrike every tick.)
        Assert.Equal(afterSingle, sustained.SevereStrikeCount);
    }

    // ── D5: a deliberate "more protection" preference must NOT count as a
    //         discomfort recurrence — Q4.5 offset preserved, no pause, FOV still moves ──

    [Fact]
    public void ApplyUserFeedback_DeliberatePreference_DoesNotErodeHabituationOrPause()
    {
        var engine = new MockInferenceEngine();
        engine.SetHabituationPaused(false);
        for (int i = 0; i < 600; i++) engine.Update(0.0, "Passenger"); // earn progress
        int earned = engine.Current.HabituationLevel;
        Assert.True(earned > 0, "precondition: must have earned habituation progress");

        // Severe-magnitude tightening (|−22| ≥ 18), but flagged deliberate.
        engine.ApplyUserFeedback(-22, isDeliberatePreference: true);

        Assert.Equal(earned, engine.Current.HabituationLevel);     // Q4.5 offset preserved
        Assert.False(engine.Current.HabituationPaused);            // no discomfort pause
        Assert.True(engine.UserFovOffset < 0);                     // protection STILL applied
    }

    [Fact]
    public void ApplyUserFeedback_DefaultPath_StillCountsAsDiscomfort()
    {
        var engine = new MockInferenceEngine();
        engine.SetHabituationPaused(false);
        for (int i = 0; i < 600; i++) engine.Update(0.0, "Passenger");
        int earned = engine.Current.HabituationLevel;

        engine.ApplyUserFeedback(-22); // default false → severe discomfort recurrence

        Assert.True(engine.Current.HabituationLevel < earned, "default path must erode offset");
        Assert.True(engine.Current.HabituationPaused, "default path must pause");
    }

    // ── EM2: an engine profile-snapshot persist must not wipe the chatbot's
    //         longitudinal observations (owned by LongitudinalObservationWriter) ──

    [Fact]
    public void EnginePersist_PreservesLongitudinalObservations_EM2()
    {
        var store = new InMemoryProfileStore();
        store.Save(new HabituationProfileSnapshot { Profile = "RESILIENT" });

        var writer = new LongitudinalObservationWriter(store);
        Assert.True(writer.Append("user reports symptoms worsen on Mondays"));
        Assert.Single(store.Load()!.LongitudinalObservations);   // precondition

        var engine = new MockInferenceEngine(profileStore: store);
        engine.SetHabituationProfile(HabituationProfile.Sensitive, "calibration_demo"); // triggers a profile-snapshot Save

        // EM2: BuildProfileSnapshot read-modify-writes, so the observation survives.
        var after = store.Load()!;
        Assert.Single(after.LongitudinalObservations);
        Assert.Contains("Mondays", after.LongitudinalObservations[0]);
        Assert.Equal("SENSITIVE", after.Profile);                // the engine's change also landed
    }

    [Fact]
    public void EnginePersist_TransientLoadFailure_DoesNotClobberAdvisory_EM2Hardening()
    {
        // #2: if the profile existed at boot but a later Load() fails transiently,
        // the engine must SKIP the persist rather than null-fill + overwrite the
        // vestibular advisory.
        var seed = new HabituationProfileSnapshot
        {
            Profile = "SENSITIVE",
            MedicalAdvisoryShown = MedicalAdvisory.Vestibular,
        };
        var store = new TransientFailProfileStore(seed);
        var engine = new MockInferenceEngine(profileStore: store); // ctor Load succeeds -> existed-at-boot
        store.FailLoads = true;                                     // now every Load returns null

        engine.SetHabituationProfile(HabituationProfile.Typical, "force"); // triggers a persist attempt

        Assert.Equal(0, store.SaveCount);                          // persist skipped
        Assert.Equal(MedicalAdvisory.Vestibular, store.Stored!.MedicalAdvisoryShown); // advisory survived
    }

    [Fact]
    public void Step_NonPositiveDt_IsNoOp()
    {
        var engine = new HabituationEngine();

        Calm(engine, 150);
        Assert.Equal(2, engine.HabituationOffset); // pre-condition

        // dt=0 and negative dt must be silently ignored.
        engine.Step(10.0, false, 0.0);
        engine.Step(10.0, false, -5.0);

        Assert.Equal(2, engine.HabituationOffset);
    }

    [Fact]
    public void Personalization_CautiousUser_StartsLater()
    {
        var engine = new HabituationEngine();

        // PersonalWeight("FovRadius", count=5, netDelta=-75, meanDelta=-15):
        //   rawCaution = clamp(15/15, 0, 1) = 1.0
        //   confidence  = clamp(5/3,  0, 1) = 1.0
        //   caution = 1.0
        //   effectiveWindow      = 120 + 1×(240-120) = 240 s
        //   effectiveStepInterval = 30 + 1×(75-30)   = 75 s
        // A neutral engine would yield offset 2 after 150 calls; cautious still 0 (needs 240).
        engine.SetPersonalization(new PersonalWeight("FovRadius", 5, -75, -15));

        Calm(engine, 150);

        Assert.Equal(0, engine.HabituationOffset);
    }

    [Fact]
    public void Personalization_NeutralUser_UsesNominalRate()
    {
        var engine = new HabituationEngine();

        // Explicitly setting a zero-history weight must leave nominal parameters unchanged.
        engine.SetPersonalization(new PersonalWeight("FovRadius", 0, 0, 0));

        Calm(engine, 150);

        Assert.Equal(2, engine.HabituationOffset);
    }

    [Fact]
    public void Snapshot_And_Restore_PreservesOffset()
    {
        var engineA = new HabituationEngine();

        // offset 6 at N=210.
        Calm(engineA, 210);
        Assert.Equal(6, engineA.HabituationOffset); // pre-condition

        HabituationState snap = engineA.Snapshot();

        // Restore into a fresh engine; stability counters restart → Phase resets to Observing.
        var engineB = new HabituationEngine();
        engineB.Restore(snap);

        Assert.Equal(6, engineB.HabituationOffset);
        Assert.Equal(HabituationPhase.Observing, engineB.Phase);
    }

    // ── Integration tests (MockInferenceEngine) ───────────────────────────────

    [Fact]
    public void MockEngine_HabituationNeverPushesFovAbove100()
    {
        var engine = new MockInferenceEngine();
        engine.SetHabituationPaused(false);

        // OFI=0 keeps pressure low (calm path); run 600 ticks so habituation reaches cap.
        SenseState state = engine.Current;
        for (int i = 0; i < 600; i++)
            state = engine.Update(0.0, "Passenger");

        // Habituation relaxes the FOV upward, but the final radius must be ≤ 100 %.
        Assert.True(state.FovRadiusPercent <= 100,
            $"FovRadiusPercent {state.FovRadiusPercent} exceeds upper clamp.");
        Assert.True(state.HabituationLevel >= 0,
            $"HabituationLevel {state.HabituationLevel} is negative.");
    }

    [Fact]
    public void MockEngine_DiscomfortFeedbackPausesHabituation()
    {
        var engine = new MockInferenceEngine();
        engine.SetHabituationPaused(false);

        // Drive enough ticks to establish a non-trivial session (>= 120 s calm equivalent).
        for (int i = 0; i < 200; i++)
            engine.Update(0.0, "Passenger");

        // Negative feedback with |delta| >= HabituationSevereFovThreshold (18) is severe.
        engine.ApplyUserFeedback(-22);

        Assert.True(engine.Current.HabituationPaused,
            "HabituationPaused should be true after a severe discomfort report.");
    }

    [Fact]
    public void MockEngine_HabituationProgressSurvivesViaStore()
    {
        var store = new InMemoryHabituationStore();

        // Engine A: drive until habituation level is non-zero.
        // dt=1.0 per Update(); need ≥ 150 calm ticks for first relaxation step.
        // Use 600 ticks to ensure the store has captured a non-zero offset.
        // Habituation now defaults to PAUSED at construction — explicit opt-in is the
        // therapeutic posture. The test must un-pause to exercise progress accumulation.
        var engineA = new MockInferenceEngine(null, store);
        engineA.SetHabituationPaused(false);
        for (int i = 0; i < 600; i++)
            engineA.Update(0.0, "Passenger");

        Assert.True(engineA.Current.HabituationLevel > 0,
            $"Engine A must have made habituation progress (got level {engineA.Current.HabituationLevel}).");

        // Engine B: created with the same store; constructor calls store.Load() and
        // engine.Restore() so the saved offset is replayed into a fresh engine.
        // Default-pause is independent of restored offset — restore brings the EARNED
        // progress back; whether the engine RESUMES relaxing is a separate user choice.
        var engineB = new MockInferenceEngine(null, store);

        Assert.True(engineB.Current.HabituationLevel > 0,
            $"Engine B must restore habituation progress from the store (got level {engineB.Current.HabituationLevel}).");
    }

    [Fact]
    public void PausedDuringObserving_DoesNotAccumulateWindow()
    {
        var engine = new HabituationEngine();

        // Pause and drive 400 calm ticks; a pre-C2-fix engine would silently fill
        // the 120 s window during the pause and then immediately relax on resume.
        engine.SetPaused(true);
        Calm(engine, 400);
        engine.SetPaused(false);

        // 119 calm ticks after resume: window is 120 s, so not yet satisfied.
        Calm(engine, 119);
        Assert.Equal(0, engine.HabituationOffset);

        // 31 more calm ticks: window fills (119+31=150 ≥ 120) and the 30 s step
        // interval elapses (31 ticks ≥ 30) → first relaxation step fires.
        Calm(engine, 31);
        Assert.Equal(2, engine.HabituationOffset);
    }

    [Fact]
    public void Restore_ClampsOffsetAboveMax()
    {
        var engine = new HabituationEngine();

        engine.Restore(new HabituationState { Offset = 999.0 });

        Assert.Equal(30, engine.HabituationOffset);
    }

    [Fact]
    public void Restore_ClampsNegativeOffsetToZero()
    {
        var engine = new HabituationEngine();

        engine.Restore(new HabituationState { Offset = -5.0 });

        Assert.Equal(0, engine.HabituationOffset);
    }

    [Fact]
    public void MockEngine_UserFeedbackTightensFov()
    {
        var engine = new MockInferenceEngine();

        // One update tick establishes a baseline pressure and FOV.
        engine.Update(0.0, "Passenger");
        int fovBefore = engine.Current.FovRadiusPercent;

        // Negative feedback tightens the FOV (negative offset, then NotifyDiscomfort).
        engine.ApplyUserFeedback(-20);
        int fovAfter = engine.Current.FovRadiusPercent;

        Assert.True(fovAfter < fovBefore,
            $"FovRadiusPercent should have decreased after tightening feedback (was {fovBefore}, got {fovAfter}).");
        Assert.True(fovAfter >= 15,
            $"FovRadiusPercent {fovAfter} must not fall below the lower clamp of 15.");
    }

    // ── VIMS Tier hysteresis tests ─────────────────────────────────────────────
    // VimsTier is now PRESSURE-driven (not FOV-driven). User-narrowed FOV no longer
    // raises the tier; only current pressure does. These tests drive pressure via
    // sustained OFI input and assert tier transitions, mirroring real vection onset.

    /// <summary>Pump high OFI into the engine until pressure crosses the requested
    /// fraction of TriggerPressure. Returns the final state after the last tick.</summary>
    private static SenseState DriveToPressureFraction(MockInferenceEngine engine, double pressureFractionOfTrigger, int maxTicks = 200)
    {
        engine.SetHabituationPaused(true);
        double targetPressure = pressureFractionOfTrigger * SenseDefaults.TriggerPressure;
        SenseState state = engine.Current;
        DateTime t0 = DateTime.UtcNow;
        for (int i = 0; i < maxTicks; i++)
        {
            state = engine.Update(120.0, "Passenger", t0.AddMilliseconds(i * 100));
            if (state.Pressure >= targetPressure) break;
        }
        return state;
    }

    [Fact]
    public void VimsTier_AtRestPressure0_IsTier1()
    {
        var engine = new MockInferenceEngine();
        var state = engine.Update(0.0, "Passenger", DateTime.UtcNow);
        Assert.Equal(1, state.VimsTier);
    }

    [Fact]
    public void VimsTier_UserShrinksFov_ButPressureZero_StaysTier1()
    {
        // Regression: chatbot "I feel dizzy" path used to raise tier through FOV
        // shrinkage alone — the mask appeared on a static screen. Tier must stay at
        // 1 when pressure is zero, no matter how aggressive the user's FOV preference.
        var engine = new MockInferenceEngine();
        engine.SetHabituationPaused(true);
        engine.ApplyUserFeedback(-60); // maximum tightening
        var state = engine.Update(0.0, "Passenger", DateTime.UtcNow);
        Assert.Equal(1, state.VimsTier);
    }

    [Fact]
    public void VimsTier_AtPeakPressure_ClimbsToTopTiers()
    {
        var engine = new MockInferenceEngine();
        var state = DriveToPressureFraction(engine, 1.0);
        Assert.True(state.VimsTier >= 6,
            $"Tier should have climbed substantially under sustained high pressure (got tier {state.VimsTier}, pressure {state.Pressure:F1}).");
    }

    [Fact]
    public void VimsTier_FastOnsetBypass_SnapsOnPressureSurge()
    {
        var engine = new MockInferenceEngine();
        // Start at rest (tier 1, pressure 0).
        var s1 = engine.Update(0.0, "Passenger", DateTime.UtcNow);
        Assert.Equal(1, s1.VimsTier);
        // Pump several large-OFI ticks back-to-back to surge pressure past trigger.
        // The fast-onset bypass snaps tier directly when levelF jumps by ≥ VimsTierFastOnsetGap.
        SenseState state = s1;
        for (int i = 0; i < 8; i++)
            state = engine.Update(150.0, "Passenger", DateTime.UtcNow.AddMilliseconds(i * 100));
        Assert.True(state.VimsTier >= 5,
            $"Fast-onset bypass should snap tier ≥ 5 once pressure surges past trigger (got tier {state.VimsTier}, pressure {state.Pressure:F1}).");
    }

    [Fact]
    public void VimsTier_TierIsStable_WithinDeadband()
    {
        // Once settled, small pressure fluctuations within the ±deadband should NOT
        // change the tier. We drive to a stable mid-range tier by holding OFI flat
        // for long enough that pressure stops climbing, then verify a one-tick OFI
        // perturbation (still inside the band) doesn't move the tier.
        var engine = new MockInferenceEngine();
        engine.SetHabituationPaused(true);
        SenseState state = engine.Current;
        DateTime t0 = DateTime.UtcNow;
        // Hold OFI at a modest level; let pressure reach quasi-steady-state.
        for (int i = 0; i < 60; i++)
            state = engine.Update(30.0, "Passenger", t0.AddMilliseconds(i * 100));
        int initialTier = state.VimsTier;
        // A small nudge in OFI keeps levelF within ±deadband of the current tier;
        // tier should hold.
        var s = engine.Update(32.0, "Passenger", t0.AddMilliseconds(60 * 100 + 100));
        Assert.Equal(initialTier, s.VimsTier);
    }
}
