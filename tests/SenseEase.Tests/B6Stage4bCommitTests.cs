using System;
using System.Collections.Generic;
using System.Linq;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Adaptation;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// B6 Stage 4b — the SINGLE-LOCKED-COMMIT (<c>MockInferenceEngine.ApplyGateOutcome</c>) + Reset revert +
/// F6 audit-only + the shared-budget derive. Each test pins one safety invariant from
/// docs/B6_STAGE4B_IMPLEMENTATION.md §10. ApplyGateOutcome takes a pre-built verdict + trigger decision,
/// so these exercise the COMMIT's aborts / coherence guards / retracts directly (no MeasuredState needed).
/// </summary>
public sealed class B6Stage4bCommitTests
{
    private sealed class FakeLog : IAdjustmentLog
    {
        public readonly List<AdjustmentRecord> Records = new();
        public void Append(AdjustmentRecord r) => Records.Add(r);
        public IReadOnlyList<AdjustmentRecord> All() => Records;
        public PersonalWeight WeightFor(string parameter) => new(parameter, 0, 0.0, 0.0);
    }

    private sealed class InMemoryProfileStore : IHabituationProfileStore
    {
        public HabituationProfileSnapshot? Stored;
        public HabituationProfileSnapshot? Load() => Stored;
        public void Save(HabituationProfileSnapshot snapshot) => Stored = snapshot;
    }

    private static GateDecision Hold => new GateDecision.Hold("test-hold");
    private static GateDecision Relax(double newDelta) => new GateDecision.Apply(newDelta, 85.0 + newDelta, AdaptDirection.Relax, "relax");
    private static GateDecision Tighten(double newDelta) => new GateDecision.Apply(newDelta, 85.0 + newDelta, AdaptDirection.Tighten, "tighten");

    private static AdaptationCommitRequest Req(
        MockInferenceEngine e, ProfileMove move, HabituationProfile target, GateDecision trigger,
        double? snapDelta = null, bool? snapVest = null, int? snapLevel = null,
        HabituationProfile? snapProfile = null, double? snapStrike = null)
    {
        var s = e.SnapshotForAdaptation();
        return new AdaptationCommitRequest(
            snapProfile ?? s.Profile, snapDelta ?? s.AutoDeltaPp, snapVest ?? s.Vestibular,
            snapLevel ?? s.HabituationLevel, snapStrike ?? s.StrikeCount,
            move, target, trigger);
    }

    private static MockInferenceEngine Engine(FakeLog log, IEventLogStore? events = null, IHabituationProfileStore? store = null)
        => new(adjustmentLog: log, profileStore: store, eventLog: events);

    private static void Recent(FakeLog log, string param, double delta)
        => log.Append(new AdjustmentRecord { Parameter = param, Delta = delta, Source = "system", TimestampUtc = DateTime.UtcNow });

    // ════════════════════════════════ NO-OP + ABORTS ════════════════════════════════

    [Fact] // NOOP_LOCK_FREE
    public void Commit_HoldHold_IsCheapNoOp()
    {
        var e = Engine(new FakeLog());
        var r = e.ApplyGateOutcome(Req(e, ProfileMove.Hold, e.HabituationProfile, Hold));
        Assert.False(r.ProfileMoved);
        Assert.False(r.TriggerMoved);
        Assert.Equal("no-op", r.Reason);
    }

    [Fact] // MISMATCH_ABORT_DELTA
    public void Commit_StaleDeltaSnapshot_AbortsHold()
    {
        var e = Engine(new FakeLog());
        e.SetHabituationProfile(HabituationProfile.Adapting, "calibration_demo");
        var r = e.ApplyGateOutcome(Req(e, ProfileMove.Down, HabituationProfile.Reactive, Hold,
            snapDelta: e.AutoAdaptTriggerDeltaPp + 5.0));   // verdict computed against a now-stale delta
        Assert.False(r.ProfileMoved);
        Assert.Equal("snapshot-vs-live-mismatch", r.Reason);
        Assert.Equal(HabituationProfile.Adapting, e.HabituationProfile);   // unchanged
    }

