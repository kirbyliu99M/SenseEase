using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using SenseEase.App.Views;
using SenseEase.Core.Configuration;
using SenseEase.Core.Models;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Round-10 trembling audit (2026-06-11): Kirby reported the grid still trembles/scales
/// (縮放抖動) AFTER the round-9 corner-growth freeze — verified to be on the frozen build.
/// A 5-lens adversarially-verified audit found the residual motion is NOT geometry but
/// clock/pipeline defects, all code-provable. These pin the fixes:
/// <list type="bullet">
///   <item>ONE-CLOCK (smoking gun): the engine-tick ApplyState carried the RAW un-One-Euro'd
///         sidecar pose and fought the pump's filtered pose over the hole center at ~15 Hz —
///         the presented hole hopped raw↔filtered by ~1-7 device px, bypassing the filter.
///         While <c>PoseOwnedByPump</c> is set, ApplyState must not move the pose at all.</item>
///   <item>RE-AIM GATE: the live grid-opacity target re-aimed the 280 ms animation every
///         ~66 ms tick straight off OFI/pressure noise (the last un-deadbanded follower).
///         Noise wander below ~2 on-screen codes must never re-aim; discrete tier/gv/steady
///         changes must.</item>
///   <item>EMA CONTINUITY: the steady branch used to freeze _smoothedIntensity, so a
///         steady→live edge injected a minutes-old (possibly saturated) intensity as a
///         one-tick step of up to ~22 on-screen codes. The EMA now tracks on both branches.</item>
///   <item>SEAM MAPPING: the grid-window OpacityMask brush mapped RelativeToBoundingBox over
///         the overshoot-inflated GridCanvas render bounds (~12 device px hole offset); it
///         must map ABSOLUTELY onto the field's extent.</item>
/// </list>
/// </summary>
public class OverlayTremblingRound10Tests
{
    private static SenseState Live(int tier, double ofi, double pressure) => new()
    {
        VimsTier                 = tier,
        MaskActive               = true,
        PersistentBaselineSteady = false,
        OpticalFlowIndex         = ofi,
        RawOpticalFlowIndex      = ofi,
        Pressure                 = pressure,
        HeadTrackingActive       = false,
        Shader                   = new ShaderParams(),
    };

    private static SenseState Steady(int tier, double ofi, double pressure)
    {
        var s = Live(tier, ofi, pressure);
        s.PersistentBaselineSteady = true;
        return s;
    }

    // ── 1) One-clock pose ownership (the smoking gun) ─────────────────────────

    [Fact]
    public void ApplyState_WhilePumpOwnsPose_IgnoresRawStatePose()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();
            w.PoseOwnedByPump = true;

            // Seed the field bitmap (UpdateHeadGazeCenter is a no-op until it exists). The
            // seed state carries a divergent raw pose — already ignored under pump ownership.
            var seed = Steady(3, 0, 0);
            seed.HeadGazeX          = 0.70;
            seed.HeadGazeY          = 0.30;
            seed.HeadTrackingActive = true;
            w.ApplyState(seed);
            Assert.Equal(0.5, w.FieldCenterXForTests, 9); // raw seed pose did not move it

            // The pump (the single One-Euro clock) parks the pose at (0.60, 0.45).
            w.UpdateHeadGazeCenter(0.60, 0.45, active: true, dtMs: 6.0);
            Assert.Equal(0.60, w.FieldCenterXForTests, 9);
            Assert.Equal(0.45, w.FieldCenterYForTests, 9);

            // Engine tick arrives carrying a DIVERGENT raw sidecar pose: ignored entirely —
            // including its HeadTrackingActive flag (the pump's verdict governs).
            var raw = Steady(3, 0, 0);
            raw.HeadGazeX          = 0.70;
            raw.HeadGazeY          = 0.30;
            raw.HeadTrackingActive = true;
            w.ApplyState(raw);
            Assert.Equal(0.60, w.FieldCenterXForTests, 9);
            Assert.Equal(0.45, w.FieldCenterYForTests, 9);

            // Once non-pose params settle, a tick whose ONLY change is the raw pose causes
            // zero recomputes — the raw stream cannot re-dirt the field while the pump owns it.
            w.ApplyState(raw);
            long before = w.TotalRecomputesForTests;
            raw.HeadGazeX = 0.72;
            raw.HeadGazeY = 0.28;
            w.ApplyState(raw);
            Assert.Equal(0.60, w.FieldCenterXForTests, 9);
            Assert.Equal(before, w.TotalRecomputesForTests);

