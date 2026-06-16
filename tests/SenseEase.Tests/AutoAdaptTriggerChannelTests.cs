using SenseEase.Core.Abstractions;
using SenseEase.Core.Configuration;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// B5 Stage 2 — the dedicated <c>AutoAdaptTriggerDeltaPp</c> engine channel: the seam that applies the
/// AdaptationGate's decision. Verifies the sign (relax raises EffectiveTrigger, tighten lowers it), the
/// budget clamp, the difference-application (auto-delta never accumulates and is preserved across a user
/// sensitivity change — the v1 overwrite bug), Reset-zeroing (an auto-relax can never survive a Reset),
/// the vestibular composed cap, persistence, and boot re-composition.
/// </summary>
public class AutoAdaptTriggerChannelTests
{
    /// <summary>A profile store that captures the last Save and serves a seeded snapshot on Load —
    /// lets the tests exercise the boot-restore + save paths without disk I/O.</summary>
    private sealed class CapturingProfileStore : IHabituationProfileStore
    {
        public HabituationProfileSnapshot? Saved;
        public HabituationProfileSnapshot? Load() => Saved;
        public void Save(HabituationProfileSnapshot snapshot) => Saved = snapshot;
    }

    private static double FreshBaseTrigger()
        => new MockInferenceEngine(null, null, null, null).EffectiveTrigger; // the default base (no auto delta)

    // ── Sign + magnitude ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Relax_RaisesEffectiveTrigger_LessProtection()
    {
        var engine = new MockInferenceEngine(null, null, null, null);
        double b = engine.EffectiveTrigger;
        engine.ApplyAutoAdaptDelta(3.0);
        Assert.Equal(b + 3.0, engine.EffectiveTrigger, 6); // higher trigger = fires later = less protection
        Assert.Equal(3.0, engine.AutoAdaptTriggerDeltaPp, 6);
    }

    [Fact]
    public void Tighten_LowersEffectiveTrigger_MoreProtection()
    {
        var engine = new MockInferenceEngine(null, null, null, null);
        double b = engine.EffectiveTrigger;
        engine.ApplyAutoAdaptDelta(-3.0);
        Assert.Equal(b - 3.0, engine.EffectiveTrigger, 6);
        Assert.Equal(-3.0, engine.AutoAdaptTriggerDeltaPp, 6);
    }

    [Fact]
    public void Delta_ClampedToSpan_AtEngineBoundary()
    {
        var engine = new MockInferenceEngine(null, null, null, null);
        double b = engine.EffectiveTrigger;
        engine.ApplyAutoAdaptDelta(50.0);                  // way past the ±12 span
        Assert.Equal(12.0, engine.AutoAdaptTriggerDeltaPp, 6);
        Assert.Equal(b + 12.0, engine.EffectiveTrigger, 6);
    }

    [Fact]
    public void Delta_AppliedAsDifference_NeverAccumulates()
    {
        var engine = new MockInferenceEngine(null, null, null, null);
        double b = engine.EffectiveTrigger;
        engine.ApplyAutoAdaptDelta(3.0);
        engine.ApplyAutoAdaptDelta(6.0);                   // absolute target 6, not 3+6
        Assert.Equal(6.0, engine.AutoAdaptTriggerDeltaPp, 6);
        Assert.Equal(b + 6.0, engine.EffectiveTrigger, 6);
    }

    [Fact]
    public void NonFinite_IsNoOp()
    {
        var engine = new MockInferenceEngine(null, null, null, null);
        engine.ApplyAutoAdaptDelta(6.0);
        double before = engine.EffectiveTrigger;
        engine.ApplyAutoAdaptDelta(double.NaN);
        Assert.Equal(before, engine.EffectiveTrigger, 6);
        Assert.Equal(6.0, engine.AutoAdaptTriggerDeltaPp, 6);
    }

    // ── Composition with the user channel ──────────────────────────────────────────────────────────

    [Fact]
    public void AdjustSensitivity_PreservesAutoDelta_OnTopOfNewUserBase()
    {
        // The v1 overwrite bug: a user sensitivity change must NOT erase the gate's contribution.
        var engine = new MockInferenceEngine(null, null, null, null);
        engine.ApplyAutoAdaptDelta(6.0);
        engine.ApplyChatbotOverride(OverrideIntent.AdjustSensitivity, 0.8); // base = 85/0.8 = 106.25
        Assert.Equal(SenseDefaults.TriggerPressure / 0.8 + 6.0, engine.EffectiveTrigger, 6);
        Assert.Equal(6.0, engine.AutoAdaptTriggerDeltaPp, 6); // preserved
    }

    // ── Reset ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_ZeroesAutoDelta_AnAutoRelaxNeverSurvivesReset()
    {
        var engine = new MockInferenceEngine(null, null, null, null);
        double b = engine.EffectiveTrigger;
        engine.ApplyAutoAdaptDelta(6.0);                   // an auto-relax (less protection)
        Assert.Equal(b + 6.0, engine.EffectiveTrigger, 6);

        engine.ResetPersonalization();
        Assert.Equal(0.0, engine.AutoAdaptTriggerDeltaPp, 6);
        Assert.Equal(b, engine.EffectiveTrigger, 6);       // back to base (more protection than the relaxed state)
    }

