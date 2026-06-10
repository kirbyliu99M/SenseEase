using System;
using System.Collections.Generic;
using SenseEase.App.Configuration;
using SenseEase.App.Views;
using SenseEase.Core.Abstractions;
using SenseEase.Core.AdaptiveFit;
using SenseEase.Core.Configuration;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Alpha-1 P5 (propose-confirm) — the AdaptiveFit selector's CROSS-SEGMENT path.
/// Where Group D HELD a cross-segment / upward (more-protection) target, P5 instead
/// QUEUES it as a pending proposal (the engine never opens a window). The user
/// surfaces it (App-side, behind a userInitiated card-open), and Accept applies it,
/// Decline/expiry/manual-nudge wire the previously-dead state-machine hooks.
///
/// <para>Mirrors the engine-driving helpers in <see cref="AdaptiveFitWiringTests"/>
/// (SeedDurable, DriveCalm, the AppConfig flag save/restore in try/finally, the
/// [Collection("AdaptiveFitSelectorFlag")] serialization). The cross-segment seed is
/// the SAME high-pressure (~170) window that test favours the Band-up (6,2)
/// cross-segment target with.</para>
/// </summary>
[Collection("AdaptiveFitSelectorFlag")]
public class AdaptiveFitProposeConfirmTests
{
    private static readonly DateTime ClockBase = new(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc);

    private const int    DriveTicks    = 70;
    private const double TickSeconds   = 2.0;
    private const int    SeededSampleN = 24; // > SelectorMinSamplesBeforeAnyMove (12)

    /// <summary>Seed durable entries with a fixed Pressure inside the 7-day window so
    /// IntensityStatsBuilder distils them into the intended stats shape.</summary>
    private static void SeedDurable(InMemoryEventLogStore log, double pressure, bool spike)
    {
        for (int i = 0; i < SeededSampleN; i++)
        {
            log.Append(new EventLogEntry
            {
                Timestamp = ClockBase.AddMinutes(-(i + 1) * 3),
                Category  = spike ? "AdaptiveFit.Spike" : "AdaptiveFit.Sample",
                Message   = "seeded",
                Pressure  = pressure,
            });
        }
    }

    /// <summary>Drive calm Update() ticks with deterministic 2 s timestamps.</summary>
    private static void DriveCalm(MockInferenceEngine engine, double ofi = 0.0)
    {
        for (int i = 0; i < DriveTicks; i++)
            engine.Update(ofi, "Passenger", ClockBase.AddSeconds(i * TickSeconds));
    }

    /// <summary>True iff the engine's active set still resolves, rung-for-rung, to the
    /// pinned Set0 curve (== today's VimsLevels) — i.e. NO set move took effect.</summary>
    private static bool ResolvesToSet0(MockInferenceEngine engine)
    {
        for (int level = 1; level <= SetCurve.LevelCount; level++)
            if (engine.ResolveLevel(level).PeakMaskAlpha != OverlayWindow.VimsLevels[level - 1].Alpha)
                return false;
        return true;
    }

    /// <summary>True iff the engine resolves, rung-for-rung, to the curve of <paramref name="coord"/>.</summary>
    private static bool ResolvesToSet(MockInferenceEngine engine, GridCoord coord)
    {
        SetCurve expected = SetGrid.Curve(coord);
        for (int level = 1; level <= SetCurve.LevelCount; level++)
            if (engine.ResolveLevel(level).PeakMaskAlpha != expected.ForLevel(level).PeakMaskAlpha)
                return false;
        return true;
    }