            // Pump parked (flag off): the engine path is the pose fallback again — the
            // pre-existing ApplyState-driven follow (FollowsHeadGaze pin) is unchanged.
            w.PoseOwnedByPump = false;
            w.ApplyState(raw);
            Assert.Equal(0.72, w.FieldCenterXForTests, 9);
            Assert.Equal(0.28, w.FieldCenterYForTests, 9);
        });
    }

    // ── 2) Hysteretic re-aim gate on the live grid-opacity target ─────────────

    [Fact]
    public void LiveNoiseWander_DoesNotRetargetGridOpacity()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();

            // Warm the intensity EMA to its operating point so the sustained-rise transient
            // (a legitimate re-aim) is over before the noise assertion starts.
            for (int i = 0; i < 300; i++)
                w.ApplyState(Live(6, 40, 40));
            double aimed = w.GridOpacityTargetForTests;

            // Zero-mean OFI noise around the operating point: per-tick EMA movement is far
            // below the 2-code re-aim quantum, so the 280 ms animation must never re-aim —
            // the anchor (the .To the seam reads) stays byte-identical.
            double[] ofiNoise = { 45, 35, 48, 32, 44, 36, 47, 33, 42, 38, 46, 34 };
            foreach (double ofi in ofiNoise)
            {
                w.ApplyState(Live(6, ofi, 40));
                Assert.Equal(aimed, w.GridOpacityTargetForTests, 12);
            }
        });
    }

    [Fact]
    public void DiscreteTierStep_StillRetargetsImmediately()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();

            for (int i = 0; i < 50; i++)
                w.ApplyState(Live(6, 40, 40));
            double aimedAt6 = w.GridOpacityTargetForTests;

            // A genuine tier step is a DISCRETE protection change: it must re-aim at once
            // (the gate must never deaden deliberate/protection-relevant moves).
            w.ApplyState(Live(7, 40, 40));
            double aimedAt7 = w.GridOpacityTargetForTests;
            Assert.True(aimedAt7 > aimedAt6,
                $"tier step must re-aim the grid target: L6={aimedAt6}, L7={aimedAt7}");
        });
    }

    // ── 3) Steady→live EMA continuity (no stale-intensity step) ───────────────

    [Fact]
    public void SteadyToLiveEdge_HandsOffContinuously_NoStaleIntensityStep()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();

            // Saturate the EMA in a hot LIVE episode (pressure ≥ trigger ⇒ intensity ≈ 1.0).
            for (int i = 0; i < 400; i++)
                w.ApplyState(Live(8, 120, 200));

            // Long STEADY hold at calm signals. The old bug froze the EMA here, so the next
            // live tick re-entered with the saturated value (a ~3.5× one-tick target step).
            for (int i = 0; i < 400; i++)
                w.ApplyState(Steady(6, 0, 0));

            // First LIVE tick after the hold, same calm signals: the intensity term must be
            // continuous with the steady floor (the EMA decayed during the hold), not stale.
            w.ApplyState(Live(6, 0, 0));
            double target = w.GridOpacityTargetForTests;
            double gv     = new ShaderParams().GridVisibility;
            double tierComponent = OverlayWindow.GridTierFraction(6) * OverlayWindow.GridCeiling(gv);
            double bound = tierComponent * (0.55 + 0.45 * 0.05); // ≤ 5% residual EMA allowed
            Assert.True(target <= bound + 1e-9,
                $"stale-EMA step leaked across the steady→live edge: target={target}, bound={bound}");
        });
    }

    // ── 4) Grid-window mask maps absolutely onto the field extent ─────────────

    [Fact]
    public void GridWindowMaskBrush_MapsAbsolutelyToFieldExtent()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();
            w.ApplyState(Live(6, 0, 0)); // builds the field bitmap, DrawGrid, and the brush

            var brush = w.GridWindowMaskBrushForTests;
            Assert.NotNull(brush);
            Assert.Equal(BrushMappingMode.Absolute, brush!.ViewportUnits);

            // An unshown STA window has no layout, so DrawGrid fell back to the primary
            // screen extent — the SAME fallback EnsureFieldBitmap uses. The viewport must
            // span exactly that field rect (not the overshoot-inflated canvas bounds).
            Assert.Equal(0.0, brush.Viewport.X, 6);
            Assert.Equal(0.0, brush.Viewport.Y, 6);
            Assert.Equal(SystemParameters.PrimaryScreenWidth,  brush.Viewport.Width,  6);
            Assert.Equal(SystemParameters.PrimaryScreenHeight, brush.Viewport.Height, 6);
        });
    }

    // ── STA harness (copied from OverlayPersistentBaselineSteadyTests) ────────
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
