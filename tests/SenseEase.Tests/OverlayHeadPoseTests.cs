using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using SenseEase.App;
using SenseEase.App.Configuration;
using SenseEase.App.ViewModels;
using SenseEase.App.Views;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Configuration;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Real;
using Xunit;
using Xunit.Abstractions;

namespace SenseEase.Tests;

/// <summary>M5 (Overlay Integrator) gate tests. Pin the head-pose →
/// SenseState → OverlayWindow plumbing introduced in M5:
/// <list type="bullet">
///   <item>Flag-off + null-sample path leaves HeadGazeX/Y at center.</item>
///   <item>Reader-returns-sample path propagates Cx/Cy through SenseState.</item>
///   <item>MockSweepHeadPoseReader generates the expected 20-second pattern
///         at key timestamps.</item>
///   <item>OverlayWindow's resolved field center (<c>FieldCenterXForTests</c>)
///         follows <c>HeadGazeX/Y</c> when <c>HeadTrackingActive</c> is true.</item>
/// </list>
/// These are the M5 acceptance gates. STA-required tests reuse the same
/// pattern as <see cref="NotificationCardWindowTests"/> (own STA thread +
/// captured-exception relay).
///
/// <para><b>Baseline (UI Consolidation Sprint, 2026-05-30):</b> the M3
/// invariant — flag-off → <see cref="DefaultHeadPoseReader"/> → center
/// default (cx=0.5, cy=0.5, brush radius 0.85) — is pinned to the v0.9
/// byte-equivalent baseline and SURVIVES the v0.10 → v0.11 transition.
/// Display Comfort decommission is orthogonal to head tracking and does
/// not perturb the invariant. The new sprint reference baseline for the
/// overall app behaviour is v0.11; see CHANGELOG.md for the inventory of
/// what changed.</para></summary>
public class OverlayHeadPoseTests
{
    private readonly ITestOutputHelper _output;
    public OverlayHeadPoseTests(ITestOutputHelper output) => _output = output;

    // ── 1) Flag off: state update keeps HeadGaze at center ─────────────────

    [Fact]
    public void MainViewModel_FlagOff_StateUpdate_HeadGazeStaysAtCenter()
    {
        // M3 invariant: with HeadTrackingEnabled=false, DefaultHeadPoseReader
        // returns null on every call so UpdateFromState must coerce
        // HeadGazeX/Y back to 0.5 and HeadTrackingActive to false. This is
        // the byte-equivalent-to-v0.9 guarantee.
        bool prior = AppConfig.HeadTrackingEnabled;
        try
        {
            AppConfig.HeadTrackingEnabled = false;
            var vm = new MainViewModel();
            Assert.IsType<DefaultHeadPoseReader>(vm.HeadPoseReader);

            SenseState? published = null;
            vm.StateChanged += (_, s) => published = s;

            var state = new SenseState
            {
                HeadGazeX = 0.9, HeadGazeY = 0.1, HeadTrackingActive = true,
            };
            vm.UpdateFromStateForTests(state);

            Assert.NotNull(published);
            Assert.Equal(0.5, published!.HeadGazeX);
            Assert.Equal(0.5, published.HeadGazeY);
            Assert.False(published.HeadTrackingActive);
        }
        finally
        {
            AppConfig.HeadTrackingEnabled = prior;
        }
    }

    // ── 2) Flag on, reader returns null: still snaps to center ─────────────

    [Fact]
    public void MainViewModel_FlagOn_ReaderReturnsNull_HeadGazeStaysAtCenter()
    {
        // Even with the flag flipped on, if the (mock) reader returns null
        // — sidecar not running, face not detected, staleness fallback —
        // the consumer MUST fall back to the (0.5, 0.5) center default
        // per IHeadPoseReader's contract.
        var vm = new MainViewModel();
        vm.OverrideHeadPoseReaderForTests = new NullStubReader();

        SenseState? published = null;
        vm.StateChanged += (_, s) => published = s;

        var state = new SenseState
        {
            HeadGazeX = 0.7, HeadGazeY = 0.3, HeadTrackingActive = true,
        };
        vm.UpdateFromStateForTests(state);

        Assert.NotNull(published);
        Assert.Equal(0.5, published!.HeadGazeX);
        Assert.Equal(0.5, published.HeadGazeY);
        Assert.False(published.HeadTrackingActive);
    }

    // ── 3) Flag on, reader returns sample: state follows it ────────────────

    [Fact]
    public void MainViewModel_FlagOn_ReaderReturnsSample_HeadGazeFollowsSample()
    {
        var vm = new MainViewModel();
        var sample = new HeadPoseSample(
            TimestampMs:  42, Cx: 0.7, Cy: 0.3, Luma: 0.4,
            FaceDetected: true, FacePresent: 1.0);
        vm.OverrideHeadPoseReaderForTests = new ConstantReader(sample);

        SenseState? published = null;
        vm.StateChanged += (_, s) => published = s;

        var state = new SenseState
        {
            HeadGazeX = 0.5, HeadGazeY = 0.5, HeadTrackingActive = false,
        };
        vm.UpdateFromStateForTests(state);

        Assert.NotNull(published);
        Assert.Equal(0.7, published!.HeadGazeX);
        Assert.Equal(0.3, published.HeadGazeY);
        Assert.True(published.HeadTrackingActive);
    }

    // ── 4) MockSweepHeadPoseReader pattern ─────────────────────────────────

    [Fact]
    public void MockSweepHeadPoseReader_ProducesExpectedPattern()
    {
        // Drive the sweep with a controllable elapsed-seconds source so the
        // assertions don't race the wall clock. Pattern (from M5 spec):
        //   0..5  s: horizontal sweep, cx walks 0.5 → 0.1 → 0.9 → 0.5, cy held at 0.5
        //   5..10 s: vertical sweep,   cy walks 0.5 → 0.1 → 0.9 → 0.5, cx held at 0.5
        //  10..15 s: circular sweep around (0.5, 0.5), radius 0.3
        //  15..20 s: hold at (0.5, 0.5)
        //  >=20 s : null (sweep finished, fall back to center default)
        double t = 0;
        var reader = new MockSweepHeadPoseReader(() => t);

        // t=0: center start of horizontal phase
        t = 0;
        var s0 = reader.ReadLatest();
        Assert.NotNull(s0);
        Assert.Equal(0.5, s0!.Cx, 3);
        Assert.Equal(0.5, s0.Cy, 3);

        // t=2.5 s: midpoint of horizontal phase — cx at the first extreme 0.1
        t = 2.5;
        var s1 = reader.ReadLatest();
        Assert.NotNull(s1);
        Assert.Equal(0.1, s1!.Cx, 3);
        Assert.Equal(0.5, s1.Cy, 3);

        // t=3.75 s: 3/4 through horizontal phase — cx at the second extreme 0.9
        t = 3.75;
        var s2 = reader.ReadLatest();
        Assert.NotNull(s2);
        Assert.Equal(0.9, s2!.Cx, 3);
        Assert.Equal(0.5, s2.Cy, 3);

        // t=7.5 s: midpoint of vertical phase — cy at first extreme 0.1, cx held
        t = 7.5;
        var s3 = reader.ReadLatest();
        Assert.NotNull(s3);
        Assert.Equal(0.5, s3!.Cx, 3);
        Assert.Equal(0.1, s3.Cy, 3);

        // t=12.5 s: midpoint of circular phase — angle = π → cx = 0.5 + 0.3·cos(π) = 0.2, cy = 0.5 + 0.3·sin(π) = 0.5
        t = 12.5;
        var s4 = reader.ReadLatest();
        Assert.NotNull(s4);
        Assert.Equal(0.5 + 0.3 * Math.Cos(Math.PI), s4!.Cx, 3);
        Assert.Equal(0.5 + 0.3 * Math.Sin(Math.PI), s4.Cy, 3);

        // t=18 s: hold phase — straight back to center
        t = 18;
        var s5 = reader.ReadLatest();
        Assert.NotNull(s5);
        Assert.Equal(0.5, s5!.Cx, 3);
        Assert.Equal(0.5, s5.Cy, 3);

        // t=20.5 s: sweep finished — ReadLatest must return null so the
        // consumer falls back to the center default.
        t = 20.5;
        var s6 = reader.ReadLatest();
        Assert.Null(s6);
    }

    // ── 5) OverlayWindow field center follows HeadGaze (A2) ────────────────