    [Fact] // VESTIBULAR_LIVE_FLIP_ABORT — live vestibular but the off-lock snapshot saw non-vestibular.
    public void Commit_LiveVestibularVsNonVestibularSnapshot_Aborts()
    {
        var e = Engine(new FakeLog());
        e.SetVestibularProtection(true);
        var r = e.ApplyGateOutcome(Req(e, ProfileMove.Down, HabituationProfile.Reactive, Hold, snapVest: false));
        Assert.False(r.ProfileMoved);
        Assert.Equal("snapshot-vs-live-mismatch", r.Reason);
    }

    [Fact] // UP_STALE_STRIKE_ABORT — a protective drop in level between snapshot and commit aborts an UP.
    public void Commit_UpWithStaleHighLevel_AbortsProtective()
    {
        var e = Engine(new FakeLog());
        e.SetHabituationProfile(HabituationProfile.Adapting, "calibration_demo"); // live level 0 (no offset)
        var r = e.ApplyGateOutcome(Req(e, ProfileMove.Up, HabituationProfile.Resilient, Hold, snapLevel: 95));
        Assert.False(r.ProfileMoved);
        Assert.Equal("up-stale-protective-change", r.Reason);
        Assert.Equal(HabituationProfile.Adapting, e.HabituationProfile);
    }

    [Fact] // A DOWN in the same protective-drift race is NOT aborted (DOWN fails toward protection).
    public void Commit_DownWithStaleHighLevel_DoesNotAbort()
    {
        var e = Engine(new FakeLog());
        e.SetHabituationProfile(HabituationProfile.Adapting, "calibration_demo");
        var r = e.ApplyGateOutcome(Req(e, ProfileMove.Down, HabituationProfile.Reactive, Hold, snapLevel: 95));
        Assert.True(r.ProfileMoved);
        Assert.Equal(HabituationProfile.Reactive, e.HabituationProfile);
    }

    // ════════════════════════════════ COHERENCE GUARDS ════════════════════════════════

    [Fact] // NEVER_BOTH_NET_RELAX — an UP profile move suppresses a same-pass net-relax trigger.
    public void Commit_UpPlusNetRelax_SuppressesTheRelax()
    {
        var e = Engine(new FakeLog());
        e.SetHabituationProfile(HabituationProfile.Adapting, "calibration_demo");
        var r = e.ApplyGateOutcome(Req(e, ProfileMove.Up, HabituationProfile.Resilient, Relax(3.0)));
        Assert.True(r.ProfileMoved);
        Assert.False(r.TriggerMoved);                       // net-relax suppressed
        Assert.Equal(HabituationProfile.Resilient, e.HabituationProfile);
        Assert.Equal(0.0, e.AutoAdaptTriggerDeltaPp);
    }

    [Fact] // TIGHTEN_MAY_STACK — a DOWN profile move + a tighten trigger BOTH apply (both protective).
    public void Commit_DownPlusTighten_BothApply()
    {
        var e = Engine(new FakeLog());
        e.SetHabituationProfile(HabituationProfile.Adapting, "calibration_demo");
        var r = e.ApplyGateOutcome(Req(e, ProfileMove.Down, HabituationProfile.Reactive, Tighten(-3.0)));
        Assert.True(r.ProfileMoved);
        Assert.True(r.TriggerMoved);
        Assert.Equal(HabituationProfile.Reactive, e.HabituationProfile);
        Assert.Equal(-3.0, e.AutoAdaptTriggerDeltaPp);
    }

