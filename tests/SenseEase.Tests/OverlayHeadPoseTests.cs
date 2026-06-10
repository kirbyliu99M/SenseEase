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

namespace SenseEase.Tests;

/// <summary>M5 (Overlay Integrator) gate tests. Pin the head-pose →
/// SenseState → OverlayWindow plumbing introduced in M5:
/// <list type="bullet">
///   <item>Flag-off + null-sample path leaves HeadGazeX/Y at center.</item>
///   <item>Reader-returns-sample path propagates Cx/Cy through SenseState.</item>
///   <item>MockSweepHeadPoseReader generates the expected 20-second pattern
///         at key timestamps.</item>
///   <item>OverlayWindow's <c>_fovBrush.Center</c> follows
///         <c>HeadGazeX/Y</c> when <c>HeadTrackingActive</c> is true.</item>
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

    // ── 5) OverlayWindow brush center follows HeadGaze ─────────────────────

    [Fact]
    public void OverlayWindow_BrushCenter_FollowsHeadGaze()
    {
        // STA test — mirrors NotificationCardWindowTests.RunOnSta pattern.
        // Construct OverlayWindow, apply a state with HeadTrackingActive=true
        // and HeadGazeX=0.6, then assert the brush center moved to that
        // point. The flag-off path is covered by the M3 invariant test
        // above plus the static literal-read in OverlayWindow.ApplyFovMask.
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();

            var state = new SenseState
            {
                VimsTier            = 3,        // mid tier so the brush actually allocates
                HeadGazeX           = 0.6,
                HeadGazeY           = 0.4,
                HeadTrackingActive  = true,
                MaskActive          = true,
            };
            w.ApplyState(state);

            var brush = w.FovBrushForTests;
            Assert.NotNull(brush);
            Assert.Equal(0.6, brush!.Center.X, 3);
            Assert.Equal(0.4, brush.Center.Y, 3);
            Assert.Equal(0.6, brush.GradientOrigin.X, 3);
            Assert.Equal(0.4, brush.GradientOrigin.Y, 3);

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

            Assert.Equal(0.5, brush.Center.X, 3);
            Assert.Equal(0.5, brush.Center.Y, 3);
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
                var brush = w.FovBrushForTests;
                Assert.NotNull(brush);
                Assert.Equal(0.90, brush!.Center.X, 3);
                Assert.Equal(0.90, brush.Center.Y, 3);

                // Other corner (0.0, 0.0) clamps to (0.10, 0.10).
                w.ApplyState(new SenseState
                {
                    VimsTier            = 3,
                    HeadGazeX           = 0.0,
                    HeadGazeY           = 0.0,
                    HeadTrackingActive  = true,
                    MaskActive          = true,
                });
                Assert.Equal(0.10, brush.Center.X, 3);
                Assert.Equal(0.10, brush.Center.Y, 3);

                // In-range value (0.55, 0.45) passes through unchanged.
                w.ApplyState(new SenseState
                {
                    VimsTier            = 3,
                    HeadGazeX           = 0.55,
                    HeadGazeY           = 0.45,
                    HeadTrackingActive  = true,
                    MaskActive          = true,
                });
                Assert.Equal(0.55, brush.Center.X, 3);
                Assert.Equal(0.45, brush.Center.Y, 3);
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
                var brush = w.FovBrushForTests;
                Assert.NotNull(brush);
                Assert.Equal(1.0, brush!.Center.X, 3);
                Assert.Equal(1.0, brush.Center.Y, 3);

                // Other corner.
                w.ApplyState(new SenseState
                {
                    VimsTier            = 3,
                    HeadGazeX           = 0.0,
                    HeadGazeY           = 0.0,
                    HeadTrackingActive  = true,
                    MaskActive          = true,
                });
                Assert.Equal(0.0, brush.Center.X, 3);
                Assert.Equal(0.0, brush.Center.Y, 3);

                // Out-of-range still clamped to [0, 1] for safety.
                w.ApplyState(new SenseState
                {
                    VimsTier            = 3,
                    HeadGazeX           = 1.5,
                    HeadGazeY           = -0.3,
                    HeadTrackingActive  = true,
                    MaskActive          = true,
                });
                Assert.Equal(1.0, brush.Center.X, 3);
                Assert.Equal(0.0, brush.Center.Y, 3);
            }
            finally
            {
                AppConfig.HeadTrackingFollowMode = prior;
            }
        });
    }

    /// <summary>M5 UX-iter 3 Fix B: BrushRadius dynamically scales to keep
    /// the farthest screen corner inside the gradient. At center=(0.5, 0.5)
    /// the farthest corner is at distance sqrt(0.5) ≈ 0.707; radius needed
    /// is 0.707 × 1.10 = 0.78, but capped at v0.9 baseline 0.85.
    /// At full corner center=(1, 1), opposite (0, 0) is at distance sqrt(2);
    /// radius = sqrt(2) × 1.10 = 1.556. v0.9 baseline (active=false) keeps
    /// the static 0.85.</summary>
    [Fact]
    public void OverlayWindow_BrushRadius_ScalesToFarthestCornerWhenActive()
    {
        // Exercise this in FullFollow so we can drive the brush.Center all
        // the way to (1, 1) and observe radius scaling against the farthest
        // corner. The dynamic-radius math itself is identical in Balanced
        // mode; just the cx/cy values would clamp before reaching it.
        RunOnSta(() =>
        {
            var prior = AppConfig.HeadTrackingFollowMode;
            AppConfig.HeadTrackingFollowMode = HeadTrackingFollowModeKind.FullFollow;
            try
            {
            var w = new OverlayWindow();
            w.ApplyTemplate();

            // Center: radius pinned to baseline 0.85 (computed need is below).
            var stateCenter = new SenseState
            {
                VimsTier            = 3,
                HeadGazeX           = 0.5,
                HeadGazeY           = 0.5,
                HeadTrackingActive  = true,
                MaskActive          = true,
            };
            w.ApplyState(stateCenter);
            var brush = w.FovBrushForTests;
            Assert.NotNull(brush);
            Assert.Equal(0.85, brush!.RadiusX, 3);

            // Corner: radius = sqrt(2) * 1.10 ≈ 1.556.
            var stateCorner = new SenseState
            {
                VimsTier            = 3,
                HeadGazeX           = 1.0,
                HeadGazeY           = 1.0,
                HeadTrackingActive  = true,
                MaskActive          = true,
            };
            w.ApplyState(stateCorner);
            double expected = Math.Sqrt(2.0) * 1.10;
            Assert.Equal(expected, brush.RadiusX, 3);
            Assert.Equal(expected, brush.RadiusY, 3);

            // Edge (1, 0.5): radius = sqrt(1 + 0.25) * 1.10 ≈ 1.230.
            var stateEdge = new SenseState
            {
                VimsTier            = 3,
                HeadGazeX           = 1.0,
                HeadGazeY           = 0.5,
                HeadTrackingActive  = true,
                MaskActive          = true,
            };
            w.ApplyState(stateEdge);
            double expectedEdge = Math.Sqrt(1.0 + 0.25) * 1.10;
            Assert.Equal(expectedEdge, brush.RadiusX, 3);

            // Flag-off: radius snaps back to v0.9 baseline 0.85.
            var stateOff = new SenseState
            {
                VimsTier            = 3,
                HeadGazeX           = 0.5,
                HeadGazeY           = 0.5,
                HeadTrackingActive  = false,
                MaskActive          = true,
            };
            w.ApplyState(stateOff);
            Assert.Equal(0.85, brush.RadiusX, 3);
            Assert.Equal(0.85, brush.RadiusY, 3);
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
            var brush = w.FovBrushForTests;
            Assert.NotNull(brush);

            // Now exercise the pump's entry point directly (dtMs = 0 → snap).
            w.UpdateHeadGazeCenter(headGazeX: 0.7, headGazeY: 0.3, active: true);
            Assert.Equal(0.7, brush!.Center.X, 3);
            Assert.Equal(0.3, brush.Center.Y, 3);
            // Radius math: farX = 0.7, farY = 0.7, farthest = sqrt(0.98) ≈ 0.99, × 1.10 ≈ 1.089.
            double expected = Math.Sqrt(0.49 + 0.49) * 1.10;
            Assert.Equal(expected, brush.RadiusX, 3);

            // Snap back via active=false.
            w.UpdateHeadGazeCenter(headGazeX: 0.7, headGazeY: 0.3, active: false);
            Assert.Equal(0.5, brush.Center.X, 3);
            Assert.Equal(0.5, brush.Center.Y, 3);
            Assert.Equal(0.85, brush.RadiusX, 3);
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
    // The dtMs parameter is now inert inside ApplyHeadPoseToBrush (kept on the
    // signature for a clean revert to f381a43); the smoothing that used to live
    // here is exercised at the One-Euro level by the convergence test below.

    /// <summary>One-clock: radius reaches the head-coupled corner-reach target in a
    /// single frame regardless of dtMs (because radius is now a pure function of the
    /// already-smoothed center, not a separately-eased value). Corner reach — the M5
    /// UX-iter 3 behaviour the dynamic radius was built for — is intact.</summary>
    [Fact]
    public void OverlayWindow_OneClock_Radius_DerivesFromCenter_InSameFrame()
    {
        RunOnSta(() =>
        {
            var prior = AppConfig.HeadTrackingFollowMode;
            AppConfig.HeadTrackingFollowMode = HeadTrackingFollowModeKind.FullFollow;
            try
            {
                var w = new OverlayWindow();
                w.ApplyTemplate();
                // Seed brush + baseline radius via a flag-off ApplyState.
                w.ApplyState(new SenseState { VimsTier = 3, HeadGazeX = 0.5, HeadGazeY = 0.5, HeadTrackingActive = false, MaskActive = true });
                var brush = w.FovBrushForTests;
                Assert.NotNull(brush);
                Assert.Equal(0.85, brush!.RadiusX, 3);

                // A realistic ~6 ms frame (NOT a giant dt) toward the corner: with the
                // separate envelope removed, radius lands on target the SAME frame the
                // center does — no 600 ms lag.
                w.UpdateHeadGazeCenter(headGazeX: 1.0, headGazeY: 1.0, active: true, dtMs: 6.0);
                double target = Math.Sqrt(2.0) * 1.10;
                Assert.Equal(1.0, brush.Center.X, 3);
                Assert.Equal(1.0, brush.Center.Y, 3);
                Assert.Equal(target, brush.RadiusX, 3);
                Assert.Equal(target, brush.RadiusY, 3);
            }
            finally { AppConfig.HeadTrackingFollowMode = prior; }
        });
    }

    /// <summary>One-clock: returning the head toward center COLLAPSES the radius in
    /// the SAME frame the center moves — there is no separate slow-release clock left
    /// (the old 600 ms radius envelope is gone). Smoothing now lives entirely in the
    /// upstream One-Euro center; within the overlay, center and edge move together.</summary>
    [Fact]
    public void OverlayWindow_OneClock_Radius_TracksCenter_NoSeparateReleaseLag()
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
                var brush = w.FovBrushForTests!;

                // Reach the corner (center 1,1 → radius sqrt(2)*1.10).
                w.UpdateHeadGazeCenter(headGazeX: 1.0, headGazeY: 1.0, active: true, dtMs: 6.0);
                Assert.True(brush.RadiusX > 1.5, $"precondition: radius at corner, was {brush.RadiusX}");

                // Centre handed back (the upstream filter would have already eased it):
                // a single small-dt frame collapses the radius to the floor in lockstep
                // with the center — proving the old 600 ms release clock is gone.
                w.UpdateHeadGazeCenter(headGazeX: 0.5, headGazeY: 0.5, active: true, dtMs: 6.0);
                Assert.Equal(0.5, brush.Center.X, 3);
                Assert.Equal(0.85, brush.RadiusX, 3);
                Assert.Equal(0.85, brush.RadiusY, 3);
            }
            finally { AppConfig.HeadTrackingFollowMode = prior; }
        });
    }

    /// <summary>One-clock: the grid OpacityMask hole (the third moving geometry)
    /// tracks the FOV brush's Center and Radius in the SAME frame — one moving
    /// boundary, not a separately-clocked grid layer that lags the mask.</summary>
    [Fact]
    public void OverlayWindow_OneClock_GridHole_TracksMask_InSameFrame()
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
                var fov  = w.FovBrushForTests!;
                var grid = w.GridMaskBrushForTests;

                w.UpdateHeadGazeCenter(headGazeX: 0.8, headGazeY: 0.2, active: true, dtMs: 6.0);

                // Grid hole center + radius are byte-identical to the FOV brush's,
                // updated in the same call — no lag, no second clock.
                Assert.Equal(fov.Center.X,  grid.Center.X,  6);
                Assert.Equal(fov.Center.Y,  grid.Center.Y,  6);
                Assert.Equal(fov.RadiusX,   grid.RadiusX,   6);
                Assert.Equal(fov.RadiusY,   grid.RadiusY,   6);
            }
            finally { AppConfig.HeadTrackingFollowMode = prior; }
        });
    }

    /// <summary>Disabling tracking SNAPS the radius back to the v0.9 baseline
    /// (0.85) immediately — no slow-release on disable — even after the radius
    /// had grown to a corner. Preserves the byte-identical flag-off baseline.</summary>
    [Fact]
    public void OverlayWindow_RadiusEnvelope_InactiveSnapsToBaseline()
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
                var brush = w.FovBrushForTests!;

                w.UpdateHeadGazeCenter(headGazeX: 1.0, headGazeY: 1.0, active: true, dtMs: 10000);
                Assert.True(brush.RadiusX > 1.5);

                // Tracking drops out → immediate baseline (no 600 ms decay).
                w.UpdateHeadGazeCenter(headGazeX: 1.0, headGazeY: 1.0, active: false, dtMs: 16);
                Assert.Equal(0.85, brush.RadiusX, 3);
                Assert.Equal(0.85, brush.RadiusY, 3);
                Assert.Equal(0.5, brush.Center.X, 3);
                Assert.Equal(0.5, brush.Center.Y, 3);
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
                var brush = w.FovBrushForTests!;

                // Jitter within the central box where farthestCorner*1.10 < 0.85.
                double[] xs = { 0.50, 0.52, 0.48, 0.51, 0.49 };
                foreach (var x in xs)
                    w.UpdateHeadGazeCenter(headGazeX: x, headGazeY: 0.5, active: true, dtMs: 16);

                Assert.Equal(0.85, brush.RadiusX, 3);
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
    private static void RunOnSta(Action body)
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
        Assert.True(done.Wait(TimeSpan.FromSeconds(15)), "STA test timed out");
        if (captured != null)
            throw new Xunit.Sdk.XunitException("STA test threw: " + captured);
    }
}
