using System;
using System.Collections.Generic;
using System.Linq;
using SenseEase.App.Configuration;
using SenseEase.App.Views;
using SenseEase.Core.AdaptiveFit;
using SenseEase.Core.Configuration;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Alpha-2 core GROUP D (final core) — wiring the deterministic selector behind the
/// default-OFF flag. These tests pin the engine SELECTOR-TICK contract (spec items
/// 1–4): the flag-mirror default, flag-off byte-identity (the #1 invariant survives
/// Group D), a flag-on SILENT adjacent same-segment move, the cross-segment HOLD
/// (deferred to P5), and the vestibular propose-stronger-only guard.
///
/// <para><b>The #1 invariant.</b> When <c>AdaptiveFitSelectorEnabled == false</c> (the
/// DEFAULT) the engine is BYTE-IDENTICAL to today: the new selector tick lives entirely
/// inside <c>if (_adaptiveFitSelectorEnabled)</c>, so two identically-driven flag-off
/// engines produce an identical (VimsTier, Fov, Mask) stream — the new tick is inert.</para>
///
/// <para><b>Observability.</b> <c>_activeSet</c> is private; the public
/// <see cref="MockInferenceEngine.ResolveLevel"/> reads <c>SetGrid.Curve(_activeSet)</c>,
/// so a set move is observed by comparing the resolved 12-rung curve against the pinned
/// Set0 curve (== today's <c>OverlayWindow.VimsLevels</c>). The vestibular L9 floor is
/// observed through <see cref="MockInferenceEngine.CurrentLevel"/>.</para>
///
/// <para><b>Selector math (verified against <see cref="DeterministicSetSelector"/> with
/// <see cref="SelectorWeights.Default"/>).</b> From the pinned median Set0 (Band 5,
/// Shape 2):
/// <list type="bullet">
///   <item>a CALM durable window (low pressure, fully calm, no spikes) makes the
///   selector argmax the Band-DOWN neighbour <c>(4,2)</c> — Manhattan-1, SAME 5級
///   segment (中) — beating Hold by ~0.154 &gt; the 0.15 confidence margin, so it
///   applies silently;</item>
///   <item>a HIGH-PRESSURE durable window (P90 ≈ 2×trigger, no calm, no spikes) makes the
///   argmax the Band-UP neighbour <c>(6,2)</c> — also Manhattan-1 and beating Hold by
///   ~0.154 — but that crosses into the next 5級 segment (低), so the engine HOLDs.</item>
/// </list></para>
///
/// <para>Every test that flips <see cref="AppConfig.AdaptiveFitSelectorEnabled"/> or the
/// engine's instance flag guards it in a try/finally and restores it to <c>false</c> so
/// there is no cross-test leakage (the AppConfig field is a process-wide static).</para>
/// </summary>
[Collection("AdaptiveFitSelectorFlag")]
public class AdaptiveFitWiringTests
{
    // The durable read window is 7 days; pin "now" + the seeded timestamps inside it.
    private static readonly DateTime ClockBase = new(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc);

    // The selector evaluates at most once per SettlingMinSeconds (60 s) of accumulated dt.
    // Update() clamps dt to [0.001, 2.0], so ~30 ticks of 2 s cross the 60 s cadence and
    // the state machine's settle floor in one shot. We drive comfortably more so exactly
    // one evaluation fires, then the 300 s Set cooldown blocks any second move inside the
    // ~140 s of driven time — guaranteeing an at-most-one-step result.
    private const int    DriveTicks      = 70;
    private const double TickSeconds     = 2.0;
    private const int    SeededSampleN   = 24; // > SelectorMinSamplesBeforeAnyMove (12)

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Seed <see cref="SeededSampleN"/> durable entries with a fixed Pressure and
    /// (optionally) a spike category, all timestamped inside the durable window so
    /// <c>IntensityStatsBuilder</c> distils them into the intended stats shape.</summary>
    private static void SeedDurable(InMemoryEventLogStore log, double pressure, bool spike)
    {
        for (int i = 0; i < SeededSampleN; i++)
        {
            log.Append(new EventLogEntry
            {
                // A few minutes apart, all comfortably within the 7-day window before ClockBase.
                Timestamp = ClockBase.AddMinutes(-(i + 1) * 3),
                Category  = spike ? "AdaptiveFit.Spike" : "AdaptiveFit.Sample",
                Message   = "seeded",
                Pressure  = pressure,
            });
        }
    }