    // ── Vestibular floor ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Vestibular_RuntimeRelax_CappedAtFloor()
    {
        var engine = new MockInferenceEngine(null, null, null, null);
        engine.SetVestibularProtection(true);              // base trigger = 50
        engine.ApplyAutoAdaptDelta(6.0);                   // a relax would push to 56 …
        Assert.True(engine.EffectiveTrigger <= 50.0);      // … but the vestibular cap holds it at the floor
    }

    [Fact]
    public void Vestibular_RuntimeTighten_Lowers()
    {
        var engine = new MockInferenceEngine(null, null, null, null);
        engine.SetVestibularProtection(true);
        engine.ApplyAutoAdaptDelta(-6.0);
        Assert.Equal(44.0, engine.EffectiveTrigger, 6);    // 50 − 6 (tighter than the vestibular floor)
    }

    // ── Persistence + boot re-composition ────────────────────────────────────────────────────────────

    [Fact]
    public void Persisted_ReappliesOnBoot_NonVestibular()
    {
        double b = FreshBaseTrigger();
        var snap = new HabituationProfileSnapshot { PersonalPressureScale = 1.0, AutoAdaptTriggerDeltaPp = 6.0 };
        var engine = new MockInferenceEngine(null, null, new CapturingProfileStore { Saved = snap }, null);
        Assert.Equal(b + 6.0, engine.EffectiveTrigger, 6);
        Assert.Equal(6.0, engine.AutoAdaptTriggerDeltaPp, 6);
    }

    [Fact]
    public void Persisted_ReappliesOnBoot_Vestibular_RelaxStillCapped()
    {
        var snap = new HabituationProfileSnapshot
        {
            VestibularProtectionEnabled = true, PersonalPressureScale = 1.0, AutoAdaptTriggerDeltaPp = 6.0,
        };
        var engine = new MockInferenceEngine(null, null, new CapturingProfileStore { Saved = snap }, null);
        Assert.Equal(50.0, engine.EffectiveTrigger, 6); // vestibular base 50 + relax 6, capped back to 50
    }

    [Fact]
    public void Persisted_ReappliesOnBoot_Vestibular_TightenSurvives()
    {
        var snap = new HabituationProfileSnapshot
        {
            VestibularProtectionEnabled = true, PersonalPressureScale = 1.0, AutoAdaptTriggerDeltaPp = -6.0,
        };
        var engine = new MockInferenceEngine(null, null, new CapturingProfileStore { Saved = snap }, null);
        Assert.Equal(44.0, engine.EffectiveTrigger, 6); // 50 − 6, tighter than the floor (allowed)
    }

    [Fact]
    public void Vestibular_RetiresStaleRelaxDelta_OnTransition_NoResurrection()
    {
        // A typical user auto-relaxes (+6), then becomes vestibular: the relax must be RETIRED (not just
        // hidden by the cap), so turning vestibular back OFF can never resurrect it (final-review MED).
        var engine = new MockInferenceEngine(null, null, null, null);
        engine.ApplyAutoAdaptDelta(6.0);                       // +6 relax (typical)
        double baseTrigger = engine.EffectiveTrigger - 6.0;    // the non-vestibular base

        engine.SetVestibularProtection(true);
        Assert.Equal(0.0, engine.AutoAdaptTriggerDeltaPp, 9);   // relax retired
        Assert.Equal(50.0, engine.EffectiveTrigger, 9);

        engine.SetVestibularProtection(false);
        Assert.Equal(baseTrigger, engine.EffectiveTrigger, 9);  // back to base — the +6 did NOT come back
        Assert.Equal(0.0, engine.AutoAdaptTriggerDeltaPp, 9);
    }

    [Fact]
    public void Vestibular_RuntimeRelax_IsClampedToZero_AtEngineBoundary()
    {
        var engine = new MockInferenceEngine(null, null, null, null);
        engine.SetVestibularProtection(true);
        engine.ApplyAutoAdaptDelta(6.0);                        // a relax attempt
        Assert.Equal(0.0, engine.AutoAdaptTriggerDeltaPp, 9);   // clamped to 0 (vestibular never relaxes)
        Assert.Equal(50.0, engine.EffectiveTrigger, 9);
    }

    [Fact]
    public void ConsentOff_VestibularAdjustSensitivity_NotCapped_NoRegression()
    {
        // delta==0 (B5 never used): ApplyAutoDeltaToFreshBase must be a true no-op, so a vestibular
        // user's chatbot AdjustSensitivity is byte-identical to pre-B5 (85/scale, NOT clamped to 50).
        var engine = new MockInferenceEngine(null, null, null, null);
        engine.SetVestibularProtection(true);
        engine.ApplyChatbotOverride(OverrideIntent.AdjustSensitivity, 1.2);
        Assert.Equal(SenseDefaults.TriggerPressure / 1.2, engine.EffectiveTrigger, 6); // ~70.8, not 50
    }

    [Fact]
    public void ApplyDelta_PersistsToSnapshot()
    {
        var store = new CapturingProfileStore();
        var engine = new MockInferenceEngine(null, null, store, null);
        engine.ApplyAutoAdaptDelta(6.0);
        engine.FlushPendingPersists();
        Assert.NotNull(store.Saved);
        Assert.Equal(6.0, store.Saved!.AutoAdaptTriggerDeltaPp, 6);
    }
}
