using System;
using System.Threading;
using SenseEase.App.Configuration;
using SenseEase.App.Views;
using SenseEase.Core.Configuration;
using SenseEase.Core.Models;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Kirby round-3 (item 4, 2026-06-11): overlay SMOOTHNESS — no stutter/bounce, follow the
/// head faithfully. These STA tests pin the two structural smoothness guarantees:
/// <list type="bullet">
///   <item><b>Anti-bounce</b>: a smooth monotonic synthetic pose ramp produces field centers
///         with NO frame-to-frame reversal — the field follows the head without the
///         freeze-then-catch-up that read as 彈跳 (the item 4.1 motion-epsilon fix prevents the
///         One-Euro glide being quantized into a freeze);</item>
///   <item><b>No double-compute</b>: the field recompute count (== WritePixels) over N pumped
///         frames stays &lt;= N — at most one WritePixels per CompositionTarget.Rendering frame
///         (item 4.4), and a non-moving frame costs nothing.</item>
/// </list>
/// </summary>
public class OverlaySmoothnessTests
{
    /// <summary>A smooth monotonic ramp of the head pose toward a corner produces a strictly
    /// non-decreasing field center X — no frame-to-frame reversal (anti-bounce). With the
    /// item 4.1 active-motion epsilon the field tracks every step of the ramp; nothing is
    /// quantized into a freeze that would later jump backward or stall.</summary>
    [Fact]
    public void SmoothPoseRamp_ProducesMonotonicFieldCenters_NoReversal()
    {
        RunOnSta(() =>
        {
            var prior = AppConfig.HeadTrackingFollowMode;
            AppConfig.HeadTrackingFollowMode = HeadTrackingFollowModeKind.FullFollow;
            try
            {
                var w = new OverlayWindow();
                w.ApplyTemplate();
                // Seed baseline (flag-off centred).
                w.ApplyState(new SenseState { VimsTier = 3, HeadGazeX = 0.5, HeadGazeY = 0.5, HeadTrackingActive = false, MaskActive = true });

                const int frames = 120;
                double prevCx = w.FieldCenterXForTests;
                double prevCy = w.FieldCenterYForTests;
                for (int i = 1; i <= frames; i++)
                {
                    // A smooth ramp 0.5 → 0.9 over the burst (the kind of value the upstream
                    // One-Euro pump emits during a steady head turn).
                    double f = (double)i / frames;
                    double x = 0.5 + 0.4 * f;
                    double y = 0.5 + 0.4 * f;
                    w.UpdateHeadGazeCenter(headGazeX: x, headGazeY: y, active: true, dtMs: 6.0);

                    double cx = w.FieldCenterXForTests;
                    double cy = w.FieldCenterYForTests;
                    // Strictly non-decreasing — never reverses direction (no bounce / catch-up).
                    Assert.True(cx >= prevCx - 1e-12, $"field center X reversed at frame {i}: {prevCx} -> {cx}");
                    Assert.True(cy >= prevCy - 1e-12, $"field center Y reversed at frame {i}: {prevCy} -> {cy}");
                    prevCx = cx; prevCy = cy;
                }

                // The field ended at the ramp target (it followed the head all the way — no
                // stall short of target).
                Assert.Equal(0.9, w.FieldCenterXForTests, 6);
            }
            finally { AppConfig.HeadTrackingFollowMode = prior; }
        });
    }

    /// <summary>The recompute (WritePixels) count over N pumped frames stays &lt;= N — at most
    /// one field write per frame (item 4.4) and zero when nothing moved (the dirty gate). This
    /// catches a regression that double-computes the field within a single frame, or that
    /// recomputes a static frame.</summary>
    [Fact]
    public void PumpedFrames_RecomputeCount_NeverExceedsFrameCount()
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

                long beforeMoving = w.TotalRecomputesForTests;
                const int movingFrames = 100;
                for (int i = 1; i <= movingFrames; i++)
                {
                    double f = (double)i / movingFrames;
                    w.UpdateHeadGazeCenter(headGazeX: 0.5 + 0.4 * f, headGazeY: 0.5, active: true, dtMs: 6.0);
                }
                long movingRecomputes = w.TotalRecomputesForTests - beforeMoving;

                // At most one recompute per pumped frame (one WritePixels per Rendering frame).
                Assert.True(movingRecomputes <= movingFrames,
                    $"recompute count must be <= frame count (no double-compute): {movingRecomputes} > {movingFrames}");
                // The moving ramp genuinely re-rendered (sanity: not a silent no-op).
                Assert.True(movingRecomputes > 0, "a moving pose ramp must recompute the field at least once");

                // Now pump IDENTICAL (non-moving) frames — the dirty gate must cost zero
                // recomputes (a static screen costs ~zero).
                long beforeStatic = w.TotalRecomputesForTests;
                for (int i = 0; i < 50; i++)
                    w.UpdateHeadGazeCenter(headGazeX: 0.9, headGazeY: 0.5, active: true, dtMs: 6.0);
                long staticRecomputes = w.TotalRecomputesForTests - beforeStatic;
                Assert.True(staticRecomputes <= 1,
                    $"identical (non-moving) frames must not recompute the field: {staticRecomputes}");
            }
            finally { AppConfig.HeadTrackingFollowMode = prior; }
        });
    }

    // ── STA harness (copied from OverlayHeadPoseTests) ──
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