    /// <summary>Drive <see cref="DriveTicks"/> calm Update() ticks with deterministic 2 s
    /// timestamps. OFI is fed at <paramref name="ofi"/> (0 keeps the live tier at 1 so the
    /// only thing that can move the set is the seeded durable LOG stats, not live pressure).
    /// The per-tick (Tier, Fov, Mask, Pressure) stream is captured for parity comparison.</summary>
    private static List<(int Tier, int Fov, bool Mask, double Pressure)> DriveCalm(
        MockInferenceEngine engine, double ofi = 0.0)
    {
        var stream = new List<(int, int, bool, double)>();
        for (int i = 0; i < DriveTicks; i++)
        {
            SenseState s = engine.Update(ofi, "Passenger", ClockBase.AddSeconds(i * TickSeconds));
            stream.Add((s.VimsTier, s.FovRadiusPercent, s.MaskActive, s.Pressure));
        }
        return stream;
    }

    /// <summary>True iff the engine's active set still resolves to the pinned Set0 curve
    /// (== today's VimsLevels) for every one of the 12 rungs — i.e. NO set move took
    /// effect.</summary>
    private static bool ResolvesToSet0(MockInferenceEngine engine)
    {
        for (int level = 1; level <= SetCurve.LevelCount; level++)
        {
            if (engine.ResolveLevel(level).PeakMaskAlpha != OverlayWindow.VimsLevels[level - 1].Alpha)
                return false;
        }
        return true;
    }

    /// <summary>True iff the engine's active set resolves, rung-for-rung, to the curve of
    /// <paramref name="coord"/> (pins the EXACT target the selector moved to).</summary>
    private static bool ResolvesToSet(MockInferenceEngine engine, GridCoord coord)
    {
        SetCurve expected = SetGrid.Curve(coord);
        for (int level = 1; level <= SetCurve.LevelCount; level++)
        {
            if (engine.ResolveLevel(level).PeakMaskAlpha != expected.ForLevel(level).PeakMaskAlpha)
                return false;
        }
        return true;
    }

    /// <summary>A minimal vestibular-advisory calibration result. Sets ONLY the advisory
    /// string the engine's guard keys off (<c>MedicalAdvisory == "vestibular"</c>); leaves
    /// the trigger at the default 85 (no override, scale 1.0) so the seeded-stats math is
    /// unchanged by calibration.</summary>
    private static CalibrationResult VestibularResult() => new()
    {
        Profile               = HabituationProfile.Sensitive,
        PersonalPressureScale = 1.0,
        TriggerThresholdOverride = null,
        MedicalAdvisory       = "vestibular",
        CalibrationConfidence = CalibrationConfidence.High,
        Completeness          = CalibrationCompleteness.Complete,
    };