    [Fact] // DOWN_INTO_SENSITIVE_RETRACTS — a DOWN into Reactive retracts a standing positive (relaxed) delta.
    public void Commit_DownIntoReactive_RetractsStandingRelaxDelta()
    {
        var e = Engine(new FakeLog());
        e.SetHabituationProfile(HabituationProfile.Adapting, "calibration_demo");
        e.ApplyAutoAdaptDelta(6.0);                          // standing relax +6
        Assert.Equal(6.0, e.AutoAdaptTriggerDeltaPp);

        var r = e.ApplyGateOutcome(Req(e, ProfileMove.Down, HabituationProfile.Reactive, Hold));
        Assert.True(r.ProfileMoved);
        Assert.Equal(HabituationProfile.Reactive, e.HabituationProfile);
        Assert.Equal(0.0, e.AutoAdaptTriggerDeltaPp);       // retracted to 0 (NET-protective)
    }

    // ════════════════════════════════ SHARED BUDGET ════════════════════════════════

    [Fact] // The shared weekly relax budget BLOCKS an UP profile move when spent.
    public void Commit_BudgetSpent_BlocksUp()
    {
        var log = new FakeLog();
        Recent(log, "AutoAdaptTrigger", -3.0);
        Recent(log, "AutoAdaptTrigger", -3.0);              // 2 == MaxAutoChangesPerWeek ⇒ spent
        var e = Engine(log);
        e.SetHabituationProfile(HabituationProfile.Adapting, "calibration_demo");
        var r = e.ApplyGateOutcome(Req(e, ProfileMove.Up, HabituationProfile.Resilient, Hold));
        Assert.False(r.ProfileMoved);
        Assert.Equal(HabituationProfile.Adapting, e.HabituationProfile);
    }

    [Fact] // DOWN_BUDGET_EXEMPT — a protective DOWN move still applies even when the budget is spent.
    public void Commit_BudgetSpent_DownStillApplies()
    {
        var log = new FakeLog();
        Recent(log, "AutoAdaptTrigger", -3.0);
        Recent(log, "AutoAdaptTrigger", -3.0);
        var e = Engine(log);
        e.SetHabituationProfile(HabituationProfile.Adapting, "calibration_demo");
        var r = e.ApplyGateOutcome(Req(e, ProfileMove.Down, HabituationProfile.Reactive, Hold));
        Assert.True(r.ProfileMoved);
        Assert.Equal(HabituationProfile.Reactive, e.HabituationProfile);
    }

    [Fact] // PROFILE_ROW_NO_NETRELAX_POLLUTION + budget coupling, at the DeriveAutoChannelTiming layer.
    public void DeriveTiming_ProfileRow_CountsForBudget_NotNetRelax()
    {
        var now = DateTime.UtcNow;
        var rows = new List<AdjustmentRecord>
        {
            new() { Parameter = "AutoReclassifyProfile", Delta = 0.0, Source = "system", TimestampUtc = now },
        };
        var with = AdaptationEvidence.DeriveAutoChannelTiming(rows, now, "AutoAdaptTrigger", "AutoReclassifyProfile");
        Assert.Equal(1, with.AutoChangesThisWeek);          // counted toward the shared budget
        Assert.False(with.HasEverAutoRelaxed);              // Delta=0 ⇒ never a net relax

        var without = AdaptationEvidence.DeriveAutoChannelTiming(rows, now, "AutoAdaptTrigger");
        Assert.Equal(0, without.AutoChangesThisWeek);       // null profileParameter ⇒ trigger-only (byte-identical)
    }

    // ════════════════════════════════ F6 AUDIT-ONLY ════════════════════════════════

    [Fact] // The AUTO move IS written to the durable Event Log (audit kept).
    public void Commit_AutoMove_WritesProfileEventToEventLog()
    {
        IEventLogStore events = new InMemoryEventLogStore();
        var e = Engine(new FakeLog(), events: events);
        e.SetHabituationProfile(HabituationProfile.Adapting, "calibration_demo");
        e.ApplyGateOutcome(Req(e, ProfileMove.Up, HabituationProfile.Resilient, Hold));

        var profileEvents = events.ReadDurable(DateTime.UtcNow.AddDays(-1))
            .Where(x => x.Category == "Habituation.Profile").ToList();
        Assert.Contains(profileEvents, x => x.Message.Contains("auto_elasticity"));
    }

