using System;
using SenseEase.App.Views;
using SenseEase.Core.Configuration;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Kirby round-3 (items 1 + 2, 2026-06-11): grid tier-ONSET + opacity CAP. Pin the pure
/// helpers that WRAP the (byte-identical) GridCeiling curve:
/// <list type="bullet">
///   <item><b>Onset</b> (<see cref="OverlayWindow.GridTierFraction"/>): tiers below
///         <see cref="SenseDefaults.GridOnsetTier"/> contribute NOTHING (0); the ramp is
///         monotonic from the onset to full strength (1.0) at L12.</item>
///   <item><b>Cap</b>: the ApplyGridIntensity target is clamped to
///         <see cref="SenseDefaults.GridOpacityCap"/>; a hot top tier with grid-on saturates
///         AT the cap (never above), and "/grid off" (GridCeiling 0) stays exactly 0 under
///         the cap (min(0, cap) = 0 — grid-off is honoured).</item>
/// </list>
/// The GridCeiling CURVE itself is pinned byte-for-byte by <see cref="GridCeilingTests"/>;
/// these pin only the round-3 onset gate + cap wrap.
/// </summary>
public class GridOnsetTests
{
    // ── Item 1: grid tier-onset ──────────────────────────────────────────────

    [Fact]
    public void GridTierFraction_BelowOnset_IsZero_GridFullyInvisible()
    {
        // L1..L(onset-1) ⇒ the grid contributes NOTHING (fully invisible). At onset 4 that
        // is L1, L2, L3 (Kirby: the grid appeared too early — it faded in from L2 ≈ 5%).
        for (int tier = 1; tier < SenseDefaults.GridOnsetTier; tier++)
            Assert.Equal(0.0, OverlayWindow.GridTierFraction(tier), 9);
    }

    [Fact]
    public void GridTierFraction_AtOnset_IsZero_ThenRampsUp()
    {
        // Exactly at the onset the contribution begins at 0 and rises immediately after.
        Assert.Equal(0.0, OverlayWindow.GridTierFraction(SenseDefaults.GridOnsetTier), 9);
        Assert.True(OverlayWindow.GridTierFraction(SenseDefaults.GridOnsetTier + 1) > 0.0,
            "the grid must begin to ramp just past the onset tier");
    }

    [Fact]
    public void GridTierFraction_AtL12_IsFullStrength()
    {
        // Full strength still at L12 (the onset only shifts WHERE the ramp begins).
        Assert.Equal(1.0, OverlayWindow.GridTierFraction(12), 9);
    }

    [Fact]
    public void GridTierFraction_IsMonotonicAcrossTiers()
    {
        double prev = -1;
        for (int tier = 1; tier <= 12; tier++)
        {
            double f = OverlayWindow.GridTierFraction(tier);
            Assert.True(f >= prev - 1e-12, $"grid tier fraction must be monotonic, L{tier}: {prev} -> {f}");
            Assert.InRange(f, 0.0, 1.0 + 1e-9);
            prev = f;
        }
    }

    [Fact]
    public void GridTierFraction_RampMatchesClampedFormula()
    {
        // gridTierFraction = clamp((tier - onset) / (12 - onset), 0, 1).
        int onset = SenseDefaults.GridOnsetTier;
        double span = 12.0 - onset;
        for (int tier = onset; tier <= 12; tier++)
        {
            double expected = Math.Clamp((tier - onset) / span, 0.0, 1.0);
            Assert.Equal(expected, OverlayWindow.GridTierFraction(tier), 9);
        }
    }

    // ── Item 2: opacity cap (the target wrap on top of GridCeiling) ──────────
    //
    // ApplyGridIntensity computes:
    //   computed = clamp(gridTierFraction(tier) * GridCeiling(gv) * intensityTerm, 0, ceiling)
    //   target   = min(computed, GridOpacityCap)
    // These pin the cap behaviour through that exact composition (the live target is
    // exercised end-to-end by OverlayHeadPoseTests.OverlayWindow_GridOpacityTarget_*; here
    // we pin the math so a curve/cap drift is caught without an STA window).

    private static double ComputeTarget(int tier, double gv, double intensityTerm)
    {
        double computed = Math.Clamp(
            OverlayWindow.GridTierFraction(tier) * OverlayWindow.GridCeiling(gv) * intensityTerm,
            0.0,
            OverlayWindow.GridCeiling(gv));
        return Math.Min(computed, SenseDefaults.GridOpacityCap);
    }

    [Fact]
    public void Target_HotTopTier_SaturatesAtCap_NeverAbove()
    {
        // L12, default grid-on (gv 0.75 ⇒ ceiling 1.0), hot intensity (term 1.0): the
        // uncapped product is 1.0, so the cap clamps it to exactly GridOpacityCap.
        double target = ComputeTarget(tier: 12, gv: 0.75, intensityTerm: 1.0);
        Assert.Equal(SenseDefaults.GridOpacityCap, target, 9);
    }

    [Fact]
    public void Target_NeverExceedsCap_AcrossTiersAndIntensity()
    {
        for (int tier = 1; tier <= 12; tier++)
            for (double term = 0.55; term <= 1.0 + 1e-9; term += 0.15)
            {
                double target = ComputeTarget(tier, gv: 0.75, intensityTerm: term);
                Assert.True(target <= SenseDefaults.GridOpacityCap + 1e-12,
                    $"target must never exceed the cap, L{tier} term={term}: {target}");
            }
    }

    [Fact]
    public void Target_GridOff_IsZero_UnderCap()
    {
        // "/grid off": GridVisibility 0 ⇒ GridCeiling 0 ⇒ computed 0 ⇒ min(0, cap) = 0.
        double target = ComputeTarget(tier: 12, gv: 0.0, intensityTerm: 1.0);
        Assert.Equal(0.0, target, 9);
    }

    [Fact]
    public void Target_BelowOnset_IsZero_EvenHot()
    {
        // Below the onset the grid contributes nothing regardless of intensity / ceiling.
        for (int tier = 1; tier < SenseDefaults.GridOnsetTier; tier++)
            Assert.Equal(0.0, ComputeTarget(tier, gv: 0.75, intensityTerm: 1.0), 9);
    }

    [Fact]
    public void Target_IsMonotonicInTier_UpToCap()
    {
        // For a fixed grid-on ceiling + intensity, the (pre-cap) target rises monotonically
        // with tier, then is held at the cap — never reverses.
        double prev = -1;
        for (int tier = 1; tier <= 12; tier++)
        {
            double target = ComputeTarget(tier, gv: 0.75, intensityTerm: 1.0);
            Assert.True(target >= prev - 1e-12, $"target must be monotonic in tier, L{tier}: {prev} -> {target}");
            prev = target;
        }
    }
}