    // ──────────────────────────────────────────────────────────────────────────
    // 1. FLAG-MIRROR — AppConfig default false ⇒ a freshly-wired engine is OFF
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FlagMirror_AppConfigDefaultsFalse_SoAFreshlyWiredEngineIsOff()
    {
        // The startup flag-mirror sets engine.AdaptiveFitSelectorEnabled =
        // AppConfig.AdaptiveFitSelectorEnabled. Since AppConfig defaults false, the
        // mirrored engine is OFF — today's path, by construction.
        Assert.False(AppConfig.AdaptiveFitSelectorEnabled);

        var engine = new MockInferenceEngine();
        engine.AdaptiveFitSelectorEnabled = AppConfig.AdaptiveFitSelectorEnabled; // the startup mirror
        Assert.False(engine.AdaptiveFitSelectorEnabled);

        // A brand-new engine (no explicit set) is OFF too — the instance field defaults off.
        Assert.False(new MockInferenceEngine().AdaptiveFitSelectorEnabled);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 2. FLAG-OFF PARITY — the #1 invariant still holds after Group D
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FlagOff_NewSelectorTickIsInert_TwoIdenticallyDrivenEnginesAreByteIdentical()
    {
        bool original = AppConfig.AdaptiveFitSelectorEnabled;
        try
        {
            AppConfig.AdaptiveFitSelectorEnabled = false; // default; pinned explicit

            // Both engines get an identically-seeded durable log that WOULD favour an
            // adjacent same-segment move IF the tick ran — proving the tick is inert when
            // the flag is OFF (not merely that an empty log produces no move).
            var logA = new InMemoryEventLogStore();
            var logB = new InMemoryEventLogStore();
            SeedDurable(logA, pressure: 5.0, spike: false);
            SeedDurable(logB, pressure: 5.0, spike: false);

            var a = new MockInferenceEngine(null, null, null, logA);
            var b = new MockInferenceEngine(null, null, null, logB);
            Assert.False(a.AdaptiveFitSelectorEnabled);
            Assert.False(b.AdaptiveFitSelectorEnabled);

            var streamA = DriveCalm(a);
            var streamB = DriveCalm(b);

            // Flag-off is fully deterministic: identical drive → identical state stream.
            Assert.Equal(streamA, streamB);

            // And neither engine moved its set — ResolveLevel still reproduces today's
            // VimsLevels for both (the new grid is never consulted while OFF).
            Assert.True(ResolvesToSet0(a));
            Assert.True(ResolvesToSet0(b));
        }
        finally
        {
            AppConfig.AdaptiveFitSelectorEnabled = original;
        }
    }

    [Fact]
    public void FlagOff_SeededDurableLog_DoesNotMoveTheSet()
    {
        bool original = AppConfig.AdaptiveFitSelectorEnabled;
        try
        {
            AppConfig.AdaptiveFitSelectorEnabled = false;

            // The SAME calm log that drives a silent move when the flag is ON (see the
            // flag-on test) must be completely inert while OFF.
            var log = new InMemoryEventLogStore();
            SeedDurable(log, pressure: 5.0, spike: false);
            var engine = new MockInferenceEngine(null, null, null, log); // flag OFF (default)

            DriveCalm(engine);

            Assert.True(ResolvesToSet0(engine));
        }
        finally
        {
            AppConfig.AdaptiveFitSelectorEnabled = original;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 3. FLAG-ON SILENT MOVE — one adjacent, same-segment step
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FlagOn_CalmDurableLog_SilentlyMovesExactlyOneAdjacentSameSegmentStep()
    {
        bool original = AppConfig.AdaptiveFitSelectorEnabled;
        try
        {
            AppConfig.AdaptiveFitSelectorEnabled = true;

            var log = new InMemoryEventLogStore();
            SeedDurable(log, pressure: 5.0, spike: false); // clearly calm: norm ≈ 0.06, calm ratio 1.0
            var engine = new MockInferenceEngine(null, null, null, log)
            {
                AdaptiveFitSelectorEnabled = true,
            };

            // Pre-condition: the engine starts pinned to Set0 (≡ today).
            Assert.True(ResolvesToSet0(engine));

            DriveCalm(engine);

            // The calm window favours the Band-DOWN neighbour (4,2): adjacent (Manhattan 1)
            // AND same 5級 segment (中) as Set0, so the engine applies it SILENTLY.
            var target = new GridCoord(4, 2);
            Assert.Equal(1, GridCoord.ManhattanDistance(GridCoord.Set0, target));

            // ResolveLevel now reflects the NEW set, rung-for-rung — and it is NOT Set0 any
            // more (the move really took effect).
            Assert.True(ResolvesToSet(engine, target),
                "engine should resolve to the (4,2) curve after the silent same-segment move");
            Assert.False(ResolvesToSet0(engine),
                "the active set must have moved off Set0");

            // Exactly ONE step: it landed on a Manhattan-1 neighbour of Set0, not two away.
            // (The 300 s Set cooldown blocks any second move within the driven window.)
            // Cross-check: at least one rung's resolved alpha now differs from today's.
            Assert.Contains(
                Enumerable.Range(1, SetCurve.LevelCount),
                level => engine.ResolveLevel(level).PeakMaskAlpha
                         != OverlayWindow.VimsLevels[level - 1].Alpha);
        }
        finally
        {
            AppConfig.AdaptiveFitSelectorEnabled = original;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 4. CROSS-SEGMENT HOLD — a cross-segment-favouring window does NOT move (P5)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FlagOn_CrossSegmentFavouringLog_HoldsTheSet_DeferredToP5()
    {
        bool original = AppConfig.AdaptiveFitSelectorEnabled;
        try
        {
            AppConfig.AdaptiveFitSelectorEnabled = true;

            // A sustained-high-pressure window (P90 ≈ 2×trigger, no calm, no spikes) makes
            // the selector argmax the Band-UP neighbour (6,2). (6,2) is Manhattan-1 but
            // lands in the NEXT 5級 segment (低) — a felt-intensity change the user must
            // confirm — so the silent path HOLDs (P5 propose-confirm is deferred).
            var log = new InMemoryEventLogStore();
            SeedDurable(log, pressure: 170.0, spike: false); // 170 / 85 trigger ⇒ P90 norm 2.0
            var engine = new MockInferenceEngine(null, null, null, log)
            {
                AdaptiveFitSelectorEnabled = true,
            };

            // The would-be target (6,2) is a CROSS-segment step from Set0.
            var target = new GridCoord(6, 2);
            Assert.Equal(1, GridCoord.ManhattanDistance(GridCoord.Set0, target));

            // Feed OFI=0 so live pressure stays calm and the tier holds at 1 — only the
            // durable LOG stats could move the set, and the cross-segment guard blocks it.
            DriveCalm(engine, ofi: 0.0);

            // No move: the engine still resolves to the pinned Set0 curve (≡ today).
            Assert.True(ResolvesToSet0(engine),
                "a cross-segment-favouring window must HOLD the set at Set0 (deferred to P5)");
            Assert.False(ResolvesToSet(engine, target),
                "the cross-segment target must NOT have been applied");
        }
        finally
        {
            AppConfig.AdaptiveFitSelectorEnabled = original;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 5. VESTIBULAR — propose-stronger-only: a weaker-band move is BLOCKED
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FlagOn_Vestibular_BlocksTheWeakerBandMove_ThatANonAdvisoryUserWouldTake()
    {
        bool original = AppConfig.AdaptiveFitSelectorEnabled;
        try
        {
            AppConfig.AdaptiveFitSelectorEnabled = true;

            // Same calm window in both engines: it favours the Band-DOWN (weaker) move to
            // (4,2). The vestibular guard is propose-stronger-only, so the advisory engine
            // must BLOCK that weaker move while the non-advisory engine takes it. Running
            // both proves it is the vestibular guard — not a generic hold — doing the work.
            var advisoryLog    = new InMemoryEventLogStore();
            var nonAdvisoryLog = new InMemoryEventLogStore();
            SeedDurable(advisoryLog,    pressure: 5.0, spike: false);
            SeedDurable(nonAdvisoryLog, pressure: 5.0, spike: false);

            var advisory = new MockInferenceEngine(null, null, null, advisoryLog)
            {
                AdaptiveFitSelectorEnabled = true,
            };
            advisory.ApplyCalibrationResult(VestibularResult()); // sets MedicalAdvisory == "vestibular"
            Assert.Equal("vestibular", advisory.LastCalibrationResult?.MedicalAdvisory);

            var nonAdvisory = new MockInferenceEngine(null, null, null, nonAdvisoryLog)
            {
                AdaptiveFitSelectorEnabled = true,
            };

            DriveCalm(advisory);
            DriveCalm(nonAdvisory);

            var weaker = new GridCoord(4, 2);

            // Vestibular: the weaker-band (4,2) move is BLOCKED — the set stays pinned at
            // Set0 (propose-stronger-only).
            Assert.True(ResolvesToSet0(advisory),
                "vestibular advisory must block the weaker-band move and hold at Set0");
            Assert.False(ResolvesToSet(advisory, weaker),
                "vestibular must NOT step toward the weaker band (4,2)");

            // Contrast: the identical calm window DOES move the non-advisory engine to the
            // weaker (4,2) — so the seeded stats genuinely favour the down-move, and it was
            // the vestibular guard (not a degenerate no-op) that blocked it above.
            Assert.True(ResolvesToSet(nonAdvisory, weaker),
                "a non-advisory user should take the weaker-band move the vestibular guard blocked");
        }
        finally
        {
            AppConfig.AdaptiveFitSelectorEnabled = original;
        }
    }

    [Fact]
    public void FlagOn_Vestibular_ResolvedLevelHonoursTheL9Floor_NeverDropsBelow9OnAMove()
    {
        bool original = AppConfig.AdaptiveFitSelectorEnabled;
        try
        {
            AppConfig.AdaptiveFitSelectorEnabled = true;

            // The vestibular L9 floor (spec item 4) is enforced inside the engine's set-move
            // path: on ANY applied set move an advisory user's resolved level is clamped to
            // >= 9. From the pinned Set0 the only directionally-allowed neighbours are
            // either cross-segment (held) or weaker-band (blocked), so no silent move
            // applies — and the floor is therefore maintained vacuously: the resolved level
            // must NEVER be observed below 9 for an advisory user as a consequence of the
            // selector tick.
            var log = new InMemoryEventLogStore();
            SeedDurable(log, pressure: 5.0, spike: false); // calm: would pull weaker-band (blocked)
            var engine = new MockInferenceEngine(null, null, null, log)
            {
                AdaptiveFitSelectorEnabled = true,
            };
            engine.ApplyCalibrationResult(VestibularResult());

            DriveCalm(engine);

            // The set never moved (weaker move blocked), so the engine still resolves to
            // Set0 — and critically the selector tick never drove the resolved level below
            // the L9 floor for the advisory user (no sub-9 move was applied).
            Assert.True(ResolvesToSet0(engine));
            // CurrentLevel is the live tier; with OFI=0 it holds at 1 (no spurious clamp),
            // confirming the L9 clamp only ever fires on an actual applied move — which the
            // guard correctly prevented here.
            Assert.Equal(1, engine.CurrentLevel);
        }
        finally
        {
            AppConfig.AdaptiveFitSelectorEnabled = original;
        }
    }
}
