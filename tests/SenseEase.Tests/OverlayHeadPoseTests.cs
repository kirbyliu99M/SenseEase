using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using SenseEase.App.Configuration;
using SenseEase.App.ViewModels;
using SenseEase.App.Views;
using SenseEase.Core.Abstractions;
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
/// captured-exception relay).</summary>
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

    // ── 6) M5 UX-iter 1: head-gaze travel clamp ───────────────────────────

    /// <summary>M5 UX-iteration 1: extreme head-gaze values must be clamped
    /// to the central [0.5 - HeadGazeTravelRadius, 0.5 + HeadGazeTravelRadius]
    /// window so the opposite side of the screen never falls past the
    /// RadialGradientBrush radius (which would render fully opaque dark).
    /// Verified visually by user 2026-05-29 with HeadGazeTravelRadius=0.20.</summary>
    [Fact]
    public void OverlayWindow_HeadGazeBeyondClampRange_ClampedToCentralWindow()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();

            // Sample value at one extreme (cx=0.1, cy=0.9 — mock-sweep edges).
            var state = new SenseState
            {
                VimsTier            = 3,
                HeadGazeX           = 0.1,      // below clamp → 0.30
                HeadGazeY           = 0.9,      // above clamp → 0.70
                HeadTrackingActive  = true,
                MaskActive          = true,
            };
            w.ApplyState(state);

            var brush = w.FovBrushForTests;
            Assert.NotNull(brush);
            Assert.Equal(0.30, brush!.Center.X, 3);
            Assert.Equal(0.70, brush.Center.Y, 3);

            // Sample at the other extreme.
            var state2 = new SenseState
            {
                VimsTier            = 3,
                HeadGazeX           = 0.95,     // above clamp → 0.70
                HeadGazeY           = 0.05,     // below clamp → 0.30
                HeadTrackingActive  = true,
                MaskActive          = true,
            };
            w.ApplyState(state2);

            Assert.Equal(0.70, brush.Center.X, 3);
            Assert.Equal(0.30, brush.Center.Y, 3);

            // Inside-range value passes through unchanged.
            var state3 = new SenseState
            {
                VimsTier            = 3,
                HeadGazeX           = 0.55,
                HeadGazeY           = 0.45,
                HeadTrackingActive  = true,
                MaskActive          = true,
            };
            w.ApplyState(state3);

            Assert.Equal(0.55, brush.Center.X, 3);
            Assert.Equal(0.45, brush.Center.Y, 3);
        });
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
