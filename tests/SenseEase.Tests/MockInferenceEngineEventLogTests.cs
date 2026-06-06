using System.Linq;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// M1 (Phase 6-pre): contract tests for habituation lifecycle event-log
/// writes. The chatbot's Phase 6c trajectory builder will summarise the
/// user's habituation arc by grepping <c>Habituation.*</c> category
/// entries — these tests pin the contract that the engine wrapper
/// actually emits them when state transitions fire.
///
/// <para>Tests exercise the public engine API (no reflection / no internal
/// access). All emission paths go through <c>MaybePersistProfileSnapshot</c>
/// which is invoked by <c>ApplyUserFeedback</c>, <c>Update</c>, etc.</para>
/// </summary>
public class MockInferenceEngineEventLogTests
{
    /// <summary>A wired profile store whose Load() always returns null — the
    /// fail-open shape WAVE W2.0-E targets (missing / corrupt / refused
    /// schema-downgrade all surface as a null snapshot at the engine boundary).
    /// Non-null itself, so the engine knows a store WAS supplied.</summary>
    private sealed class NullLoadProfileStore : IHabituationProfileStore
    {
        public HabituationProfileSnapshot? Load() => null;
        public void Save(HabituationProfileSnapshot snapshot) { /* no-op */ }
    }

    [Fact]
    public void NullProfileLoad_WithWiredStore_EmitsRecoveryRecord()
    {
        // WAVE W2.0-E: when a profile store IS wired but Load() returns null,
        // the engine silently reverts to RESILIENT + source='default', losing
        // any calibrated state. That silent loss must leave one durable,
        // non-blocking audit record on the event-log sink so it is traceable
        // after the fact. No UI alarm — purely a diagnostics breadcrumb.
        var eventLog = new InMemoryEventLogStore();
        var engine = new MockInferenceEngine(
            adjustmentLog: null,
            habituationStore: null,
            profileStore: new NullLoadProfileStore(),
            eventLog: eventLog);

        var recovery = eventLog.Recent()
            .Where(e => e.Category == "Recovery.ProfileLoadNull")
            .ToList();
        var rec = Assert.Single(recovery);
        // The record names the fallback the engine silently took so the loss is
        // self-describing without cross-referencing other state.
        Assert.Contains("RESILIENT", rec.Message);
        Assert.Contains("default", rec.Message);
    }

    [Fact]
    public void NullProfileStore_EmitsNoRecoveryRecord()
    {
        // Symmetric guard: when NO profile store is wired at all (headless /
        // unit-test contexts), there is no calibration state to lose, so the
        // recovery record must NOT fire — it would be noise, not a signal.
        var eventLog = new InMemoryEventLogStore();
        var engine = new MockInferenceEngine(
            adjustmentLog: null,
            habituationStore: null,
            profileStore: null,
            eventLog: eventLog);

        Assert.DoesNotContain(eventLog.Recent(),
            e => e.Category == "Recovery.ProfileLoadNull");
    }

    [Fact]
    public void ApplyUserFeedback_SevereDelta_EmitsSpikeAndStateChange()
    {
        var eventLog = new InMemoryEventLogStore();
        // Null adjustment/habituation/profile stores — only the event log
        // is the unit under test. Engine still constructs + functions.
        var engine = new MockInferenceEngine(null, null, null, eventLog);

        // -50 exceeds HabituationSevereFovThreshold (-30 typical) so this
        // is classified as a chatbot-severe report → RegisterSevereStrike
        // → spike timestamp + strike count increment + EnterPause →
        // EngineState transitions Running → Cooldown.
        engine.ApplyUserFeedback(-50);

        var events = eventLog.Recent();
        Assert.NotEmpty(events);
        Assert.Contains(events, e => e.Category == "Habituation.Spike");
        Assert.Contains(events, e => e.Category == "Habituation.StateChange");
    }

    [Fact]
    public void ApplyUserFeedback_SpikeEventCapturesStateSnapshot()
    {
        var eventLog = new InMemoryEventLogStore();
        var engine = new MockInferenceEngine(null, null, null, eventLog);

        engine.ApplyUserFeedback(-50);

        var spike = eventLog.Recent().First(e => e.Category == "Habituation.Spike");
        // The event-log entry should carry the engine's current state at
        // write-time so the chatbot can reason about sensor context around
        // the event. Pressure starts at 0 in tests (no OFI samples fed).
        Assert.Equal(0.0, spike.Pressure);
        Assert.NotNull(spike.MaskSnapshot);
        Assert.Contains("strike_count", spike.Message);
    }

    [Fact]
    public void ApplyUserFeedback_NonSevereDelta_DoesNotEmitSpike()
    {
        var eventLog = new InMemoryEventLogStore();
        var engine = new MockInferenceEngine(null, null, null, eventLog);

        // -5 is well below the severe threshold — RegisterSevereStrike
        // is NOT called, _lastSpikeAtUtc unchanged, so no Habituation.Spike
        // event. Mild back-off + EnterPause still fires though, so we may
        // see a StateChange event from the cooldown transition.
        engine.ApplyUserFeedback(-5);

        var events = eventLog.Recent();
        Assert.DoesNotContain(events, e => e.Category == "Habituation.Spike");
    }

    [Fact]
    public void NullEventLog_DoesNotCrash()
    {
        // Defensive: the eventLog parameter is optional. Engine must
        // construct + accept ApplyUserFeedback without throwing when
        // no event log is wired (test contexts, headless scenarios).
        var engine = new MockInferenceEngine(null, null, null, eventLog: null);
        var ex = Record.Exception(() => engine.ApplyUserFeedback(-50));
        Assert.Null(ex);
    }

    [Fact]
    public void HabituationEvents_UseCorrectCategoryPrefix()
    {
        // The Phase 6c trajectory builder will grep on the "Habituation."
        // prefix. Pin the prefix here so a future refactor that renames
        // (e.g. "habituation_*") gets caught.
        var eventLog = new InMemoryEventLogStore();
        var engine = new MockInferenceEngine(null, null, null, eventLog);
        engine.ApplyUserFeedback(-50);

        var habEvents = eventLog.Recent()
                                .Where(e => e.Category.StartsWith("Habituation."))
                                .ToList();
        Assert.NotEmpty(habEvents);
        foreach (var e in habEvents)
        {
            Assert.StartsWith("Habituation.", e.Category);
            // Confirm it's one of the documented categories from the
            // WriteHabituationLifecycleEvents helper.
            Assert.Contains(e.Category, new[]
            {
                "Habituation.Profile",
                "Habituation.LevelChanged",
                "Habituation.Spike",
                "Habituation.StateChange",
            });
        }
    }
}