    [Fact] // ...but the chatbot trajectory EXCLUDES the auto move (F6), while a MANUAL move is kept.
    public void Trajectory_ExcludesAutoElasticityMove_KeepsManual()
    {
        var auto = new InMemoryEventLogStore();
        auto.Append(new EventLogEntry { Category = "Habituation.Profile",
            Message = "Adapting → Resilient (source: auto_elasticity)", Timestamp = DateTime.UtcNow });
        Assert.Contains("no habituation activity", HabituationTrajectoryBuilder.BuildSummary(auto));

        var manual = new InMemoryEventLogStore();
        manual.Append(new EventLogEntry { Category = "Habituation.Profile",
            Message = "Adapting → Reactive (source: user_dashboard)", Timestamp = DateTime.UtcNow });
        Assert.DoesNotContain("no habituation activity", HabituationTrajectoryBuilder.BuildSummary(manual));
    }

    // ════════════════════════════════ RESET + PERSIST ════════════════════════════════

    [Fact] // RESET_AUTO_REVERT — an auto-moved profile reverts to the fresh-user seed on Reset.
    public void Reset_RevertsAutoMovedProfile_ToSeed()
    {
        var e = Engine(new FakeLog());
        e.SetHabituationProfile(HabituationProfile.Adapting, "calibration_demo");
        e.ApplyGateOutcome(Req(e, ProfileMove.Up, HabituationProfile.Resilient, Hold)); // → Resilient / auto_elasticity
        Assert.Equal(HabituationProfile.Resilient, e.HabituationProfile);

        e.ResetPersonalization();
        Assert.Equal(HabituationProfile.Adapting, e.HabituationProfile);                 // reverted to seed
    }

    [Fact] // RESET_EXPLICIT_UNTOUCHED — an explicit user pick survives a personalization Reset.
    public void Reset_LeavesExplicitProfileUntouched()
    {
        var e = Engine(new FakeLog());
        e.SetHabituationProfile(HabituationProfile.Reactive, "user_dashboard");
        e.ResetPersonalization();
        Assert.Equal(HabituationProfile.Reactive, e.HabituationProfile);                 // untouched
    }

    [Fact] // PERSIST_ROUNDTRIP — an auto move persists + reloads to the same member (Stage 4a new strings).
    public void Commit_AutoMove_PersistsAndReloads()
    {
        var store = new InMemoryProfileStore();
        var e1 = Engine(new FakeLog(), store: store);
        e1.SetHabituationProfile(HabituationProfile.Adapting, "calibration_demo");
        e1.ApplyGateOutcome(Req(e1, ProfileMove.Up, HabituationProfile.Resilient, Hold));
        Assert.NotNull(store.Stored);
        Assert.Equal("RESILIENT", store.Stored!.Profile);

        var e2 = new MockInferenceEngine(profileStore: store);
        Assert.Equal(HabituationProfile.Resilient, e2.HabituationProfile);
    }

    // ════════════════════════════════ SHADER UNTOUCHED (F6) ════════════════════════════════

    [Fact] // SHADER_UNTOUCHED_ON_AUTO — an auto move does NOT re-seed the shader (explicit switch does).
    public void Commit_AutoMove_DoesNotReSeedShader()
    {
        var e = Engine(new FakeLog());
        e.SetHabituationProfile(HabituationProfile.Adapting, "calibration_demo"); // neutral shader (0.74)
        double maskBefore = e.Current.Shader.MaskDepth;

        e.ApplyGateOutcome(Req(e, ProfileMove.Down, HabituationProfile.Reactive, Hold)); // Reactive's seed is 0.80 IF re-seeded
        Assert.Equal(maskBefore, e.Current.Shader.MaskDepth, 6);                          // unchanged — NOT re-seeded
    }
}
