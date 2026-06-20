using System;
using System.Collections.Generic;
using System.Linq;
using SenseEase.App.Configuration;
using SenseEase.App.Views;
using SenseEase.Core.AdaptiveFit;
using SenseEase.Core.Configuration;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Alpha-2 core GROUP D (final core) — wiring the deterministic selector behind the
/// AppConfig flag (now default-ON, mirrored onto the engine at startup). These tests pin
/// the engine SELECTOR-TICK contract (spec items 1–4): the flag-mirror default, flag-off
/// byte-identity (the #1 invariant survives Group D), a flag-on SILENT adjacent same-segment
/// move, the cross-segment HOLD (deferred to P5), and the vestibular propose-stronger-only
/// guard. Engine-only tests that do NOT mirror the flag still default OFF at the instance
/// level (an unwired engine is on today's path); the App layer is what turns it on.
///
/// <para><b>Vestibular signal.</b> The vestibular guard / L9 floor key off the live
/// <c>IsVestibularActive</c> signal. Since D-13 deleted the calibration plumbing
/// (<c>ApplyCalibrationResult</c>), these tests arm vestibular protection through the live
/// path <see cref="MockInferenceEngine.SetVestibularProtection"/> (the auto-profiling /
/// Settings toggle that sets <c>EffectiveTrigger=50</c> + <c>SevereStrikeLimitOverride=1</c>).</para>
///
/// <para><b>The #1 invariant.</b> When <c>AdaptiveFitSelectorEnabled == false</c> (an
/// unwired engine's instance default) the engine is BYTE-IDENTICAL to today: the new
/// selector tick lives entirely inside <c>if (_adaptiveFitSelectorEnabled)</c>, so two
/// identically-driven flag-off engines produce an identical (VimsTier, Fov, Mask) stream —
/// the new tick is inert.</para>
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

    /// <summary>Drive a GRADUAL OFI ramp from ~0 up to <paramref name="ofi"/> over
    /// <paramref name="ticks"/> ticks (100 ms apart), capturing the discrete VimsTier per tick —
    /// the discretizer's output. The gradual ramp is deliberate: a CONSTANT high OFI surges past
    /// the fast-onset gap and snaps the tier straight to a plateau (one distinct value, cf.
    /// VimsTier_FastOnsetBypass), whereas a slow ramp lets the tier climb ±1 through several
    /// DISTINCT tiers so the parity check below is non-vacuous. The whole ramp stays far under
    /// SettlingMinSeconds (60 s) so the selector never evaluates and the active set stays Set0.</summary>
    private static List<int> DriveTierStream(MockInferenceEngine engine, double ofi, int ticks)
    {
        var tiers = new List<int>();
        for (int i = 0; i < ticks; i++)
        {
            double ofiNow = ofi * (i + 1) / ticks;   // ~0 → ofi gradual ramp (avoids the fast-onset snap)
            tiers.Add(engine.Update(ofiNow, "Passenger", ClockBase.AddMilliseconds(i * 100)).VimsTier);
        }
        return tiers;
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

    /// <summary>Arm vestibular protection through the LIVE path (D-13: the legacy
    /// <c>ApplyCalibrationResult(MedicalAdvisory="vestibular")</c> plumbing is deleted).
    /// <see cref="MockInferenceEngine.SetVestibularProtection"/> sets the persistent
    /// in-memory signal the engine's <c>IsVestibularActive</c> reads — the SAME signal the
    /// selector's propose-stronger-only guard / L9 floor key off — plus <c>EffectiveTrigger=50</c>
    /// and <c>SevereStrikeLimitOverride=1</c>. Note the trigger is now 50 (not the old advisory
    /// helper's 85): the seeded-stats math below normalises P90 against the engine's effective
    /// trigger, so each vestibular test seeds the pressure that yields the intended norm under
    /// the 50 trigger.</summary>
    private static void ArmVestibular(MockInferenceEngine engine) =>
        engine.SetVestibularProtection(true);

    // ──────────────────────────────────────────────────────────────────────────
    // 1. FLAG-MIRROR — AppConfig defaults ON ⇒ a freshly-wired engine is ON; an
    //    UNWIRED engine (no mirror) still defaults OFF at the instance level.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FlagMirror_AppConfigDefaultsOn_SoAWiredEngineIsOn()
    {
        // ACTIVATED 2026-06-07 (Kirby): AppConfig now defaults ON, so the startup
        // flag-mirror (engine.AdaptiveFitSelectorEnabled = AppConfig.AdaptiveFitSelectorEnabled)
        // turns the wired engine ON — the selector is live in the running app.
        Assert.True(AppConfig.AdaptiveFitSelectorEnabled);

        var engine = new MockInferenceEngine();
        engine.AdaptiveFitSelectorEnabled = AppConfig.AdaptiveFitSelectorEnabled; // the startup mirror
        Assert.True(engine.AdaptiveFitSelectorEnabled);

        // A brand-new engine with NO mirror still defaults OFF at the instance level — the
        // App layer is what turns it on by mirroring AppConfig, so engine-only tests stay on
        // today's path unless they opt in explicitly.
        Assert.False(new MockInferenceEngine().AdaptiveFitSelectorEnabled);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 1b. SELECTOR → RENDERER — the active set's mask DEPTH reaches the overlay.
    //     Alpha-2 audit B1: PeakMaskAlpha was computed but ignored; the overlay
    //     rendered the static tier alpha for EVERY set. SenseState.MaskPeakAlphaOverride
    //     now carries the active set's resolved peak alpha so the selector's chosen
    //     envelope is actually visible (-1 ⇒ flag OFF ⇒ overlay's static tier alpha).
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FlagOn_MaskPeakAlphaOverride_TracksActiveSetResolvedAlpha()
    {
        bool original = AppConfig.AdaptiveFitSelectorEnabled;
        try
        {
            AppConfig.AdaptiveFitSelectorEnabled = true;
            var engine = new MockInferenceEngine { AdaptiveFitSelectorEnabled = true };

            // A few calm ticks (no seeded durable log ⇒ no set move ⇒ active set stays Set0).
            SenseState s = engine.Update(0.0, "Passenger", ClockBase);
            for (int i = 1; i < 5; i++)
                s = engine.Update(60.0, "Passenger", ClockBase.AddSeconds(i * TickSeconds));

            // The overlay-facing override carries the ACTIVE SET's resolved peak alpha for the
            // displayed tier — the wiring that makes the selector's chosen depth visible.
            Assert.True(s.MaskPeakAlphaOverride >= 0.0);
            Assert.Equal(engine.ResolveLevel(s.VimsTier).PeakMaskAlpha, s.MaskPeakAlphaOverride);
            // Set0 ≡ today ⇒ the override equals the static VimsLevels alpha (default set unchanged).
            Assert.Equal(OverlayWindow.VimsLevels[s.VimsTier - 1].Alpha, s.MaskPeakAlphaOverride);
        }
        finally { AppConfig.AdaptiveFitSelectorEnabled = original; }
    }

    [Fact]
    public void FlagOff_MaskPeakAlphaOverride_IsMinusOne_OverlayKeepsStaticTierAlpha()
    {
        bool original = AppConfig.AdaptiveFitSelectorEnabled;
        try
        {
            AppConfig.AdaptiveFitSelectorEnabled = false;
            var engine = new MockInferenceEngine(); // instance flag defaults OFF
            SenseState s = engine.Update(60.0, "Passenger", ClockBase);
            // -1 ⇒ overlay falls back to its static tier-keyed VimsLevels alpha (byte-identical).
            Assert.Equal(-1.0, s.MaskPeakAlphaOverride);
        }
        finally { AppConfig.AdaptiveFitSelectorEnabled = original; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 1c. PER-SET Scale + render character (GridVisibility + FeatherWidth). Scale feeds the
    //     discretizer GAIN; gv + feather are the Shape-axis render character (Kirby 2026-06-11),
    //     re-seeded from the active set's curve on a set move (via _shader → Current.Shader).
    //     All three are a no-op at Set0 (Scale 11.0, gv 0.75, feather 0.50 == the engine
    //     defaults) — what these tests pin: the wiring exists and is byte-identical at Set0.
    //     Cross-Shape divergence (gv/feather actually CHANGING on a move) is pinned by
    //     ShapeAxisWiringTests; here (4,2) is the Set0-column Shape so it stays default.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FlagOn_AtSet0_DiscretizerScaleIsNoOp_TierStreamMatchesFlagOff()
    {
        bool original = AppConfig.AdaptiveFitSelectorEnabled;
        try
        {
            // 70 ticks × 100 ms = 7 s ≪ SettlingMinSeconds (60 s), so the flag-ON selector never
            // evaluates and the active set stays pinned at Set0. DriveTierStream ramps OFI ~0→200
            // gradually so the tier climbs through several DISTINCT tiers (not a fast-onset
            // plateau). The ONLY thing the flag could change across this window is the discretizer
            // GAIN (now the active set's Scale); Set0.Scale == 11.0, so the flag-ON tier stream
            // must equal the flag-OFF stream tick-for-tick — proof the Scale wiring is
            // byte-identical at Set0.
            const int Ticks = 70;
            const double Ofi = 200.0; // ramp ceiling; the gradual climb spans several tiers

            AppConfig.AdaptiveFitSelectorEnabled = false;
            var off = new MockInferenceEngine();                // instance flag OFF
            List<int> offStream = DriveTierStream(off, Ofi, Ticks);

            AppConfig.AdaptiveFitSelectorEnabled = true;
            var on = new MockInferenceEngine { AdaptiveFitSelectorEnabled = true };
            List<int> onStream = DriveTierStream(on, Ofi, Ticks);

            Assert.True(ResolvesToSet0(on),
                "flag-ON engine must still be pinned at Set0 within the settling window");
            Assert.Equal(offStream, onStream);
            // Sanity: the ramp genuinely exercised the discretizer across more than one tier
            // (otherwise a flat stream would make the parity assertion vacuous).
            Assert.True(onStream.Distinct().Count() > 1,
                "the OFI ramp should span more than one tier so the parity check is meaningful");
        }
        finally { AppConfig.AdaptiveFitSelectorEnabled = original; }
    }

    [Fact]
    public void FlagOn_AfterSilentSetMove_GridVisibilityAndFeatherBaselineEqualActiveSetCurve()
    {
        bool original = AppConfig.AdaptiveFitSelectorEnabled;
        try
        {
            AppConfig.AdaptiveFitSelectorEnabled = true;

            var log = new InMemoryEventLogStore();
            SeedDurable(log, pressure: 5.0, spike: false); // calm window → silent move to (4,2)
            var engine = new MockInferenceEngine(null, null, null, log)
            {
                AdaptiveFitSelectorEnabled = true,
            };

            DriveCalm(engine);

            var moved = new GridCoord(4, 2);
            Assert.True(ResolvesToSet(engine, moved),
                "the calm window should silently move the set to (4,2)");

            // One more calm tick to read the post-move state the overlay would see.
            SenseState s = engine.Update(0.0, "Passenger", ClockBase.AddSeconds(DriveTicks * TickSeconds));

            // CONTRACT: after a set move the peripheral-grid baseline AND the feather character
            // equal the NEW set's curve (re-seeded from lp.GridVisibility / lp.FeatherWidth,
            // flowing via Current.Shader). Shape is now the render-character axis (Kirby
            // 2026-06-11): (4,2) is the Set0-column Shape (2), so the re-seeded values happen to
            // equal the byte-identical defaults (gv 0.75 / feather 0.50) — but the live assertion
            // reads them from SetGrid.Curve(moved) so it tracks the actual per-set mapping, not a
            // hardcoded default. (A cross-Shape move would re-seed DIFFERENT gv/feather; covered
            // by ShapeAxisWiringTests.)
            LevelParams movedLp = SetGrid.Curve(moved).ForLevel(s.VimsTier);
            Assert.Equal(movedLp.GridVisibility, s.Shader.GridVisibility, 6);
            Assert.Equal(movedLp.FeatherWidth, s.Shader.FeatherWidth, 6);
            // (4,2) is the Set0-column Shape ⇒ the re-seeded values are the byte-identical defaults.
            Assert.Equal(ShaderParams.DefaultGridVisibility, s.Shader.GridVisibility, 6); // 0.75
            Assert.Equal(ShaderParams.DefaultFeatherWidth, s.Shader.FeatherWidth, 6);     // 0.50
        }
        finally { AppConfig.AdaptiveFitSelectorEnabled = original; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 1d. PERSISTENT-OVERLAY FLOOR × selector mask-depth override (regression).
    //     Bug found in user test 2026-06-08: with the always-on floor at L12 on a CALM
    //     screen, the engine's MaskPeakAlphaOverride is resolved for the RAW tier (1 ⇒ L1 =
    //     0.00) and would BLANK the always-on mask — it overrides the tier-keyed alpha in
    //     OverlayWindow.ApplyFovMask. The fix (MainWindow.OnStateChanged +
    //     MainViewModel.ResolveDisplayedMaskOverride) re-resolves the override for the
    //     DISPLAYED (floored) tier. This pins the engine contract that re-resolution relies on:
    //     the displayed-tier depth is non-zero and far above the calm raw-tier depth.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveLevel_DisplayedBaselineTier_GivesNonZeroDepth_UnlikeCalmRawTier()
    {
        var engine = new MockInferenceEngine { AdaptiveFitSelectorEnabled = true };

        double calmRawDepth  = engine.ResolveLevel(1).PeakMaskAlpha;   // raw calm tier (L1 day-1 floor)
        double baselineDepth = engine.ResolveLevel(12).PeakMaskAlpha;  // always-on baseline floor (L12)

        Assert.Equal(0.00, calmRawDepth, 6);                                 // L1 = 0.00 — the blank-out source
        Assert.Equal(OverlayWindow.VimsLevels[11].Alpha, baselineDepth, 6);  // 0.92 (set0 == VimsLevels)
        Assert.True(baselineDepth > calmRawDepth,
            "the always-on mask must render at the DISPLAYED baseline depth, not the calm raw-tier depth");
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
            ArmVestibular(advisory); // live path: sets the persistent vestibular signal (D-13)
            Assert.True(advisory.IsVestibularProtectionEnabled);

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

    // ──────────────────────────────────────────────────────────────────────────
    // 5b. SENSITIVE PROFILE (Beta-1 #7) — propose-stronger-only ALSO covers the
    //     SENSITIVE habituation profile (independent of the vestibular flag). The
    //     selector previously applied the silent same-segment Band-DOWN for sensitive
    //     users, quietly lowering the whole mask-alpha envelope for the cohort least
    //     able to tolerate it. The protection-floor guard now blocks it.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FlagOn_SensitiveProfile_BlocksTheWeakerBandMove_ThatATypicalUserWouldTake()
    {
        bool original = AppConfig.AdaptiveFitSelectorEnabled;
        try
        {
            AppConfig.AdaptiveFitSelectorEnabled = true;

            // Identical calm window in both engines: it favours the Band-DOWN (weaker) move
            // to (4,2). The SENSITIVE engine must BLOCK it while the (default) TYPICAL engine
            // takes it — proving the profile-aware guard, not a generic hold, does the work.
            var sensitiveLog = new InMemoryEventLogStore();
            var typicalLog   = new InMemoryEventLogStore();
            SeedDurable(sensitiveLog, pressure: 5.0, spike: false);
            SeedDurable(typicalLog,   pressure: 5.0, spike: false);

            var sensitive = new MockInferenceEngine(null, null, null, sensitiveLog)
            {
                AdaptiveFitSelectorEnabled = true,
            };
            sensitive.SetHabituationProfile(SenseEase.Core.Inference.HabituationProfile.Reactive, "test");

            var typical = new MockInferenceEngine(null, null, null, typicalLog)
            {
                AdaptiveFitSelectorEnabled = true,   // default profile (Typical) — not sensitive
            };

            DriveCalm(sensitive);
            DriveCalm(typical);

            var weaker = new GridCoord(4, 2);

            Assert.True(ResolvesToSet0(sensitive),
                "sensitive profile must block the weaker-band move and hold at Set0");
            Assert.False(ResolvesToSet(sensitive, weaker),
                "sensitive must NOT silently step toward the weaker band (4,2)");
            Assert.True(ResolvesToSet(typical, weaker),
                "a typical user should take the weaker-band move the sensitive guard blocked");
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
            ArmVestibular(engine); // live path (D-13)

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

    [Fact]
    public void FlagOn_Vestibular_ConfirmedStrongerMove_ClampsResolvedLevelToTheL9Floor()
    {
        bool original = AppConfig.AdaptiveFitSelectorEnabled;
        try
        {
            AppConfig.AdaptiveFitSelectorEnabled = true;

            // NON-VACUOUS L9 floor (spec item 1): unlike the weaker-band test above (where the
            // guard blocks the move so the floor holds vacuously), here a high-pressure window
            // makes the selector argmax the Band-UP neighbour (6,2) — a STRONGER, cross-segment
            // target that PASSES the propose-stronger-only vestibular guard (Band 6 >= 5). It is
            // cross-segment, so it QUEUES as a pending proposal (not silently applied). Confirming
            // it is an ACTUAL applied move, on which the engine clamps the snapped felt level to
            // >= 9 for the vestibular user — the floor we assert on a real move, not vacuously.
            //
            // Pressure 100 against the vestibular EffectiveTrigger (50, set by SetVestibularProtection)
            // yields P90 norm ≈ 2.0 — the SAME high-pressure shape the non-advisory cross-segment
            // test produces with 170 against the default trigger 85 — so the argmax is (6,2).
            var log = new InMemoryEventLogStore();
            SeedDurable(log, pressure: 100.0, spike: false);
            var engine = new MockInferenceEngine(null, null, null, log)
            {
                AdaptiveFitSelectorEnabled = true,
            };
            ArmVestibular(engine); // EffectiveTrigger = 50 (norm = 100/50 = 2.0)

            // Feed OFI=0 so the live tier stays at 1 — only the durable LOG stats drive the set,
            // so the L9 floor can ONLY come from the confirmed move (not a live pressure climb).
            DriveCalm(engine, ofi: 0.0);

            // The stronger cross-segment move was QUEUED (propose-confirm), not silently applied:
            // the set is still Set0 and the live tier still 1 until the user confirms.
            Assert.True(ResolvesToSet0(engine),
                "a cross-segment proposal must HOLD the set until the user confirms");
            var target = new GridCoord(6, 2);
            var pending = engine.PeekPendingSetMove();
            Assert.NotNull(pending);
            Assert.Equal(target.Band,  pending!.Value.Band);
            Assert.Equal(target.Shape, pending.Value.Shape);

            // Confirm the proposal — the ACTUAL applied move. ConfirmPendingSetMove snaps the
            // felt level UP to Math.Max(targetLevel, 9) for a vestibular user.
            Assert.True(engine.ConfirmPendingSetMove(),
                "confirming a still-valid stronger proposal must apply the move");

            // The applied move moved the set off Set0 (real move, not vacuous) AND clamped the
            // resolved/confirmed level to the vestibular L9 floor.
            Assert.True(ResolvesToSet(engine, target),
                "the confirmed stronger move should resolve to the (6,2) curve");
            Assert.True(engine.CurrentLevel >= 9,
                $"a vestibular user's confirmed set move must clamp the level to >= 9 (was {engine.CurrentLevel})");
            Assert.Equal(9, engine.CurrentLevel); // (6,2) nominal L8 → Math.Max(8, 9) = 9
        }
        finally
        {
            AppConfig.AdaptiveFitSelectorEnabled = original;
        }
    }
}