    /// <summary>An in-memory habituation store that hands back a fixed earned offset at
    /// load, so a test can prove ConfirmPendingSetMove preserves it (Q4.5).</summary>
    private sealed class SeededHabituationStore : IHabituationStore
    {
        private readonly double _offset;
        public SeededHabituationStore(double offset) => _offset = offset;
        public HabituationState? Load() => new() { Offset = _offset };
        public void Save(HabituationState state) { /* no-op */ }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 1. CROSS-SEGMENT / UPWARD ⇒ QUEUES a proposal, does NOT move _activeSet
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FlagOn_CrossSegmentUpward_QueuesPendingProposal_WithoutMovingTheSet()
    {
        bool original = AppConfig.AdaptiveFitSelectorEnabled;
        try
        {
            AppConfig.AdaptiveFitSelectorEnabled = true;

            // SAME seed as FlagOn_CrossSegmentFavouringLog_HoldsTheSet: high pressure
            // favours the Band-UP neighbour (6,2), a cross-segment (felt-intensity) target.
            var log = new InMemoryEventLogStore();
            SeedDurable(log, pressure: 170.0, spike: false);
            var engine = new MockInferenceEngine(null, null, null, log) { AdaptiveFitSelectorEnabled = true };

            Assert.True(ResolvesToSet0(engine)); // pre-condition: pinned at Set0
            DriveCalm(engine);

            // The set did NOT move — the cross-segment target was QUEUED, not applied.
            Assert.True(ResolvesToSet0(engine), "a cross-segment target must NOT move the active set");

            // A pending proposal is queued for the Band-up (6,2) target.
            var pending = engine.PeekPendingSetMove();
            Assert.NotNull(pending);
            Assert.Equal(6, pending!.Value.Band);
            Assert.Equal(2, pending.Value.Shape);
            // Nominal level for Band 6 = round(1 + 6*11/9) = round(8.33) = 8.
            Assert.Equal(8, pending.Value.TargetLevel);
            // The TTL is ~24h out from the (clock-driven) eval time.
            Assert.True(pending.Value.ExpiresAtUtc > ClockBase);
        }
        finally { AppConfig.AdaptiveFitSelectorEnabled = original; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 2. CONFIRM applies the move + PRESERVES the earned offset (Q4.5)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConfirmPendingSetMove_AppliesTheTarget_AndPreservesTheHabituationOffset()
    {
        bool original = AppConfig.AdaptiveFitSelectorEnabled;
        try
        {
            AppConfig.AdaptiveFitSelectorEnabled = true;

            var log = new InMemoryEventLogStore();
            SeedDurable(log, pressure: 170.0, spike: false);
            // Seed a real earned habituation offset so we can prove Confirm doesn't zero it.
            var habStore = new SeededHabituationStore(offset: 5.0);
            var engine = new MockInferenceEngine(null, habStore, null, log) { AdaptiveFitSelectorEnabled = true };

            int offsetBefore = engine.HabituationOffsetPp;
            Assert.Equal(5, offsetBefore); // restored from the seeded store

            DriveCalm(engine);
            Assert.NotNull(engine.PeekPendingSetMove());

            bool confirmed = engine.ConfirmPendingSetMove();
            Assert.True(confirmed);

            // The move applied: the engine now resolves to the (6,2) curve, not Set0.
            var target = new GridCoord(6, 2);
            Assert.True(ResolvesToSet(engine, target), "confirm should apply the (6,2) target");
            Assert.False(ResolvesToSet0(engine));

            // Pending cleared after confirm.
            Assert.Null(engine.PeekPendingSetMove());

            // Q4.5: the earned habituation offset is UNCHANGED (never zeroed).
            Assert.Equal(offsetBefore, engine.HabituationOffsetPp);
        }
        finally { AppConfig.AdaptiveFitSelectorEnabled = original; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 3. DECLINE backs the selector off + clears + does not immediately re-confirm
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DeclinePendingSetMove_ClearsPending_AndBacksTheSelectorOff()
    {
        bool original = AppConfig.AdaptiveFitSelectorEnabled;
        try
        {
            AppConfig.AdaptiveFitSelectorEnabled = true;

            var log = new InMemoryEventLogStore();
            SeedDurable(log, pressure: 170.0, spike: false);
            var engine = new MockInferenceEngine(null, null, null, log) { AdaptiveFitSelectorEnabled = true };

            DriveCalm(engine);
            Assert.NotNull(engine.PeekPendingSetMove());

            engine.DeclinePendingSetMove();

            // Cleared immediately.
            Assert.Null(engine.PeekPendingSetMove());

            // OnProposalDeclined doubled the cooldown: a subsequent run of the SAME
            // amount of driven time must NOT immediately re-queue a confident proposal
            // (the selector is backed off harder). Drive again and assert no re-queue
            // lands within this window, AND the set is still unmoved.
            DriveCalm(engine);
            Assert.Null(engine.PeekPendingSetMove());
            Assert.True(ResolvesToSet0(engine));
        }
        finally { AppConfig.AdaptiveFitSelectorEnabled = original; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 4. A MANUAL FOV feedback (flag ON) clears a queued pending move
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ApplyUserFeedback_WithFovDelta_ClearsAQueuedPendingMove()
    {
        bool original = AppConfig.AdaptiveFitSelectorEnabled;
        try
        {
            AppConfig.AdaptiveFitSelectorEnabled = true;

            var log = new InMemoryEventLogStore();
            SeedDurable(log, pressure: 170.0, spike: false);
            var engine = new MockInferenceEngine(null, null, null, log) { AdaptiveFitSelectorEnabled = true };

            DriveCalm(engine);
            Assert.NotNull(engine.PeekPendingSetMove());

            // A manual FOV nudge (the EaseView / chatbot FOV-delta path) retracts it.
            engine.ApplyUserFeedback(-5);

            Assert.Null(engine.PeekPendingSetMove());
        }
        finally { AppConfig.AdaptiveFitSelectorEnabled = original; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 5. VESTIBULAR — a propose-WEAKER cross-segment target is never queued
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FlagOn_Vestibular_NeverQueuesAWeakerBandProposal()
    {
        bool original = AppConfig.AdaptiveFitSelectorEnabled;
        try
        {
            AppConfig.AdaptiveFitSelectorEnabled = true;

            // A calm window favours the Band-DOWN (weaker) neighbour. For a NON-advisory
            // user (4,2) is same-segment so it would move silently; the point here is the
            // vestibular guard — propose-stronger-only — must DROP any weaker-band target
            // before it can be queued OR moved. The advisory user gets neither a move nor
            // a pending.
            var log = new InMemoryEventLogStore();
            SeedDurable(log, pressure: 5.0, spike: false);
            var engine = new MockInferenceEngine(null, null, null, log) { AdaptiveFitSelectorEnabled = true };
            engine.SetVestibularProtection(true);

            DriveCalm(engine);

            Assert.Null(engine.PeekPendingSetMove());
            Assert.True(ResolvesToSet0(engine));
        }
        finally { AppConfig.AdaptiveFitSelectorEnabled = original; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 6. IDEMPOTENCY — Confirm/Decline are safe no-ops when nothing is pending
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConfirmAndDecline_AreIdempotent_WhenNoPending()
    {
        bool original = AppConfig.AdaptiveFitSelectorEnabled;
        try
        {
            AppConfig.AdaptiveFitSelectorEnabled = true;
            var engine = new MockInferenceEngine { AdaptiveFitSelectorEnabled = true };

            Assert.Null(engine.PeekPendingSetMove());
            Assert.False(engine.ConfirmPendingSetMove()); // returns false, no throw
            engine.DeclinePendingSetMove();               // no-op, no throw
            engine.ClearPendingSetMove();                 // no-op, no throw
            Assert.Null(engine.PeekPendingSetMove());

            // The set was never touched by any of the no-ops.
            Assert.True(ResolvesToSet0(engine));
        }
        finally { AppConfig.AdaptiveFitSelectorEnabled = original; }
    }
}