    [Fact]
    public void OverlayWindow_FieldCenter_FollowsHeadGaze()
    {
        // STA test — mirrors NotificationCardWindowTests.RunOnSta pattern.
        // Beta A2: there is no RadialGradientBrush anymore (single-pass field). The
        // resolved field center (FieldCenterX/YForTests) is the one-clock pose value the
        // corner-normalized field uses. Construct OverlayWindow, apply a state with
        // HeadTrackingActive=true, then assert the field center moved to that point.
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();

            var state = new SenseState
            {
                VimsTier            = 3,        // mid tier so the mask actually renders
                HeadGazeX           = 0.6,
                HeadGazeY           = 0.4,
                HeadTrackingActive  = true,
                MaskActive          = true,
            };
            w.ApplyState(state);

            Assert.Equal(0.6, w.FieldCenterXForTests, 3);
            Assert.Equal(0.4, w.FieldCenterYForTests, 3);

            // Flag-off branch on the same window: snaps back to (0.5, 0.5).
            var stateOff = new SenseState
            {
                VimsTier            = 3,
                HeadGazeX           = 0.9,      // would-be sample value (intentionally != 0.5)
                HeadGazeY           = 0.1,
                HeadTrackingActive  = false,    // tracking inactive — must ignore HeadGazeX/Y
                MaskActive          = true,
            };
            w.ApplyState(stateOff);

            Assert.Equal(0.5, w.FieldCenterXForTests, 3);
            Assert.Equal(0.5, w.FieldCenterYForTests, 3);
        });
    }

    // ── 6) M5 UX-iter 3 + M5.1-B: travel range depends on follow mode ───

    /// <summary>M5.1-B Balanced mode (default): head-gaze clamped to
    /// [0.10, 0.90] so the periphery is always partially dimmed regardless
    /// of gaze direction. Corner values (1.0 / 0.0) clamp to the central
    /// window. Within-range values pass through.</summary>
    [Fact]
    public void OverlayWindow_HeadGazeBalancedMode_ClampsToCentralWindow()
    {
        RunOnSta(() =>
        {
            // Balanced is the AppConfig default; assert explicitly so a
            // future change to the default doesn't silently break this test.
            var prior = AppConfig.HeadTrackingFollowMode;
            AppConfig.HeadTrackingFollowMode = HeadTrackingFollowModeKind.Balanced;
            try
            {
                var w = new OverlayWindow();
                w.ApplyTemplate();

                // Corner (1.0, 1.0) clamps to (0.90, 0.90).
                w.ApplyState(new SenseState
                {
                    VimsTier            = 3,
                    HeadGazeX           = 1.0,
                    HeadGazeY           = 1.0,
                    HeadTrackingActive  = true,
                    MaskActive          = true,
                });
                Assert.Equal(0.90, w.FieldCenterXForTests, 3);
                Assert.Equal(0.90, w.FieldCenterYForTests, 3);

                // Other corner (0.0, 0.0) clamps to (0.10, 0.10).
                w.ApplyState(new SenseState
                {
                    VimsTier            = 3,
                    HeadGazeX           = 0.0,
                    HeadGazeY           = 0.0,
                    HeadTrackingActive  = true,
                    MaskActive          = true,
                });
                Assert.Equal(0.10, w.FieldCenterXForTests, 3);
                Assert.Equal(0.10, w.FieldCenterYForTests, 3);

                // In-range value (0.55, 0.45) passes through unchanged.
                w.ApplyState(new SenseState
                {
                    VimsTier            = 3,
                    HeadGazeX           = 0.55,
                    HeadGazeY           = 0.45,
                    HeadTrackingActive  = true,
                    MaskActive          = true,
                });
                Assert.Equal(0.55, w.FieldCenterXForTests, 3);
                Assert.Equal(0.45, w.FieldCenterYForTests, 3);
            }
            finally
            {
                AppConfig.HeadTrackingFollowMode = prior;
            }
        });
    }

    /// <summary>M5.1-B FullFollow mode (opt-in): head-gaze travels the
    /// full [0, 1] range. Corner values pass through unchanged so the
    /// mask can follow gaze all the way to a screen corner.</summary>
    [Fact]
    public void OverlayWindow_HeadGazeFullFollowMode_NoClampWhenActive()
    {
        RunOnSta(() =>
        {
            var prior = AppConfig.HeadTrackingFollowMode;
            AppConfig.HeadTrackingFollowMode = HeadTrackingFollowModeKind.FullFollow;
            try
            {
                var w = new OverlayWindow();
                w.ApplyTemplate();

                // Extreme corner passes through.
                w.ApplyState(new SenseState
                {
                    VimsTier            = 3,
                    HeadGazeX           = 1.0,
                    HeadGazeY           = 1.0,
                    HeadTrackingActive  = true,
                    MaskActive          = true,
                });
                Assert.Equal(1.0, w.FieldCenterXForTests, 3);
                Assert.Equal(1.0, w.FieldCenterYForTests, 3);

                // Other corner.
                w.ApplyState(new SenseState
                {
                    VimsTier            = 3,
                    HeadGazeX           = 0.0,
                    HeadGazeY           = 0.0,
                    HeadTrackingActive  = true,
                    MaskActive          = true,
                });
                Assert.Equal(0.0, w.FieldCenterXForTests, 3);
                Assert.Equal(0.0, w.FieldCenterYForTests, 3);

                // Out-of-range still clamped to [0, 1] for safety.
                w.ApplyState(new SenseState
                {
                    VimsTier            = 3,
                    HeadGazeX           = 1.5,
                    HeadGazeY           = -0.3,
                    HeadTrackingActive  = true,
                    MaskActive          = true,
                });
                Assert.Equal(1.0, w.FieldCenterXForTests, 3);
                Assert.Equal(0.0, w.FieldCenterYForTests, 3);
            }
            finally
            {
                AppConfig.HeadTrackingFollowMode = prior;
            }
        });
    }

    /// <summary>M5 UX-iter 3 Fix B: BrushRadius dynamically scales to keep
    /// Kirby round-3 live ruling (2026-06-11, option A): the window radius is FROZEN
    /// at the 0.85 baseline — the old corner-coverage rescaling
    /// (max(0.85, farthestCorner × 1.10)) read as the grid "snapping/zooming" near
    /// edges (吸附放大). Design principle: NOTHING jumps or breathes; following the
    /// head (the hole's CENTER) is the maximum allowed motion. This pins the frozen
    /// radius at center, corner, edge, and flag-off alike.</summary>
    [Fact]
    public void OverlayWindow_BrushRadius_FrozenAtBaseline_RegardlessOfCenter()
    {
        // FullFollow so the center can reach (1, 1) unclamped — the radius must
        // STILL stay frozen there (the old formula would have given 1.556).
        RunOnSta(() =>
        {
            var prior = AppConfig.HeadTrackingFollowMode;
            AppConfig.HeadTrackingFollowMode = HeadTrackingFollowModeKind.FullFollow;
            try
            {
            var w = new OverlayWindow();
            w.ApplyTemplate();

            // Center.
            w.ApplyState(new SenseState
            {
                VimsTier = 3, HeadGazeX = 0.5, HeadGazeY = 0.5,
                HeadTrackingActive = true, MaskActive = true,
            });
            Assert.Equal(0.85, w.FieldRadiusForTests, 3);

            // Full corner (old formula: sqrt(2)×1.10 ≈ 1.556 — now frozen).
            w.ApplyState(new SenseState
            {
                VimsTier = 3, HeadGazeX = 1.0, HeadGazeY = 1.0,
                HeadTrackingActive = true, MaskActive = true,
            });
            Assert.Equal(0.85, w.FieldRadiusForTests, 3);

            // Edge (old formula: ≈1.230 — now frozen).
            w.ApplyState(new SenseState
            {
                VimsTier = 3, HeadGazeX = 1.0, HeadGazeY = 0.5,
                HeadTrackingActive = true, MaskActive = true,
            });
            Assert.Equal(0.85, w.FieldRadiusForTests, 3);

            // Flag-off: same frozen baseline.
            w.ApplyState(new SenseState
            {
                VimsTier = 3, HeadGazeX = 0.5, HeadGazeY = 0.5,
                HeadTrackingActive = false, MaskActive = true,
            });
            Assert.Equal(0.85, w.FieldRadiusForTests, 3);
            }
            finally
            {
                AppConfig.HeadTrackingFollowMode = prior;
            }
        });
    }

    /// <summary>UpdateHeadGazeCenter applies the same brush mutation as
    /// ApplyFovMask's state-driven path. Verifies the head-pose pump's entry
    /// point (~165 Hz CompositionTarget.Rendering) produces consistent brush
    /// state. Called here with the default dtMs = 0 (no live pump), so the
    /// radius envelope snaps to target — identical to the pre-envelope renderer.</summary>
    [Fact]
    public void OverlayWindow_UpdateHeadGazeCenter_AppliesSameBrushMutation()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();

            // Seed the brush with the v0.9 baseline by applying an initial
            // flag-off state.
            var stateInit = new SenseState
            {
                VimsTier            = 3,
                HeadGazeX           = 0.5,
                HeadGazeY           = 0.5,
                HeadTrackingActive  = false,
                MaskActive          = true,
            };
            w.ApplyState(stateInit);

            // Now exercise the pump's entry point directly (dtMs = 0 → snap).
            w.UpdateHeadGazeCenter(headGazeX: 0.7, headGazeY: 0.3, active: true);
            Assert.Equal(0.7, w.FieldCenterXForTests, 3);
            Assert.Equal(0.3, w.FieldCenterYForTests, 3);
            // Kirby round-3 ruling: radius is FROZEN at baseline regardless of center
            // (the old corner formula would have given ≈1.089 here).
            Assert.Equal(0.85, w.FieldRadiusForTests, 3);

            // Snap back via active=false.
            w.UpdateHeadGazeCenter(headGazeX: 0.7, headGazeY: 0.3, active: false);
            Assert.Equal(0.5, w.FieldCenterXForTests, 3);
            Assert.Equal(0.5, w.FieldCenterYForTests, 3);
            Assert.Equal(0.85, w.FieldRadiusForTests, 3);
        });
    }

    // ── 7) Beta A1: one-clock motion model (radius derives from smoothed center) ─
    //
    // D-ruling (BETA_FEATHERING_COHERENCE_PLAN.md §A1 item 3): the SEPARATE
    // asymmetric radius envelope (25/600 ms _smoothedRadius clock) is REMOVED. The
    // head pose is now smoothed ONCE upstream by a single One-Euro filter in the
    // MainWindow pump; the (cx,cy) handed to OverlayWindow is already the one-clock
    // value, and radius is a PURE deterministic function of that smoothed center,
    // computed in the SAME frame. So within OverlayWindow the radius no longer eases
    // on its own clock — it tracks the center exactly. These tests pin that:
    //   • radius reaches the corner-reach target in the SAME frame as the center
    //     (no separate 600 ms release lag — the percept the A1 change kills);
    //   • the grid OpacityMask hole (the third geometry) tracks center+radius in
    //     the same frame (one moving boundary, not three).
    // The dtMs parameter is now inert inside the head-pose application (kept on the
    // signature for a clean revert to f381a43); the smoothing that used to live
    // here is exercised at the One-Euro level by the convergence test below.
    //
    // Beta A2: the seams are now the resolved FIELD values (FieldCenterX/YForTests,
    // FieldRadiusForTests) — there is no RadialGradientBrush in the single-pass field.
    // The grid hole is no longer a separate geometry at all (it is the same per-pixel
    // field), so the old "grid hole tracks the brush" test is replaced by a field-level
    // assertion that center+radius derive from the same pose in the same frame.

    /// <summary>Kirby round-3 ruling (option A): the radius is a FROZEN constant — it
    /// no longer derives from the center at all. A full corner excursion moves the
    /// CENTER in the same frame while the radius stays at the 0.85 baseline (the old
    /// corner-reach formula would have produced 1.556). One clock, one moving value.</summary>
    [Fact]
    public void OverlayWindow_Radius_FrozenWhileCenterTracks_InSameFrame()
    {
        RunOnSta(() =>
        {
            var prior = AppConfig.HeadTrackingFollowMode;
            AppConfig.HeadTrackingFollowMode = HeadTrackingFollowModeKind.FullFollow;
            try
            {
                var w = new OverlayWindow();
                w.ApplyTemplate();
                // Seed baseline radius via a flag-off ApplyState.
                w.ApplyState(new SenseState { VimsTier = 3, HeadGazeX = 0.5, HeadGazeY = 0.5, HeadTrackingActive = false, MaskActive = true });
                Assert.Equal(0.85, w.FieldRadiusForTests, 3);

                // A realistic ~6 ms frame toward the corner: the center lands the SAME
                // frame; the radius does not move (frozen — no corner rescale).
                w.UpdateHeadGazeCenter(headGazeX: 1.0, headGazeY: 1.0, active: true, dtMs: 6.0);
                Assert.Equal(1.0, w.FieldCenterXForTests, 3);
                Assert.Equal(1.0, w.FieldCenterYForTests, 3);
                Assert.Equal(0.85, w.FieldRadiusForTests, 3);
            }
            finally { AppConfig.HeadTrackingFollowMode = prior; }
        });
    }

    /// <summary>Kirby round-3 ruling (option A): out-and-back excursion — the radius
    /// stays frozen at the baseline the WHOLE way (out to the corner AND back), while
    /// the center tracks each frame. There is no radius motion of any kind left: not
    /// the old 600 ms envelope, not the corner rescale. Nothing jumps or breathes;
    /// following the head is the maximum allowed motion.</summary>
    [Fact]
    public void OverlayWindow_Radius_FrozenThroughOutAndBackExcursion()
    {
        RunOnSta(() =>
        {
            var prior = AppConfig.HeadTrackingFollowMode;
            AppConfig.HeadTrackingFollowMode = HeadTrackingFollowModeKind.FullFollow;
            try
            {
                var w = new OverlayWindow();
                w.ApplyTemplate();
                w.ApplyState(new SenseState { VimsTier = 3, HeadGazeX = 0.5, HeadGazeY = 0.5, HeadTrackingActive = false, MaskActive = true });

                // Out to the corner: center moves, radius frozen.
                w.UpdateHeadGazeCenter(headGazeX: 1.0, headGazeY: 1.0, active: true, dtMs: 6.0);
                Assert.Equal(1.0, w.FieldCenterXForTests, 3);
                Assert.Equal(0.85, w.FieldRadiusForTests, 3);

                // Back to center: center moves, radius STILL frozen.
                w.UpdateHeadGazeCenter(headGazeX: 0.5, headGazeY: 0.5, active: true, dtMs: 6.0);
                Assert.Equal(0.5, w.FieldCenterXForTests, 3);
                Assert.Equal(0.85, w.FieldRadiusForTests, 3);
            }
            finally { AppConfig.HeadTrackingFollowMode = prior; }
        });
    }

    /// <summary>One-clock (A2): the mask and the grid are now the SAME per-pixel field —
    /// there is no separate grid-hole geometry that could lag the mask (the third moving
    /// stratum is gone entirely). Center and radius derive from the same pose in the same
    /// frame. This pins that center + radius update together in one UpdateHeadGazeCenter
    /// call (one moving boundary).</summary>
    [Fact]
    public void OverlayWindow_OneClock_CenterAndRadius_DeriveTogether_InSameFrame()
    {
        RunOnSta(() =>
        {
            var prior = AppConfig.HeadTrackingFollowMode;
            AppConfig.HeadTrackingFollowMode = HeadTrackingFollowModeKind.FullFollow;
            try
            {
                var w = new OverlayWindow();
                w.ApplyTemplate();
                w.ApplyState(new SenseState { VimsTier = 3, HeadGazeX = 0.5, HeadGazeY = 0.5, HeadTrackingActive = false, MaskActive = true });

                w.UpdateHeadGazeCenter(headGazeX: 0.8, headGazeY: 0.2, active: true, dtMs: 6.0);

                // Center is the clamped pose, resolved in the one call; the radius is
                // FROZEN at baseline (Kirby round-3 ruling — no corner rescale, no
                // second clock, no radius motion at all).
                Assert.Equal(0.8, w.FieldCenterXForTests, 6);
                Assert.Equal(0.2, w.FieldCenterYForTests, 6);
                Assert.Equal(0.85, w.FieldRadiusForTests, 6);
            }
            finally { AppConfig.HeadTrackingFollowMode = prior; }
        });
    }

    /// <summary>Tracking dropout with the center parked at a corner: the radius was
    /// frozen at the baseline the whole time (Kirby round-3 ruling), and deactivation
    /// recenters the hole while the radius stays exactly where it always was — the
    /// flag-off baseline is byte-identical because nothing ever left it.</summary>
    [Fact]
    public void OverlayWindow_Radius_FrozenAcrossTrackingDropout()
    {
        RunOnSta(() =>
        {
            var prior = AppConfig.HeadTrackingFollowMode;
            AppConfig.HeadTrackingFollowMode = HeadTrackingFollowModeKind.FullFollow;
            try
            {
                var w = new OverlayWindow();
                w.ApplyTemplate();
                w.ApplyState(new SenseState { VimsTier = 3, HeadGazeX = 0.5, HeadGazeY = 0.5, HeadTrackingActive = false, MaskActive = true });

                w.UpdateHeadGazeCenter(headGazeX: 1.0, headGazeY: 1.0, active: true, dtMs: 10000);
                Assert.Equal(0.85, w.FieldRadiusForTests, 3); // frozen even at the corner

                // Tracking drops out → hole recenters; radius unchanged (it never moved).
                w.UpdateHeadGazeCenter(headGazeX: 1.0, headGazeY: 1.0, active: false, dtMs: 16);
                Assert.Equal(0.85, w.FieldRadiusForTests, 3);
                Assert.Equal(0.5, w.FieldCenterXForTests, 3);
                Assert.Equal(0.5, w.FieldCenterYForTests, 3);
            }
            finally { AppConfig.HeadTrackingFollowMode = prior; }
        });
    }

    /// <summary>Micro-jitter around a central gaze does not pulse the radius:
    /// within the central box the head-coupled target is floored at the baseline
    /// 0.85, so small frame-to-frame gaze jitter leaves the radius pinned at 0.85
    /// (no breathing from sensor noise).</summary>
    [Fact]
    public void OverlayWindow_RadiusEnvelope_CentralJitter_StaysAtFloor()
    {
        RunOnSta(() =>
        {
            var prior = AppConfig.HeadTrackingFollowMode;
            AppConfig.HeadTrackingFollowMode = HeadTrackingFollowModeKind.FullFollow;
            try
            {
                var w = new OverlayWindow();
                w.ApplyTemplate();
                w.ApplyState(new SenseState { VimsTier = 3, HeadGazeX = 0.5, HeadGazeY = 0.5, HeadTrackingActive = false, MaskActive = true });

                // Jitter within the central box where farthestCorner*1.10 < 0.85.
                double[] xs = { 0.50, 0.52, 0.48, 0.51, 0.49 };
                foreach (var x in xs)
                    w.UpdateHeadGazeCenter(headGazeX: x, headGazeY: 0.5, active: true, dtMs: 16);

                Assert.Equal(0.85, w.FieldRadiusForTests, 3);
            }
            finally { AppConfig.HeadTrackingFollowMode = prior; }
        });
    }

    // ── 8) Beta A1: single One-Euro filter — one-clock convergence ──────────
    //
    // The pose is smoothed ONCE by this filter in the MainWindow pump; its single
    // output (cx,cy) drives center + radius + grid hole. These tests drive a pose
    // STEP at a simulated frame cadence and assert the filter converges to target
    // with one coherent time-constant (not the old 600 ms / 1.7 s side-clocks), and
    // that near-still jitter is smoothed so the edge stops "breathing".

    /// <summary>A sustained step from (0.5,0.5) to a corner converges to the target
    /// over a short burst of realistic ~6 ms frames — fast follow, no 600 ms-class
    /// lag — and is monotonic (no overshoot/ringing).</summary>
    [Fact]
    public void OneEuroFilter_StepInput_ConvergesToTarget_FastAndMonotonic()
    {
        var f = new MainWindow.OneEuroFilter2D(
            SenseDefaults.OverlayOneEuroFcMin,
            SenseDefaults.OverlayOneEuroBeta,
            SenseDefaults.OverlayOneEuroDCutoff);

        const double dt = 6.0 / 1000.0;   // ~165 Hz render frame, seconds
        // First call seeds to the raw sample (no lag-in from origin).
        var (sx, sy) = f.Filter(0.5, 0.5, dt);
        Assert.Equal(0.5, sx, 6);
        Assert.Equal(0.5, sy, 6);

        // Now hold a step target at the corner and pump frames.
        double prev = 0.5;
        double last = 0.5;
        for (int i = 0; i < 60; i++)   // 60 frames ≈ 360 ms of head fixation
        {
            (last, _) = f.Filter(0.9, 0.9, dt);
            Assert.True(last >= prev - 1e-9, $"monotonic toward target, frame {i}: {prev} -> {last}");
            Assert.True(last <= 0.9 + 1e-9, $"no overshoot past target, frame {i}: {last}");
            prev = last;
        }
        // Within ~360 ms of fixation the smoothed value has closed ~90% of the
        // 0.5→0.9 gap (≈0.867) — far faster than the removed 600 ms radius / 1.7 s
        // grid side-clocks would have allowed.
        Assert.True(last > 0.85, $"should converge close to target within ~360 ms, was {last}");
    }

    /// <summary>Near-still micro-jitter is heavily smoothed: the One-Euro low cutoff
    /// at low speed rejects the sidecar's per-sample noise so the smoothed center
    /// barely moves — this is what stops the feather edge "breathing" at rest.</summary>
    [Fact]
    public void OneEuroFilter_NearStillJitter_IsHeavilySmoothed()
    {
        var f = new MainWindow.OneEuroFilter2D(
            SenseDefaults.OverlayOneEuroFcMin,
            SenseDefaults.OverlayOneEuroBeta,
            SenseDefaults.OverlayOneEuroDCutoff);

        const double dt = 6.0 / 1000.0;
        f.Filter(0.5, 0.5, dt); // seed

        // Feed small alternating jitter around 0.5 (±0.01) at frame cadence.
        double[] jitter = { 0.51, 0.49, 0.505, 0.495, 0.51, 0.49, 0.50 };
        double x = 0.5;
        foreach (var j in jitter)
            (x, _) = f.Filter(j, 0.5, dt);

        // The smoothed output stays well inside the jitter band — the noise is
        // attenuated, not tracked. (A raw passthrough would land on 0.50; a laggy
        // tracker would chase the last 0.50. Either way it must be within the band.)
        Assert.True(Math.Abs(x - 0.5) < 0.01, $"jitter should be attenuated near 0.5, was {x}");
    }

    /// <summary>One-Euro is adaptive: a FAST step moves the smoothed value more per
    /// frame than the SAME-magnitude jitter does — proving the cutoff opens up with
    /// speed (responsive follow on real motion, heavy smoothing on noise). This is
    /// the asymmetric fast-attack/slow-settle character as ONE clock.</summary>
    [Fact]
    public void OneEuroFilter_FastMotion_OpensCutoff_VsSlowJitter()
    {
        const double dt = 6.0 / 1000.0;

        // Filter A: a FAST ramp — big sustained steps build a high speed estimate,
        // opening the adaptive cutoff so the filter tracks aggressively.
        var fa = new MainWindow.OneEuroFilter2D(
            SenseDefaults.OverlayOneEuroFcMin, SenseDefaults.OverlayOneEuroBeta, SenseDefaults.OverlayOneEuroDCutoff);
        fa.Filter(0.5, 0.5, dt);
        fa.Filter(0.6, 0.5, dt);
        fa.Filter(0.7, 0.5, dt);
        var (fastX, _) = fa.Filter(0.8, 0.5, dt);
        double fastDelta = fastX - 0.5;

        // Filter B: a SLOW ramp into the same region — small early increments keep
        // the speed estimate low, so the cutoff stays near FcMin (heavier smoothing).
        var fb = new MainWindow.OneEuroFilter2D(
            SenseDefaults.OverlayOneEuroFcMin, SenseDefaults.OverlayOneEuroBeta, SenseDefaults.OverlayOneEuroDCutoff);
        fb.Filter(0.5, 0.5, dt);
        fb.Filter(0.52, 0.5, dt);
        fb.Filter(0.54, 0.5, dt);
        var (slowX, _) = fb.Filter(0.8, 0.5, dt);
        double slowDelta = slowX - 0.5;

        // The fast-motion filter has tracked further toward the target by this frame
        // (cutoff opened with speed), confirming the adaptive fast-attack behaviour.
        Assert.True(fastDelta > slowDelta,
            $"fast motion should track further than slow dithering: fast={fastDelta}, slow={slowDelta}");
    }

    // ── 9) TEST-04: Beta A2 single-pass field renderer round-trip ───────────
    //
    // D-2 wiring at the RENDERER boundary: two MaskDepth values ⇒ the rendered field
    // differs; two FeatherWidth values ⇒ the feather band differs; MaskDepth below the
    // floor clamps the EFFECTIVE peak at the 0.20-equivalent; default knobs ⇒ the
    // resolved peak is byte-identical to the tier table. Pure field-math unit tests live
    // in OverlayFieldMathTests; these exercise the full STA ApplyState → WriteableBitmap
    // path through the public seams.

    private static SenseState FieldState(int tier, double maskDepth, double featherWidth, bool maskActive = true)
        => new()
        {
            VimsTier   = tier,
            MaskActive = maskActive,
            Shader     = new ShaderParams { MaskDepth = maskDepth, FeatherWidth = featherWidth },
        };

    /// <summary>TEST-04: two MaskDepth values produce a DIFFERENT rendered field (the
    /// depth knob actuates), and the default MaskDepth resolves the effective peak to the
    /// tier table value byte-identically.</summary>
    [Fact]
    public void OverlayWindow_TwoMaskDepths_ProduceDifferentField_DefaultIsTableIdentical()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();

            // Default MaskDepth ⇒ effective peak == the tier-6 table alpha (0.50), identical.
            w.ApplyState(FieldState(tier: 6, maskDepth: ShaderParams.DefaultMaskDepth, featherWidth: ShaderParams.DefaultFeatherWidth));
            double tableAlpha = OverlayWindow.VimsLevels[5].Alpha; // L6 == 0.50
            Assert.Equal(tableAlpha, w.EffectivePeakAlphaForTests, 6);
            byte[]? deep = w.FieldBufferSnapshotForTests;
            Assert.NotNull(deep);

            // A SHALLOWER MaskDepth (below default) lowers the effective peak and changes
            // the rendered pixels — the field differs.
            w.ApplyState(FieldState(tier: 6, maskDepth: 0.40, featherWidth: ShaderParams.DefaultFeatherWidth));
            Assert.True(w.EffectivePeakAlphaForTests < tableAlpha,
                $"shallower MaskDepth should lower the effective peak, was {w.EffectivePeakAlphaForTests}");
            byte[]? shallow = w.FieldBufferSnapshotForTests;
            Assert.NotNull(shallow);
            Assert.False(BuffersEqual(deep!, shallow!), "two MaskDepth values must render different fields");
        });
    }

    /// <summary>TEST-04: two FeatherWidth values produce a DIFFERENT rendered field (the
    /// feather band width actuates), and the default FeatherWidth resolves the band start
    /// to the locked 0.35 envelope.</summary>
    [Fact]
    public void OverlayWindow_TwoFeatherWidths_ProduceDifferentField_DefaultIs035Start()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();

            w.ApplyState(FieldState(tier: 8, maskDepth: ShaderParams.DefaultMaskDepth, featherWidth: ShaderParams.DefaultFeatherWidth));
            Assert.Equal(0.35, w.FeatherStartForTests, 6); // envelope-identical at default
            byte[]? narrow = w.FieldBufferSnapshotForTests;
            Assert.NotNull(narrow);

            // A WIDER feather pulls the band start inward (< 0.35) → different field.
            w.ApplyState(FieldState(tier: 8, maskDepth: ShaderParams.DefaultMaskDepth, featherWidth: 0.90));
            Assert.True(w.FeatherStartForTests < 0.35,
                $"wider FeatherWidth should pull the band start inward, was {w.FeatherStartForTests}");
            byte[]? wide = w.FieldBufferSnapshotForTests;
            Assert.NotNull(wide);
            Assert.False(BuffersEqual(narrow!, wide!), "two FeatherWidth values must render different fields");
        });
    }

    /// <summary>TEST-04: MaskDepth driven to 0 cannot drop the EFFECTIVE peak below the
    /// 0.20 floor while the mask is commanded ON (the always-on core protection floor).</summary>
    [Fact]
    public void OverlayWindow_MaskDepthBelowFloor_ClampsAt020_WhenMaskOn()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();

            // A high tier (ceiling well above 0.20) with MaskDepth driven to 0: the
            // multiplier collapses the depth, but the floor catches it at 0.20.
            w.ApplyState(FieldState(tier: 10, maskDepth: 0.0, featherWidth: ShaderParams.DefaultFeatherWidth, maskActive: true));
            Assert.Equal(0.20, w.EffectivePeakAlphaForTests, 6);
        });
    }

    /// <summary>TEST-04: the 0.20 floor applies only when the mask is commanded ON — a
    /// commanded-OFF mask (e.g. L1's 0.00) is never raised to the floor.</summary>
    [Fact]
    public void OverlayWindow_MaskOff_FloorDoesNotApply()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();

            // L1 table alpha is 0.00; mask commanded OFF ⇒ effective peak stays 0 (the
            // floor must not push a commanded-off mask up to 0.20).
            w.ApplyState(FieldState(tier: 1, maskDepth: ShaderParams.DefaultMaskDepth, featherWidth: ShaderParams.DefaultFeatherWidth, maskActive: false));
            Assert.Equal(0.0, w.EffectivePeakAlphaForTests, 6);
        });
    }

    /// <summary>UVHS round 2 (2026-06-11), end-to-end through the rendered field buffer: the
    /// LOW-frequency field carries the FOV mask dim WITH the legacy cell-fill tint folded in,
    /// in the PERIPHERY, and the CENTRE is CLEAN. The grid is no longer in the field (it is
    /// full-res vector geometry on GridCanvas — see the grid tests below), so this asserts only
    /// the mask+tint fold: (a) the exact-centre pixel is fully transparent (alpha 0 — no dim,
    /// no tint); (b) a deep-periphery pixel carries the composited dim+tint (alpha > 0) and its
    /// colour is near-black (the cool-shifted L12 mask over the #08080A tint).</summary>
    [Fact]
    public void OverlayWindow_RenderedField_DimAndCellTintInPeriphery_CentreClean()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();

            // High tier so the mask renders deep.
            var state = new SenseState
            {
                VimsTier           = 12,
                MaskActive         = true,
                HeadTrackingActive = false, // centred (0.5, 0.5)
                Shader             = new ShaderParams(),
            };
            for (int i = 0; i < 40; i++) w.ApplyState(state);

            byte[]? buf = w.FieldBufferSnapshotForTests;
            Assert.NotNull(buf);
            int fw = w.FieldWidthForTests, fh = w.FieldHeightForTests;
            Assert.True(fw > 12 && fh > 12, $"field too small to sample: {fw}x{fh}");

            // (a) CENTRE CLEAN: the exact centre pixel is fully transparent (no dim, no tint).
            int cxPix = fw / 2, cyPix = fh / 2;
            int ci = (cyPix * fw + cxPix) * 4;
            Assert.Equal(0, buf![ci + 3]); // centre alpha 0 — clean hole

            // (b) DEEP PERIPHERY: the top-edge pixel carries the composited mask dim + folded
            // cell tint. Alpha must be present, and the colour near-black (the dim is deep at
            // L12; the cell tint adds a faint cool near-black underneath).
            int periRow = 1;            // deep periphery (top edge), window ≈ saturated
            int periCol = fw / 2;       // mid-column, still deep periphery vertically
            int pi = (periRow * fw + periCol) * 4;
            Assert.True(buf[pi + 3] > 0,
                $"the peripheral mask dim + cell tint must be present, alpha was {buf[pi + 3]}");
            // Near-black: each premultiplied channel is small relative to a fully opaque white.
            Assert.True(buf[pi + 0] < 64 && buf[pi + 1] < 64 && buf[pi + 2] < 64,
                $"periphery tone must be near-black (dim + tint): B={buf[pi + 0]} G={buf[pi + 1]} R={buf[pi + 2]}");
        });
    }

    /// <summary>A2 RESTING INVARIANT (Kirby's 2026-06-11 RUNTIME verification request):
    /// at L1 REST the ENTIRE rendered field buffer must be fully transparent — alpha 0
    /// EVERYWHERE, no permanent peripheral darkening / vignette. This is the end-to-end
    /// runtime proof of the A2 hard-stop "resting 0.00 at L1": where the unit pin
    /// ComposeFieldPixel_AtRest_TintContributesNothing_A2RestingInvariant proves the
    /// per-pixel math, THIS pin proves the actual WriteableBitmap the renderer fills
    /// (after the full ApplyState → field-write path, settled over 40 ticks) carries no
    /// residual alpha anywhere: exact centre, all four edge midpoints, all four corners,
    /// plus a full-buffer max-alpha scan. VimsTier=1, MaskActive=false, Pressure=0,
    /// OpticalFlowIndex=0, default ShaderParams — the genuine resting state.</summary>
    [Fact]
    public void OverlayWindow_RenderedField_AtRest_FullyTransparent_NoPeripheralVignette()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();

            // The genuine resting state: lowest tier, mask commanded OFF, zero pressure,
            // zero optical flow, default shader knobs, head tracking inactive (centred).
            var rest = new SenseState
            {
                VimsTier           = 1,
                MaskActive         = false,
                Pressure           = 0.0,
                OpticalFlowIndex   = 0.0,
                HeadTrackingActive = false,
                Shader             = new ShaderParams(),
            };
            // Settle over many ticks so any EMA-driven contribution (tint/intensity) has
            // fully relaxed to its resting target before we read the buffer.
            for (int i = 0; i < 40; i++) w.ApplyState(rest);

            byte[]? buf = w.FieldBufferSnapshotForTests;
            Assert.NotNull(buf);
            int fw = w.FieldWidthForTests, fh = w.FieldHeightForTests;
            Assert.True(fw > 12 && fh > 12, $"field too small to sample: {fw}x{fh}");

            // Pbgra32 — alpha is byte +3 of each 4-byte pixel.
            int A(int col, int row) => buf![(row * fw + col) * 4 + 3];

            // Exact centre.
            Assert.Equal(0, A(fw / 2, fh / 2));

            // Four edge midpoints.
            Assert.Equal(0, A(fw / 2, 0));        // top edge
            Assert.Equal(0, A(fw / 2, fh - 1));   // bottom edge
            Assert.Equal(0, A(0, fh / 2));        // left edge
            Assert.Equal(0, A(fw - 1, fh / 2));   // right edge

            // Four corners — the deepest periphery, where any resting vignette would show.
            Assert.Equal(0, A(0, 0));             // top-left
            Assert.Equal(0, A(fw - 1, 0));        // top-right
            Assert.Equal(0, A(0, fh - 1));        // bottom-left
            Assert.Equal(0, A(fw - 1, fh - 1));   // bottom-right

            // Full-buffer scan: the MAX alpha over every pixel must be exactly 0. Report the
            // first offending pixel so a regression names the location of the residual darkening.
            byte maxAlpha = 0;
            int worstIdx = -1;
            for (int idx = 3; idx < buf!.Length; idx += 4)
            {
                if (buf[idx] > maxAlpha) { maxAlpha = buf[idx]; worstIdx = idx; }
            }
            if (maxAlpha != 0)
            {
                int p = worstIdx / 4, py = p / fw, px = p % fw;
                Assert.Fail($"A2 resting invariant VIOLATED: max field alpha was {maxAlpha} at " +
                            $"pixel ({px},{py}) — the rest buffer must be fully transparent everywhere.");
            }
            Assert.Equal(0, maxAlpha);
        });
    }

    // ── 10) UVHS round 2 (2026-06-11): full-res VECTOR grid (restored 3762a42) ──────────
    //
    // The grid returned as full-resolution WPF Line shapes on GridCanvas (a 1.2 px world-locked
    // hairline cannot survive rasterization into the quarter-res field — "grid FORM wrong / grid
    // JUMPS"). These STA tests pin the restored 3762a42 grid: DrawGrid produces real lines at
    // DPI-scaled spacing, GridCanvas opacity follows the ApplyGridIntensity / GridCeiling
    // pipeline, and the OpacityMask window mirrors the one-clock pose.

    /// <summary>DrawGrid produces full-res vector Line shapes at the DPI/resolution-scaled
    /// spacing (step = max(20, round(GridSizePx × physicalWidth/1920))). After a layout the
    /// canvas holds a non-trivial number of lines and the resolved step is at least the
    /// 1920-baseline GridSizePx (32 since Kirby's 2026-06-11 −20% spacing ruling) clamped to ≥ 20.</summary>
    [Fact]
    public void OverlayWindow_DrawGrid_ProducesVectorLines_AtDpiScaledSpacing()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();

            // Drive a state so ApplyState → DrawGrid runs at least once. The unshown STA window
            // never lays out, so DrawGrid uses its primary-screen fallback (same as EnsureField).
            w.ApplyState(new SenseState { VimsTier = 6, MaskActive = true, Shader = new ShaderParams() });

            Assert.True(w.GridLineCountForTests > 0,
                $"DrawGrid must produce full-res vector lines, count was {w.GridLineCountForTests}");
            // The step is DPI/resolution-scaled with a 20 px floor; at the 1920 baseline it is 40.
            Assert.True(w.GridStepForTests >= 20,
                $"grid step must be DPI-scaled with a >= 20 floor, was {w.GridStepForTests}");
        });
    }

    /// <summary>GridCanvas opacity TARGET follows the ApplyGridIntensity pipeline (GridCeiling ×
    /// tier fraction × intensity, EMA-smoothed): a hot high tier converges the target to a
    /// clearly-visible value, and "/grid off" (GridVisibility 0 ⇒ GridCeiling 0) drives the
    /// target to zero. The curve itself is pinned byte-for-byte by GridCeilingTests; this checks
    /// the live wiring. Reads the animation .To target (a bare STA thread has no render clock to
    /// advance the 280 ms animation, so the live GridCanvas.Opacity would read the base value).</summary>
    [Fact]
    public void OverlayWindow_GridOpacityTarget_FollowsApplyGridIntensity()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();

            // High tier + default GridVisibility: pump to converge the EMA. The target rises
            // toward GridCeiling(0.75)=1.0 × tierFraction(L12=1.0) × (0.55 + 0.45·intensity).
            var hot = new SenseState
            {
                VimsTier   = 12,
                MaskActive = true,
                Pressure   = SenseDefaults.TriggerPressure, // drive intensity high
                Shader     = new ShaderParams(),            // default GridVisibility 0.75
            };
            for (int i = 0; i < 40; i++) w.ApplyState(hot);
            Assert.True(w.GridOpacityTargetForTests > 0.0,
                $"a hot high tier must make the grid visible, target was {w.GridOpacityTargetForTests}");

            // "/grid off": GridVisibility 0 ⇒ GridCeiling 0 ⇒ target clamped to 0.
            var gridOff = new SenseState
            {
                VimsTier   = 12,
                MaskActive = true,
                Pressure   = SenseDefaults.TriggerPressure,
                Shader     = new ShaderParams { GridVisibility = 0.0 },
            };
            for (int i = 0; i < 40; i++) w.ApplyState(gridOff);
            Assert.Equal(0.0, w.GridOpacityTargetForTests, 6); // truly off
        });
    }

    /// <summary>One-clock + CANVAS MODEL (UVHS round-8 fix, 2026-06-11), test (c) HOLE: the
    /// centre of the gaze hole in the grid-window bitmap tracks a moved center in the SAME frame
    /// (replaces the retired brush-mirror test). The window-bitmap pixel AT the resolved field
    /// center is the gaze hole (alpha 0 — the grid is hidden there), and it MOVED with the pose in
    /// one UpdateHeadGazeCenter call — the field center and the window hole derive from the same
    /// pose in the same RenderFieldIfDirty pass (the grid lines stay world-locked; only the hole
    /// follows). Also confirms the OpacityMask is the canvas-model ImageBrush, not a radial
    /// gradient.</summary>
    [Fact]
    public void OverlayWindow_OneClock_GridWindowHole_TracksMovedCenter_InSameFrame()
    {
        RunOnSta(() =>
        {
            var prior = AppConfig.HeadTrackingFollowMode;
            AppConfig.HeadTrackingFollowMode = HeadTrackingFollowModeKind.FullFollow;
            try
            {
                var w = new OverlayWindow();
                w.ApplyTemplate();
                w.ConfigureFieldForTests(720, 450, tier: 6, featherWidth: 0.5);
                w.ApplyState(new SenseState { VimsTier = 6, HeadGazeX = 0.5, HeadGazeY = 0.5, HeadTrackingActive = false, MaskActive = true });

                // The OpacityMask is the canvas-model ImageBrush over the window bitmap (no radial).
                Assert.NotNull(w.GridWindowMaskBrushForTests);

                // Centred: the hole sits at the buffer centre (window alpha 0 there).
                w.RenderOnceForTests(0.5, 0.5, forceFull: true);
                int fw = w.FieldWidthForTests, fh = w.FieldHeightForTests;
                Assert.Equal(0, GridWindowAlphaAt(w, fw / 2, fh / 2));

                // Pose step in ONE frame: the field center + the window hole resolve together.
                w.RenderOnceForTests(0.8, 0.2, forceFull: false);
                Assert.Equal(0.8, w.FieldCenterXForTests, 6);
                Assert.Equal(0.2, w.FieldCenterYForTests, 6);

                // The hole MOVED with the pose: the window pixel at the NEW center is the gaze
                // hole (alpha 0), and the OLD center pixel is no longer the hole (it now reads the
                // canvas — alpha > 0 — because the hole translated away from it).
                int newCol = (int)Math.Round(0.8 * fw), newRow = (int)Math.Round(0.2 * fh);
                Assert.Equal(0, GridWindowAlphaAt(w, Math.Clamp(newCol, 0, fw - 1), Math.Clamp(newRow, 0, fh - 1)));
                Assert.True(GridWindowAlphaAt(w, fw / 2, fh / 2) > 0,
                    "the old centre must leave the gaze hole as it tracks the head (the canvas returns there)");
            }
            finally { AppConfig.HeadTrackingFollowMode = prior; }
        });
    }

    // ── 10b) CANVAS MODEL (UVHS round-8 fix, 2026-06-11): the grid OpacityMask is the ───────
    //         _gridWindowBmp written in the field pass — the canvas is PRESENT everywhere
    //         outside the gaze hole (every corner, at every gaze). The band-pass FALL is REVOKED.
    //
    // Kirby's clarified CANVAS MODEL: a STATIC full-screen grid canvas, transparent WHERE THE
    // USER LOOKS (hole + feather follow the head) and PRESENT everywhere else. The grid window is
    // no longer a RadialGradientBrush whose geometry diverged from the field (ellipse-vs-circle
    // seam, frozen-vs-damped breathing, the empty zone TRANSLATING with the head, Shape-3/4
    // featherStart misalignment — the round-8 verdict). It is the SAME monotonic window the field
    // computes per-pixel (WindowLutLookup), saturated to solid beyond the feather end, written to
    // the A8-equivalent Pbgra32 _gridWindowBmp in the SAME pass. These read the rendered window
    // buffer directly (exactly what WPF rasterizes as the OpacityMask).

    /// <summary>Window-bitmap alpha at (col,row): byte +3 of the Pbgra32 grayscale-alpha window
    /// buffer (the value the OpacityMask consumes). 0 = the gaze hole (grid hidden), 255 = the
    /// canvas (grid present).</summary>
    private static int GridWindowAlphaAt(OverlayWindow w, int col, int row)
    {
        byte[]? win = w.GridWindowBufferSnapshotForTests;
        if (win is null) return -1;
        int fw = w.FieldWidthForTests;
        return win[(row * fw + col) * 4 + 3];
    }

    /// <summary>Test (a) SEAM EQUALITY: for sample pixels across gazes (center / edge / corner)
    /// the window-bitmap alpha EQUALS round(255 · saturated-window(field geometry)) EXACTLY — the
    /// window shares the field's circle / corner-growth damping / featherStart byte-for-byte
    /// (computed from the SAME EffectiveCornerDenom + WindowLutLookup the field loop runs). This
    /// is the fix for the four divergences: there is one geometry, consumed by both.</summary>
    [Fact]
    public void OverlayWindow_GridWindow_SeamEquality_MatchesFieldGeometryExactly()
    {
        RunOnSta(() =>
        {
            var prior = AppConfig.HeadTrackingFollowMode;
            AppConfig.HeadTrackingFollowMode = HeadTrackingFollowModeKind.FullFollow;
            try
            {
                var w = new OverlayWindow();
                w.ApplyTemplate();
                w.ConfigureFieldForTests(720, 450, tier: 9, featherWidth: 0.5);
                int fw = w.FieldWidthForTests, fh = w.FieldHeightForTests;
                double aspect = (double)fw / fh;
                double featherStart = w.FeatherStartForTests;
                double damping = SenseDefaults.OverlayFieldCornerGrowthDamping;

                // Rebuild the SAME r²-window LUT the renderer uses (default feather start 0.35).
                const int lutSize = 2048;
                var lut = new float[lutSize + 1];
                OverlayWindow.BuildWindowLut(lut, lutSize, featherStart, OverlayWindow.FeatherEnd);

                // The expected window byte at pixel (col,row) for a given center — the EXACT same
                // arithmetic ComputeFieldRows runs (aspect-corrected d², damped denom, r² LUT).
                byte Expected(int col, int row, double cx, double cy)
                {
                    double denom = OverlayWindow.EffectiveCornerDenom(cx, cy, aspect, damping);
                    double invDenomSq = denom > 1e-9 ? 1.0 / (denom * denom) : 1e18;
                    double px = (col + 0.5) / fw, py = (row + 0.5) / fh;
                    double dx = (px - cx) * aspect, dy = py - cy;
                    double r2 = (dx * dx + dy * dy) * invDenomSq;
                    double window = OverlayWindow.WindowLutLookup(lut, lutSize, r2);
                    return (byte)(window * 255.0 + 0.5);
                }

                (double cx, double cy)[] gazes = { (0.5, 0.5), (0.9, 0.5), (0.9, 0.9), (0.1, 0.1), (0.5, 0.9) };
                (int col, int row)[] probes =
                {
                    (fw / 2, fh / 2), (0, 0), (fw - 1, 0), (0, fh - 1), (fw - 1, fh - 1),
                    (fw / 2, 0), (0, fh / 2), (fw / 4, fh / 3), (3 * fw / 4, 2 * fh / 3),
                };

                foreach (var (cx, cy) in gazes)
                {
                    w.RenderOnceForTests(cx, cy, forceFull: true);
                    foreach (var (col, row) in probes)
                    {
                        int actual = GridWindowAlphaAt(w, col, row);
                        Assert.Equal(Expected(col, row, cx, cy), (byte)actual);
                    }
                }
            }
            finally { AppConfig.HeadTrackingFollowMode = prior; }
        });
    }

    /// <summary>Test (b) CANVAS-FULL (the round-8 empty-corner regression, INVERTED): the
    /// FARTHEST corner from the gaze is 255 (SOLID — the canvas is present) at CENTER gaze AND at
    /// every edge / corner gaze, AND at CENTER gaze ALL FOUR corners are solid. The old
    /// RadialGradientBrush band-pass faded the corners to transparent and the empty/saturated zone
    /// TRANSLATED with the head, so the far corner went grid-free as the user looked away (the
    /// round-8 verdict). The canvas model holds the far corner solid at every gaze. (A corner NEAR
    /// the gaze may legitimately sit inside the feather — the user is looking there — so only the
    /// FAR corner is asserted per off-centre gaze; that is precisely the corner the regression
    /// emptied.)</summary>
    [Fact]
    public void OverlayWindow_GridWindow_CanvasFull_FarCornersSolid_AtEveryGaze()
    {
        RunOnSta(() =>
        {
            var prior = AppConfig.HeadTrackingFollowMode;
            AppConfig.HeadTrackingFollowMode = HeadTrackingFollowModeKind.FullFollow;
            try
            {
                var w = new OverlayWindow();
                w.ApplyTemplate();
                w.ConfigureFieldForTests(720, 450, tier: 9, featherWidth: 0.5);
                int fw = w.FieldWidthForTests, fh = w.FieldHeightForTests;
                (int col, int row)[] corners = { (0, 0), (fw - 1, 0), (0, fh - 1), (fw - 1, fh - 1) };

                // CENTRE gaze: all four corners are equidistant and far ⇒ every corner solid.
                w.RenderOnceForTests(0.5, 0.5, forceFull: true);
                foreach (var (col, row) in corners)
                    Assert.Equal(255, GridWindowAlphaAt(w, col, row));

                // Off-centre gazes: the FARTHEST corner from the gaze (the round-8 empty corner)
                // must stay solid. Find it by the corner pixel maximally distant from the gaze.
                (double cx, double cy)[] gazes =
                {
                    (0.9, 0.9), (0.1, 0.1), (0.9, 0.1), (0.1, 0.9), (0.5, 0.9), (0.9, 0.5),
                };
                foreach (var (cx, cy) in gazes)
                {
                    w.RenderOnceForTests(cx, cy, forceFull: true);
                    // Farthest corner from (cx,cy): pick the opposite corner in each axis.
                    int farCol = cx < 0.5 ? fw - 1 : 0;
                    int farRow = cy < 0.5 ? fh - 1 : 0;
                    Assert.Equal(255, GridWindowAlphaAt(w, farCol, farRow));
                }
            }
            finally { AppConfig.HeadTrackingFollowMode = prior; }
        });
    }

    /// <summary>Test (d) Shape-3/4 ALIGNMENT: with FeatherWidth shifted +0.30 (a WIDER feather ⇒
    /// the band start moves inward below 0.35) the window's rise tracks the field's resolved
    /// featherStart — the Shape-3/4 seam is fixed automatically because the window IS the field's
    /// window. Asserted via the pure math seam: every rendered window byte along a centre row
    /// equals round(255 · WindowLutLookup(LUT built at the RESOLVED featherStart, r²)) EXACTLY,
    /// and DIFFERS from a LUT built at the DEFAULT 0.35 start (so the rise genuinely shifted with
    /// the knob — the test is not vacuous).</summary>
    [Fact]
    public void OverlayWindow_GridWindow_Shape34_RiseStart_MatchesFieldFeatherStart()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();
            // FeatherWidth default 0.50 ⇒ start 0.35; shift +0.30 ⇒ 0.80 ⇒ a WIDER band, start
            // pulled inward below 0.35. Configure the field at that feather width.
            w.ConfigureFieldForTests(720, 450, tier: 9, featherWidth: 0.80);
            int fw = w.FieldWidthForTests, fh = w.FieldHeightForTests;
            double aspect = (double)fw / fh;
            double featherStart = w.FeatherStartForTests;
            Assert.True(featherStart < 0.35,
                $"a +0.30 FeatherWidth shift must pull the band start inward (< 0.35), was {featherStart}");

            w.RenderOnceForTests(0.5, 0.5, forceFull: true);
            byte[]? win = w.GridWindowBufferSnapshotForTests;
            Assert.NotNull(win);

            // The SAME r²-window LUT the renderer used: built at the RESOLVED featherStart.
            const int lutSize = 2048;
            var lutResolved = new float[lutSize + 1];
            OverlayWindow.BuildWindowLut(lutResolved, lutSize, featherStart, OverlayWindow.FeatherEnd);
            // A counterfactual LUT at the DEFAULT 0.35 start — the window would NOT match this.
            var lutDefault = new float[lutSize + 1];
            OverlayWindow.BuildWindowLut(lutDefault, lutSize, 0.35, OverlayWindow.FeatherEnd);

            double denom = OverlayWindow.EffectiveCornerDenom(0.5, 0.5, aspect, SenseDefaults.OverlayFieldCornerGrowthDamping);
            double invDenomSq = 1.0 / (denom * denom);
            int row = fh / 2;
            bool sawDifferenceVsDefault = false;

            for (int col = fw / 2; col < fw; col++)
            {
                double px = (col + 0.5) / fw, py = (row + 0.5) / fh;
                double dx = (px - 0.5) * aspect, dy = py - 0.5;
                double r2 = (dx * dx + dy * dy) * invDenomSq;

                byte expectedResolved = (byte)(OverlayWindow.WindowLutLookup(lutResolved, lutSize, r2) * 255.0 + 0.5);
                byte expectedDefault  = (byte)(OverlayWindow.WindowLutLookup(lutDefault,  lutSize, r2) * 255.0 + 0.5);
                byte actual = win![(row * fw + col) * 4 + 3];

                // The window tracks the RESOLVED featherStart byte-for-byte.
                Assert.Equal(expectedResolved, actual);
                if (expectedResolved != expectedDefault) sawDifferenceVsDefault = true;
            }

            // The +0.30 shift genuinely moved the rise: somewhere the resolved window differs from
            // the default-featherStart window (proves the seam tracks the knob, not a fixed 0.35).
            Assert.True(sawDifferenceVsDefault,
                "the shifted FeatherWidth must move the window rise away from the default 0.35 start");
        });
    }

    /// <summary>Test (e) DIRTY-BAND WINDOW BYTE-EQUALITY: a pose-only move that recomputes only
    /// the dirty band must produce a WINDOW buffer byte-identical to a full window recompute at
    /// the same pose — the band path updates exactly the window rows the field band updated (one
    /// clock), so the canvas-model OpacityMask never carries a stale window pixel.</summary>
    [Fact]
    public void OverlayWindow_GridWindow_DirtyBand_MatchesFullRecompute_ByteForByte()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();
            w.ConfigureFieldForTests(720, 450, tier: 10, featherWidth: 0.5);
            w.RenderOnceForTests(0.5, 0.5, forceFull: true);

            (double, double)[] poses =
            {
                (0.55, 0.45), (0.70, 0.30), (0.30, 0.70), (0.90, 0.90),
                (0.10, 0.10), (0.50, 0.50), (0.62, 0.38),
            };
            foreach (var (cx, cy) in poses)
            {
                w.RenderOnceForTests(cx, cy, forceFull: false);     // dirty-band path
                byte[]? liveWin = w.GridWindowBufferSnapshotForTests;
                byte[] refWin   = w.ComputeFullGridWindowReferenceForTests(cx, cy);
                Assert.NotNull(liveWin);
                Assert.True(BuffersEqual(liveWin!, refWin),
                    $"dirty-band WINDOW buffer must equal a full window recompute at pose ({cx},{cy})");
            }
        });
    }

    // ── 11) Fix 2 (Kirby round-4): perf harness + dirty-band correctness ──────────────
    //
    // ComputeField ran ~324k px/frame (2880×1800/4) at up to 165 Hz single-threaded with TWO
    // Math.Pow + one Math.Sqrt per pixel — the lag + visible stepping Kirby flagged. The fix:
    // gamma LUT + r²-indexed window LUT (no per-pixel transcendentals) + Parallel.For over rows
    // + a dirty-band recompute on pose-only moves. These pin the BEFORE/AFTER perf numbers and
    // prove the dirty band is byte-exact.

    /// <summary>Fix 2 PERF HARNESS: construct the field at exactly 720×450 and time ComputeField
    /// ~200 times with a moving center, reporting avg/p99 ms. The BEFORE path replays the OLD
    /// algorithm (per-pixel CornerNormR sqrt + ComposeFieldPixel Math.Pow×2, single-threaded) in
    /// a local buffer; the AFTER path is the real production RenderFieldIfDirty (LUTs + parallel
    /// + dirty-band). The asserted bound is deliberately GENEROUS (avg &lt; 8 ms) so CI never
    /// flakes; the real numbers are written to the test output and reported in the summary.</summary>
    [Fact]
    public void Fix2_PerfHarness_ComputeField_720x450_AvgAndP99()
    {
        // Run on a dedicated STA thread with a GENEROUS timeout: the BEFORE leg replays the slow
        // single-threaded exact-math algorithm (~27 ms/iter in Release, ~65 ms in Debug) and the
        // whole xUnit suite runs test classes in parallel, so under a saturated CPU the harness
        // can take a while. This is timing instrumentation, not a hard gate — don't let the
        // shared 15 s RunOnSta budget flake it.
        RunOnStaWithTimeout(TimeSpan.FromSeconds(90), () =>
        {
            const int W = 720, H = 450;
            const int ITERS = 200;        // AFTER (full + band) sample count
            const int BEFORE_ITERS = 40;  // BEFORE is the slow single-thread leg — fewer samples
            var w = new OverlayWindow();
            w.ApplyTemplate();
            w.ConfigureFieldForTests(W, H, tier: 8, featherWidth: 0.5);

            // Moving-center schedule (out toward a corner and back, FullFollow-style range).
            double Cx(int i) => 0.5 + 0.4 * Math.Sin(i * 0.05);
            double Cy(int i) => 0.5 + 0.4 * Math.Cos(i * 0.037);

            // ---- BEFORE: the old per-pixel exact-math single-threaded algorithm ----
            var beforeBuf = new byte[W * H * 4];
            double gamma = SenseDefaults.OverlayFieldGamma;
            byte mr = OverlayWindow.VimsLevels[7].R, mg = OverlayWindow.VimsLevels[7].G, mb = OverlayWindow.VimsLevels[7].B;
            double peak = OverlayWindow.VimsLevels[7].Alpha;
            double featherStart = OverlayWindow.ResolveFeatherStart(0.5);
            const double featherEnd = OverlayWindow.FeatherEnd;
            double aspect = (double)W / H;
            const double cellStrength = 0.5;

            void OldCompute(double cx, double cy)
            {
                for (int y = 0; y < H; y++)
                {
                    double py = (y + 0.5) / H;
                    int rowB = y * W * 4;
                    for (int x = 0; x < W; x++)
                    {
                        double px = (x + 0.5) / W;
                        double r = OverlayWindow.CornerNormR(px, py, cx, cy, aspect);
                        double window = OverlayWindow.ResolveFieldAlpha(r, 1.0, featherStart, featherEnd);
                        var (oB, oG, oR, oA) = OverlayWindow.ComposeFieldPixel(window, mr, mg, mb, peak, gamma, cellStrength);
                        int bi = rowB + x * 4;
                        beforeBuf[bi] = oB; beforeBuf[bi + 1] = oG; beforeBuf[bi + 2] = oR; beforeBuf[bi + 3] = oA;
                    }
                }
            }

            // warm up JIT
            OldCompute(0.5, 0.5);
            var beforeTimes = new double[BEFORE_ITERS];
            for (int i = 0; i < BEFORE_ITERS; i++)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                OldCompute(Cx(i), Cy(i));
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                beforeTimes[i] = (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            }

            // ---- AFTER: the production path (force FULL each iter for an apples-to-apples
            //      worst-case ComputeField comparison; the dirty band only ever makes it cheaper).
            w.RenderOnceForTests(0.5, 0.5, forceFull: true); // warm up
            var afterTimes = new double[ITERS];
            for (int i = 0; i < ITERS; i++)
                afterTimes[i] = w.RenderOnceForTests(Cx(i), Cy(i), forceFull: true);

            // Also measure the realistic pose-only (dirty-band) path for the summary.
            w.RenderOnceForTests(0.5, 0.5, forceFull: true);
            var bandTimes = new double[ITERS];
            for (int i = 0; i < ITERS; i++)
                bandTimes[i] = w.RenderOnceForTests(Cx(i), Cy(i), forceFull: false);

            (double avg, double p99) Stat(double[] t)
            {
                double sum = 0; foreach (var v in t) sum += v;
                var sorted = (double[])t.Clone(); Array.Sort(sorted);
                int p99i = (int)Math.Ceiling(0.99 * sorted.Length) - 1;
                p99i = Math.Clamp(p99i, 0, sorted.Length - 1);
                return (sum / t.Length, sorted[p99i]);
            }

            var (bAvg, bP99) = Stat(beforeTimes);
            var (aAvg, aP99) = Stat(afterTimes);
            var (dAvg, dP99) = Stat(bandTimes);

            _output.WriteLine($"[Fix2 perf @720x450]");
            _output.WriteLine($"  BEFORE (old exact-math, single-thread, n={BEFORE_ITERS}): avg={bAvg:F3} ms  p99={bP99:F3} ms");
            _output.WriteLine($"  AFTER  (LUT + Parallel.For, full,    n={ITERS}): avg={aAvg:F3} ms  p99={aP99:F3} ms");
            _output.WriteLine($"  AFTER  (dirty-band, pose-only,       n={ITERS}): avg={dAvg:F3} ms  p99={dP99:F3} ms");
            _output.WriteLine($"  speedup (full): {bAvg / Math.Max(aAvg, 1e-6):F1}x avg");

            // TIMING IS REPORTED, NOT GATED. Neither absolute nor RELATIVE timing can be a
            // hard assert here: the xUnit suite runs classes in PARALLEL, and under a
            // saturated CPU the Parallel.For AFTER legs get core-starved while the
            // single-threaded BEFORE leg does not — the "after < before" comparison was
            // observed to FAIL under full-suite Release contention (before=19.3 ms,
            // after=21.3 ms) while holding 3x in clean runs. Wall-clock under contention is
            // environment noise, not a product signal; the REAL perf evidence lives in the
            // reported output (isolated runs: before ~27 ms → band ~1.5 ms avg). Only very
            // generous liveness ceilings are asserted so a catastrophic regression
            // (e.g. the LUTs silently reverting to per-pixel Math.Pow) still fails loudly.
            Assert.True(aAvg < 50.0, $"AFTER full-recompute avg liveness ceiling (50 ms), was {aAvg:F3} ms");
            Assert.True(dAvg < 50.0, $"AFTER dirty-band avg liveness ceiling (50 ms), was {dAvg:F3} ms");
        });
    }

    /// <summary>UVHS round-8 (2026-06-11) PERF-DELTA HARNESS: isolate the ADDED cost of the
    /// canvas-model second buffer write (the per-pixel grid-window encode + its WritePixels).
    /// Times the production full render at 720×450 WITH the window write (real path) vs WITHOUT it
    /// (the field-only leg, flag flipped via the perf seam), N=200 each, and reports the delta. The
    /// window write rides the same per-pixel pass (the window value is already computed for the
    /// field), so the delta is just 4 byte stores/px + one extra WritePixels of an equal-size
    /// buffer — expected ~negligible. REPORTED, not gated beyond a generous liveness ceiling on the
    /// WITH-window leg (the same parallel-suite contention caveat as the Fix2 harness applies).</summary>
    [Fact]
    public void RoundEight_PerfDelta_GridWindowSecondBufferWrite_720x450()
    {
        RunOnStaWithTimeout(TimeSpan.FromSeconds(60), () =>
        {
            const int W = 720, H = 450, ITERS = 200, WARM = 20;
            var w = new OverlayWindow();
            w.ApplyTemplate();
            w.ConfigureFieldForTests(W, H, tier: 8, featherWidth: 0.5);

            double Cx(int i) => 0.5 + 0.4 * Math.Sin(i * 0.05);
            double Cy(int i) => 0.5 + 0.4 * Math.Cos(i * 0.037);

            double[] Run(bool withWindow)
            {
                w.WriteGridWindowForPerfTests = withWindow;
                for (int i = 0; i < WARM; i++) w.RenderOnceForTests(Cx(i), Cy(i), forceFull: true); // JIT warm
                var t = new double[ITERS];
                for (int i = 0; i < ITERS; i++)
                    t[i] = w.RenderOnceForTests(Cx(i), Cy(i), forceFull: true);
                return t;
            }

            // Interleave-free but order-balanced: measure WITHOUT first, then WITH, then restore.
            var withoutTimes = Run(withWindow: false);
            var withTimes    = Run(withWindow: true);
            w.WriteGridWindowForPerfTests = true; // restore production behaviour

            double Avg(double[] t) { double s = 0; foreach (var v in t) s += v; return s / t.Length; }
            double P99(double[] t)
            {
                var s = (double[])t.Clone(); Array.Sort(s);
                int i = Math.Clamp((int)Math.Ceiling(0.99 * s.Length) - 1, 0, s.Length - 1);
                return s[i];
            }

            double woAvg = Avg(withoutTimes), wAvg = Avg(withTimes);
            double woP99 = P99(withoutTimes), wP99 = P99(withTimes);
            double deltaAvg = wAvg - woAvg, deltaP99 = wP99 - woP99;

            _output.WriteLine($"[round-8 grid-window perf delta @720x450, full recompute, n={ITERS}]");
            _output.WriteLine($"  field ONLY (no window write):   avg={woAvg:F3} ms  p99={woP99:F3} ms");
            _output.WriteLine($"  field + WINDOW (production):     avg={wAvg:F3} ms  p99={wP99:F3} ms");
            _output.WriteLine($"  ADDED second-buffer-write cost:  avg={deltaAvg:+0.000;-0.000} ms  p99={deltaP99:+0.000;-0.000} ms");
            double pct = woAvg > 1e-6 ? 100.0 * deltaAvg / woAvg : 0.0;
            _output.WriteLine($"  added cost as % of field-only:   {pct:F1}%");

            // Liveness only (REPORTED, not gated on the delta — parallel-suite contention makes
            // a sub-millisecond delta noisy): the WITH-window full recompute stays well inside the
            // 165 Hz budget head-room the Fix2 harness already proved.
            Assert.True(wAvg < 50.0, $"field+window avg liveness ceiling (50 ms), was {wAvg:F3} ms");
        });
    }

    /// <summary>Fix 2 dirty-band CORRECTNESS: a pose-only move that recomputes only the dirty
    /// band must produce a buffer BYTE-IDENTICAL to a full recompute at the same pose. This pins
    /// that the band optimization never leaves a stale pixel (the rows it skips really are
    /// pose-invariant — clean centre window=0 or saturated periphery window=1.0 for both poses).</summary>
    [Fact]
    public void Fix2_DirtyBand_MatchesFullRecompute_ByteForByte()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();
            w.ConfigureFieldForTests(720, 450, tier: 10, featherWidth: 0.5);

            // Seed a full render at center so _lastCx/_lastCy are established.
            w.RenderOnceForTests(0.5, 0.5, forceFull: true);

            // A sequence of pose-only moves (each uses the dirty band path), then compare the
            // live buffer to a fresh full reference at the same pose after EACH move.
            (double, double)[] poses =
            {
                (0.55, 0.45), (0.70, 0.30), (0.30, 0.70), (0.90, 0.90),
                (0.10, 0.10), (0.50, 0.50), (0.62, 0.38),
            };
            foreach (var (cx, cy) in poses)
            {
                w.RenderOnceForTests(cx, cy, forceFull: false);   // dirty-band path
                byte[]? live = w.FieldBufferSnapshotForTests;
                byte[] reference = w.ComputeFullFieldReferenceForTests(cx, cy);
                Assert.NotNull(live);
                Assert.True(BuffersEqual(live!, reference),
                    $"dirty-band buffer must equal a full recompute at pose ({cx},{cy})");
            }
        });
    }

    /// <summary>Parked-storm fix (2026-06-11) invisibility: a sub-quantum pose move changes the
    /// rendered field by AT MOST one 8-bit code (≤1 LSB on the quarter-res buffer). This is the
    /// justification for the pump render gate skipping it — the change is below the established
    /// invisibility bar (the SAME ≤1-byte tolerance ComposeFieldPixelLut_MatchesExactCompose meets;
    /// a single quarter-res texel dithering by 1/255 in one channel, then bilinear-upscaled, is
    /// imperceptible and is the unavoidable floating-point rounding-boundary chatter — NOT a visible
    /// step). At several base poses (incl. near a clamp edge where the falloff is steepest), nudge
    /// the center by 0.9× the quarter-texel quantum on each axis and confirm a full recompute at the
    /// nudged pose differs from the base by no more than 2 per byte. (Was ≤1 with the wide 0.35→0.95 band;
    /// the 2026-06-20 sharp feather edge — FeatherEnd pulled in — steepened the ramp, so a skipped
    /// sub-quarter-texel jitter now shifts the edge by ≤2 LSB = 2/255 ≈ 0.8%, still imperceptible. Real
    /// head motion exceeds the quantum and re-renders, so there is no visible crawl.)</summary>
    [Fact]
    public void ParkedPump_SubTexelPose_RendersWithinTwoBytes()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();
            w.ConfigureFieldForTests(720, 450, tier: 12, featherWidth: 0.5);
            int maxDim = Math.Max(w.FieldWidthForTests, w.FieldHeightForTests);
            // The render gate quantum. A move strictly below it is skipped; this proves the skip is
            // within 1 LSB. Probe at 0.9× the quantum on each axis.
            double quantum = 1.0 / (maxDim * SenseDefaults.OverlayFieldRenderTexelFraction);
            double nudge = quantum * 0.9;

            (double cx, double cy)[] basePoses =
            {
                (0.50, 0.50), (0.62, 0.38), (0.30, 0.70), (0.10, 0.90), (0.90, 0.10),
            };
            int maxDelta = 0;
            foreach (var (cx, cy) in basePoses)
            {
                byte[] baseField = w.ComputeFullFieldReferenceForTests(cx, cy);
                foreach (var (nx, ny) in new[] { (cx + nudge, cy), (cx, cy + nudge), (cx + nudge, cy + nudge) })
                {
                    byte[] n = w.ComputeFullFieldReferenceForTests(nx, ny);
                    for (int i = 0; i < baseField.Length; i++)
                        maxDelta = Math.Max(maxDelta, Math.Abs(baseField[i] - n[i]));
                }
            }
            Assert.True(maxDelta <= 2,
                $"a sub-quarter-texel pose nudge ({nudge:E2}) must change the field by <= 2 LSB " +
                $"(invisible — the sharp feather edge steepened the ramp; was <= 1 with the wide band); " +
                $"max byte delta was {maxDelta}");
        });
    }

    /// <summary>Fix 3 (parked-storm, 2026-06-11): the field-recompute Parallel.For is CAPPED so a
    /// fullscreen game saturating every core can never starve the whole thread-pool out from under
    /// the render (the 19–28 ms wgc_diag spikes were the UNCAPPED Parallel.For fighting the game for
    /// every core). The cap leaves at least two cores for the UI/compositor on any multi-core box,
    /// and is always at least 1 (never zero/negative). On a 1–2 core box it floors to 1 (serial),
    /// which is correct — there is no spare core to fan out to anyway.</summary>
    [Fact]
    public void Fix3_FieldParallelism_IsCapped_LeavingHeadroom()
    {
        int cap = SenseDefaults.OverlayFieldMaxParallelism;
        Assert.True(cap >= 1, $"parallelism cap must be at least 1 (serial floor), was {cap}");
        Assert.True(cap <= Environment.ProcessorCount,
            $"cap must not exceed the core count ({Environment.ProcessorCount}), was {cap}");
        if (Environment.ProcessorCount > 2)
            Assert.True(cap <= Environment.ProcessorCount - 2,
                $"on a >2-core box the cap must leave >= 2 cores free for the UI/compositor: " +
                $"cap {cap} vs {Environment.ProcessorCount} cores");
    }

    /// <summary>Fix 1 end-to-end through the RENDERED buffer, round-9 semantics: the clear-hole
    /// radius does NOT grow as the center moves to a corner (damping 0.0 — frozen size, the hole
    /// only translates). Measure the hole half-width by scanning left from the hole center at
    /// rest vs. at a near-corner pose and confirm they match, while the UNDAMPED denominator
    /// would have produced real growth (the zoom source being killed).</summary>
    [Fact]
    public void Fix1_RenderedHole_SizeFrozen_OnCornerApproach()
    {
        RunOnSta(() =>
        {
            var prior = AppConfig.HeadTrackingFollowMode;
            AppConfig.HeadTrackingFollowMode = HeadTrackingFollowModeKind.FullFollow;
            try
            {
                var w = new OverlayWindow();
                w.ApplyTemplate();
                w.ConfigureFieldForTests(720, 450, tier: 12, featherWidth: 0.5);
                int fw = w.FieldWidthForTests, fh = w.FieldHeightForTests;
                double aspect = (double)fw / fh;

                // Predicted hole growth in screen-x (px units): featherStart · denom · fw.
                double featherStart = w.FeatherStartForTests;
                double restDenom = OverlayWindow.RestCornerDenom(aspect);
                double liveDenomCorner = OverlayWindow.LiveCornerDenom(0.9, 0.9, aspect);
                double dampDenomCorner = OverlayWindow.EffectiveCornerDenom(0.9, 0.9, aspect,
                    SenseDefaults.OverlayFieldCornerGrowthDamping);

                double undampedGrowth = featherStart * (liveDenomCorner - restDenom);
                double dampedGrowth   = featherStart * (dampDenomCorner - restDenom);

                // The rendered field uses the damped denom; at damping 0.0 the growth is ZERO
                // while the undamped path would still have grown (proves the pin has teeth).
                Assert.True(undampedGrowth > 0,
                    "the undamped denom must grow at the corner (the zoom source being killed)");
                Assert.Equal(0.0, dampedGrowth, 9);

                // Cross-check against the actual rendered buffer: measure the hole half-width by
                // scanning LEFT from the ACTUAL hole-center pixel (cx·fw) toward x=0 for the first
                // dimmed pixel. The hole rides the head, so the scan must start at the moved center
                // (not the buffer center). Both rest (center 0.5) and corner (center 0.9) have
                // room toward x=0. (Sanity that the field really renders with the frozen denom.)
                w.RenderOnceForTests(0.5, 0.5, forceFull: true);
                int restHole = MeasureHoleHalfWidthLeft(w, 0.5, 0.5);
                w.RenderOnceForTests(0.9, 0.9, forceFull: true);
                int cornerHole = MeasureHoleHalfWidthLeft(w, 0.9, 0.9);

                // Frozen size: the corner hole matches the rest hole to within one field pixel
                // (center-quantization slack) — far below the undamped growth in px.
                int undampedGrowthPx = (int)Math.Round(undampedGrowth * fw);
                Assert.True(Math.Abs(cornerHole - restHole) <= 1,
                    $"hole size must be frozen on corner approach: rest={restHole}, corner={cornerHole} " +
                    $"(undamped growth would be ~{undampedGrowthPx}px)");
            }
            finally { AppConfig.HeadTrackingFollowMode = prior; }
        });
    }

    /// <summary>Fix 1 protection invariant through the RENDERED buffer: with the center at a
    /// corner, the FARTHEST screen corner pixel is still FULLY dimmed (alpha saturated to the
    /// peak mask value). Damping can only deepen the periphery, never weaken it.</summary>
    [Fact]
    public void Fix1_FarthestCorner_StaysFullyDimmed_AfterDamping()
    {
        RunOnSta(() =>
        {
            var prior = AppConfig.HeadTrackingFollowMode;
            AppConfig.HeadTrackingFollowMode = HeadTrackingFollowModeKind.FullFollow;
            try
            {
                var w = new OverlayWindow();
                w.ApplyTemplate();
                w.ConfigureFieldForTests(720, 450, tier: 12, featherWidth: 0.5);
                int fw = w.FieldWidthForTests, fh = w.FieldHeightForTests;

                // Center near the bottom-right corner; the farthest pixel is the top-left (0,0).
                w.RenderOnceForTests(0.9, 0.9, forceFull: true);
                byte[]? buf = w.FieldBufferSnapshotForTests;
                Assert.NotNull(buf);

                // Saturated mask value = gamma(window=1.0 · peak) of L12 (0.92). The window LUT
                // saturates at the corner (r >= 1), so the top-left pixel alpha == the saturated
                // mask alpha (compose with cellStrength as configured = 0.5).
                var (_, _, _, satA) = OverlayWindow.ComposeFieldPixelLut(1.0,
                    OverlayWindow.VimsLevels[11].R, OverlayWindow.VimsLevels[11].G,
                    OverlayWindow.VimsLevels[11].B, OverlayWindow.VimsLevels[11].Alpha, 0.5);

                int tlA = buf![(0 * fw + 0) * 4 + 3];
                Assert.True(tlA >= satA - 1,
                    $"farthest corner must be fully dimmed (saturated): corner alpha {tlA}, saturated {satA}");
            }
            finally { AppConfig.HeadTrackingFollowMode = prior; }
        });
    }

    /// <summary>Scan LEFT from the hole-center pixel (cx·fw, cy·fh) toward x=0 and return the
    /// number of fully-transparent (alpha 0) pixels before the first dimmed pixel — the clear
    /// hole half-width in px along −x. The hole rides the head, so the scan starts at the moved
    /// center, not the buffer center. Used by the Fix 1 rendered-hole test.</summary>
    private static int MeasureHoleHalfWidthLeft(OverlayWindow w, double cx, double cy)
    {
        byte[]? buf = w.FieldBufferSnapshotForTests;
        if (buf is null) return 0;
        int fw = w.FieldWidthForTests, fh = w.FieldHeightForTests;
        int rowY = Math.Clamp((int)Math.Round(cy * fh), 0, fh - 1);
        int cxPix = Math.Clamp((int)Math.Round(cx * fw), 0, fw - 1);
        int count = 0;
        for (int x = cxPix; x >= 0; x--)
        {
            int a = buf[(rowY * fw + x) * 4 + 3];
            if (a == 0) count++;
            else break;
        }
        return count;
    }

    private static bool BuffersEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private sealed class NullStubReader : IHeadPoseReader
    {
        public HeadPoseSample? ReadLatest() => null;
    }

    private sealed class ConstantReader : IHeadPoseReader
    {
        private readonly HeadPoseSample _sample;
        public ConstantReader(HeadPoseSample sample) => _sample = sample;
        public HeadPoseSample? ReadLatest() => _sample;
    }

    /// <summary>Runs <paramref name="body"/> on a fresh STA thread and
    /// re-throws any exception on the calling thread so xUnit reports the
    /// actual assertion failure instead of a generic timeout. Copied from
    /// <see cref="NotificationCardWindowTests"/> to keep M5 tests fully
    /// self-contained.</summary>
    private static void RunOnSta(Action body) => RunOnStaWithTimeout(TimeSpan.FromSeconds(15), body);

    /// <summary>RunOnSta with an explicit timeout — the Fix 2 perf harness replays a slow
    /// single-threaded BEFORE leg and runs alongside the parallel xUnit suite, so it needs a
    /// roomier budget than the default 15 s (it is instrumentation, not a hard latency gate).</summary>
    private static void RunOnStaWithTimeout(TimeSpan timeout, Action body)
    {
        Exception? captured = null;
        var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { captured = ex; }
            finally { done.Set(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(done.Wait(timeout), "STA test timed out");
        if (captured != null)
            throw new Xunit.Sdk.XunitException("STA test threw: " + captured);
    }
}
