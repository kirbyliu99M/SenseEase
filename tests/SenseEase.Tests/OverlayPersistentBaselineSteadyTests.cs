using System;
using System.Threading;
using SenseEase.App.Views;
using SenseEase.Core.Configuration;
using SenseEase.Core.Models;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Kirby round-3 (item 3, 2026-06-11): persistent-overlay sensing ISOLATION (保持遮罩開啟 =
/// render the CHOSEN baseline tier steadily, never polluted by live sensing noise). The
/// host (<c>MainWindow.OnStateChanged</c>) sets <see cref="SenseState.PersistentBaselineSteady"/>
/// when the always-on baseline floor has ELEVATED the displayed tier above the engine's live
/// tier (live tier &lt;= baseline). These STA tests pin the OverlayWindow contract:
/// <list type="bullet">
///   <item>baseline tier + NOISY calm samples (varying OFI/pressure) ⇒ the rendered grid/cell
///         target, the effective mask peak AND the field center hold CONSTANT across ticks (no
///         JUMP from live OFI/pressure leaking into the steady render);</item>
///   <item>a genuine spike (live tier &gt; baseline ⇒ flag false) lets normal LIVE behaviour
///         take over — protection never weakens;</item>
///   <item>when the spike subsides the render RETURNS to the steady baseline.</item>
/// </list>
/// The constancy is the whole point of the fix: with the flag the intensity term is the
/// CONSTANT 0.55 floor (no smoothedIntensity) and the mask cross-fade snaps to the tier
/// target (no OFI flicker / chase).
/// </summary>
public class OverlayPersistentBaselineSteadyTests
{
    private static SenseState SteadyBaseline(int tier, double ofi, double pressure)
        => new()
        {
            VimsTier                 = tier,   // the host has already floored this to the baseline
            MaskActive               = true,
            PersistentBaselineSteady = true,   // baseline floor is winning (live tier <= baseline)
            OpticalFlowIndex         = ofi,    // live sensing noise that MUST NOT leak in
            RawOpticalFlowIndex      = ofi,
            Pressure                 = pressure,
            HeadTrackingActive       = false,  // centred (0.5, 0.5)
            Shader                   = new ShaderParams(),
        };

    [Fact]
    public void SteadyBaseline_NoisyCalmSamples_RenderTierPeakAndGridTargetAreConstant()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();

            // Baseline L6 held steady while live OFI/pressure bounce around (calm-but-noisy):
            // the engine tier stays floored to 6, but without isolation the grid/cell target
            // and the mask cross-fade would chase this noise and JUMP.
            double[] ofiNoise      = { 0, 30, 5, 80, 12, 95, 3, 60, 0, 45 };
            double[] pressureNoise = { 0, 20, 10, 70, 15, 84, 5, 55, 0, 40 };

            // Settle the first steady tick, then read the reference values.
            w.ApplyState(SteadyBaseline(6, ofiNoise[0], pressureNoise[0]));
            double refTarget = w.GridOpacityTargetForTests;
            double refPeak   = w.EffectivePeakAlphaForTests;
            double refCx     = w.FieldCenterXForTests;
            double refCy     = w.FieldCenterYForTests;

            for (int i = 1; i < ofiNoise.Length; i++)
            {
                w.ApplyState(SteadyBaseline(6, ofiNoise[i], pressureNoise[i]));

                Assert.Equal(refTarget, w.GridOpacityTargetForTests, 9);   // grid/cell target constant
                Assert.Equal(refPeak,   w.EffectivePeakAlphaForTests, 9);  // mask peak constant (no chase)
                Assert.Equal(refCx,     w.FieldCenterXForTests, 9);        // centre constant
                Assert.Equal(refCy,     w.FieldCenterYForTests, 9);
            }

            // The steady grid target equals the SAME tier computed with the CONSTANT 0.55 floor
            // (no smoothedIntensity term), then capped — proving the intensity isolation.
            double expected = Math.Min(
                Math.Clamp(
                    OverlayWindow.GridTierFraction(6)
                        * OverlayWindow.GridCeiling(new ShaderParams().GridVisibility)
                        * SenseDefaults.OverlaySteadyBaselineIntensity,
                    0.0,
                    OverlayWindow.GridCeiling(new ShaderParams().GridVisibility)),
                SenseDefaults.GridOpacityCap);
            Assert.Equal(expected, refTarget, 9);
        });
    }

    [Fact]
    public void SteadyBaseline_MaskPeak_EqualsBaselineTierTable_NotChasing()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();

            // Steady L6: the effective peak must equal the L6 table value at the default
            // MaskDepth (no override) — snapped, not eased from a prior tier.
            w.ApplyState(SteadyBaseline(6, ofi: 50, pressure: 60));
            double l6Table = OverlayWindow.VimsLevels[5].Alpha; // L6 == 0.50
            Assert.Equal(l6Table, w.EffectivePeakAlphaForTests, 9);
        });
    }

    [Fact]
    public void GenuineSpike_LiveTierAboveBaseline_LiveBehaviourWins_ProtectionNeverWeakens()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();

            // Steady baseline L6 first.
            for (int i = 0; i < 5; i++) w.ApplyState(SteadyBaseline(6, ofi: 0, pressure: 0));
            double steadyPeak = w.EffectivePeakAlphaForTests;

            // A genuine spike: the live tier exceeds the baseline (the host leaves the flag
            // FALSE because displayed == original at tier 9 > baseline 6). Normal live
            // rendering takes over — and the deeper tier must darken FURTHER (never weaken).
            var spike = new SenseState
            {
                VimsTier                 = 9,      // live tier > baseline 6
                MaskActive               = true,
                PersistentBaselineSteady = false,  // host: displayed == original (live wins)
                OpticalFlowIndex         = 110,
                Pressure                 = SenseDefaults.TriggerPressure,
                HeadTrackingActive       = false,
                Shader                   = new ShaderParams(),
            };
            for (int i = 0; i < 20; i++) w.ApplyState(spike);

            // L9 table alpha (0.73) > L6 (0.50): the spike deepens the mask — protection
            // strengthened, never weakened.
            Assert.True(w.EffectivePeakAlphaForTests > steadyPeak,
                $"a genuine spike must darken further (never weaken): steady={steadyPeak}, spike={w.EffectivePeakAlphaForTests}");
        });
    }

    [Fact]
    public void SpikeSubsides_ReturnsToSteadyBaseline()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();

            // 1) Steady baseline L6 — capture the steady render values.
            w.ApplyState(SteadyBaseline(6, ofi: 0, pressure: 0));
            double steadyTarget = w.GridOpacityTargetForTests;
            double steadyPeak   = w.EffectivePeakAlphaForTests;

            // 2) Spike: live tier 9 wins (flag false), live smoothing engaged.
            var spike = new SenseState
            {
                VimsTier = 9, MaskActive = true, PersistentBaselineSteady = false,
                OpticalFlowIndex = 110, Pressure = SenseDefaults.TriggerPressure,
                HeadTrackingActive = false, Shader = new ShaderParams(),
            };
            for (int i = 0; i < 20; i++) w.ApplyState(spike);
            Assert.True(w.EffectivePeakAlphaForTests > steadyPeak, "precondition: spike deepened the mask");

            // 3) Spike subsides — the engine's VimsTier debounce releases the live tier back
            // under the baseline, so the host re-floors to L6 and re-sets the steady flag.
            // The render RETURNS to the exact steady baseline values (snapped, not chasing).
            w.ApplyState(SteadyBaseline(6, ofi: 0, pressure: 0));
            Assert.Equal(steadyTarget, w.GridOpacityTargetForTests, 9);
            Assert.Equal(steadyPeak,   w.EffectivePeakAlphaForTests, 9);
        });
    }

    [Fact]
    public void FlagOff_LiveBehaviour_StillSmoothsAcrossTicks_ByteIdenticalBaseline()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();

            // With the flag OFF (default) the live path is unchanged: the EMA-smoothed
            // intensity means a rising pressure ramp moves the grid target across ticks
            // (it does NOT hold constant). This guards that the isolation is gated on the
            // flag and the flag-off path keeps the pre-round-3 live smoothing.
            SenseState Live(double pressure) => new()
            {
                VimsTier = 8, MaskActive = true, PersistentBaselineSteady = false,
                OpticalFlowIndex = 100, Pressure = pressure,
                HeadTrackingActive = false, Shader = new ShaderParams(),
            };

            w.ApplyState(Live(0));
            double t0 = w.GridOpacityTargetForTests;
            for (int i = 0; i < 10; i++) w.ApplyState(Live(SenseDefaults.TriggerPressure));
            double t1 = w.GridOpacityTargetForTests;

            // The live target RESPONDS to sustained pressure (EMA climbs) — it is NOT frozen
            // like the steady path. (Capped, so we assert it rose, not by how much.)
            Assert.True(t1 > t0,
                $"flag-off live path must keep responding to pressure (EMA), t0={t0} t1={t1}");
        });
    }

    // ── STA harness (copied from OverlayHeadPoseTests / NotificationCardWindowTests) ──
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
