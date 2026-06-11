using System;
using SenseEase.App.Views;
using SenseEase.Core.Configuration;
using SenseEase.Core.Models;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Beta A2 (single-pass field) — PURE field-math unit tests. The field math on
/// <see cref="OverlayWindow"/> is factored into static, WPF-free helpers exactly so it
/// is unit-testable without spinning up the STA window (the realization decision: the
/// WriteableBitmap path makes the field computable + assertable in plain C#). These pin:
/// <list type="bullet">
///   <item>corner-normalized radial coordinate at the center / corners (no animated
///         global radius — the breathing killer);</item>
///   <item>the shared FOV-mask / grid-window smoothstep falloff at sample radii;</item>
///   <item>the cell-fill fold (UVHS round 2, 2026-06-11): the legacy near-black cell tint
///         folded UNDER the tier mask dim, both windowed by the SAME inverse-feather — clean
///         centre, deepened periphery tone matching 3762a42; grid lines are no longer
///         rasterized into the field (they are full-res vector geometry on a separate surface);</item>
///   <item>MaskDepth (D-2) → bounded effective peak with the ≥0.20 floor / table ceiling
///         and byte-identity at the default;</item>
///   <item>FeatherWidth (D-2) → feather-band start slide with envelope-identity at default;</item>
///   <item>gamma-shape monotonicity (banding-mitigation pre-encode shaping).</item>
/// </list>
/// </summary>
public class OverlayFieldMathTests
{
    // ── Corner-normalized radial coordinate ─────────────────────────────────

    [Fact]
    public void CornerNormR_AtCenter_IsZero()
    {
        // At the center, distance is 0 ⇒ normalized coordinate 0 regardless of aspect.
        Assert.Equal(0.0, OverlayWindow.CornerNormR(0.5, 0.5, 0.5, 0.5, 16.0 / 9.0), 9);
        Assert.Equal(0.0, OverlayWindow.CornerNormR(0.3, 0.7, 0.3, 0.7, 16.0 / 9.0), 9);
    }

    [Fact]
    public void CornerNormR_AtFarthestCorner_IsOne()
    {
        // From a centered origin the farthest corner is any screen corner; the
        // corner-normalization makes it exactly 1.0 (full corner coverage, no global radius).
        double aspect = 16.0 / 9.0;
        Assert.Equal(1.0, OverlayWindow.CornerNormR(0.0, 0.0, 0.5, 0.5, aspect), 9);
        Assert.Equal(1.0, OverlayWindow.CornerNormR(1.0, 1.0, 0.5, 0.5, aspect), 9);
        Assert.Equal(1.0, OverlayWindow.CornerNormR(1.0, 0.0, 0.5, 0.5, aspect), 9);

        // Off-center: the FARTHEST corner from (0.8, 0.2) is (0, 1); it normalizes to 1.0.
        Assert.Equal(1.0, OverlayWindow.CornerNormR(0.0, 1.0, 0.8, 0.2, aspect), 9);
    }

    [Fact]
    public void CornerNormR_IsMonotonicOutward()
    {
        // Walking from center toward a corner, r increases monotonically in [0,1].
        double aspect = 16.0 / 9.0;
        double prev = -1;
        for (double t = 0; t <= 1.0; t += 0.1)
        {
            double r = OverlayWindow.CornerNormR(0.5 + 0.5 * t, 0.5 + 0.5 * t, 0.5, 0.5, aspect);
            Assert.True(r >= prev - 1e-12, $"r should be monotonic outward, t={t}: {prev} -> {r}");
            Assert.InRange(r, 0.0, 1.0 + 1e-9);
            prev = r;
        }
    }

    // ── Smoothstep falloff (shared FOV-mask + grid-window geometry) ─────────

    [Fact]
    public void ResolveFieldAlpha_InsideFeatherStart_IsZero()
    {
        // Below the ramp start the field alpha is 0 — the CLEAN centre. Both the FOV dim
        // AND the grid window are this same falloff, so a 0 here means BOTH are absent in
        // the centre (no dim, no grid) — the centre-clean invariant (UVHS 2026-06-11).
        Assert.Equal(0.0, OverlayWindow.ResolveFieldAlpha(0.0, 0.5, 0.35, 0.95), 9);
        Assert.Equal(0.0, OverlayWindow.ResolveFieldAlpha(0.34, 0.5, 0.35, 0.95), 9);
        Assert.Equal(0.0, OverlayWindow.ResolveFieldAlpha(0.35, 0.5, 0.35, 0.95), 9);
    }

    [Fact]
    public void ResolveFieldAlpha_AtOrBeyondFeatherEnd_IsPeak()
    {
        Assert.Equal(0.92, OverlayWindow.ResolveFieldAlpha(0.95, 0.92, 0.35, 0.95), 9);
        Assert.Equal(0.92, OverlayWindow.ResolveFieldAlpha(1.00, 0.92, 0.35, 0.95), 9);
    }

    [Fact]
    public void ResolveFieldAlpha_MidBand_MatchesSmootherstep()
    {
        // BANDING ROUND-5 (2026-06-11, PLAN-SANCTIONED): the falloff was upgraded from Hermite
        // smoothstep s(t)=t²(3−2t) to Perlin SMOOTHERSTEP s(t)=6t⁵−15t⁴+10t³ (continuous 2nd
        // derivative ⇒ no Mach-band edge salience — the faint crawling contour lines Kirby
        // reported at shallow tiers). The plan (BETA_FEATHERING_COHERENCE_PLAN.md §A2) names
        // smootherstep as the sanctioned optional banding mitigation. These re-pin the band
        // values to smootherstep; the endpoints + peak output are unchanged.

        // Midpoint of the 0.35→0.95 band: t=0.5, smootherstep(0.5) = 6·0.03125 − 15·0.0625 +
        // 10·0.125 = 0.1875 − 0.9375 + 1.25 = 0.5 ⇒ peak·0.5 (UNCHANGED at the midpoint — both
        // curves pass through 0.5 there, so the band centre is byte-identical to smoothstep).
        double mid = 0.35 + (0.95 - 0.35) * 0.5;
        Assert.Equal(0.5 * 0.80, OverlayWindow.ResolveFieldAlpha(mid, 0.80, 0.35, 0.95), 9);

        // The smootherstep value at t=0.25: 6·(0.25)⁵ − 15·(0.25)⁴ + 10·(0.25)³ =
        // 0.005859375 − 0.05859375 + 0.15625 = 0.103515625 (smoothstep gave 0.15625 here —
        // smootherstep is flatter near the start, the gentler near-clear ramp that softens the
        // shallow-tier edge).
        double q = 0.35 + (0.95 - 0.35) * 0.25;
        Assert.Equal(0.103515625 * 0.80, OverlayWindow.ResolveFieldAlpha(q, 0.80, 0.35, 0.95), 9);
    }

    [Fact]
    public void Smootherstep_EndpointsAndMidpoint_AndMonotonic()
    {
        // Endpoint identity (same as smoothstep — the protection envelope endpoints are preserved).
        Assert.Equal(0.0, OverlayWindow.Smootherstep(0.0), 12);
        Assert.Equal(1.0, OverlayWindow.Smootherstep(1.0), 12);
        Assert.Equal(0.0, OverlayWindow.Smootherstep(-0.3), 12); // clamps below 0
        Assert.Equal(1.0, OverlayWindow.Smootherstep(1.4), 12);  // clamps above 1
        // Midpoint is exactly 0.5 (the curve is symmetric — same as smoothstep at t=0.5).
        Assert.Equal(0.5, OverlayWindow.Smootherstep(0.5), 12);
        // The pinned t=0.25 value the falloff test relies on.
        Assert.Equal(0.103515625, OverlayWindow.Smootherstep(0.25), 12);

        // Strictly monotonic increasing across [0,1].
        double prev = -1;
        for (double t = 0.0; t <= 1.0 + 1e-9; t += 0.01)
        {
            double s = OverlayWindow.Smootherstep(t);
            Assert.True(s >= prev - 1e-12, $"smootherstep must be monotonic, t={t}: {prev} -> {s}");
            Assert.InRange(s, 0.0, 1.0 + 1e-12);
            prev = s;
        }
    }

    [Fact]
    public void Smootherstep_IsFlatterNearEndpoints_ThanSmoothstep()
    {
        // The Mach-band-killing property: near the endpoints smootherstep rises MORE SLOWLY than
        // smoothstep (its 2nd derivative vanishes at 0 and 1), so the feather edge has no abrupt
        // slope change for the eye to latch onto as a contour. Below the midpoint smootherstep <
        // smoothstep; above it smootherstep > smoothstep (steeper middle compensates).
        foreach (double t in new[] { 0.1, 0.2, 0.25, 0.4 })
        {
            double smoother = OverlayWindow.Smootherstep(t);
            double smooth   = t * t * (3.0 - 2.0 * t);
            Assert.True(smoother < smooth,
                $"smootherstep must be flatter than smoothstep below midpoint, t={t}: {smoother} vs {smooth}");
        }
        foreach (double t in new[] { 0.6, 0.75, 0.8, 0.9 })
        {
            double smoother = OverlayWindow.Smootherstep(t);
            double smooth   = t * t * (3.0 - 2.0 * t);
            Assert.True(smoother > smooth,
                $"smootherstep must be steeper than smoothstep above midpoint, t={t}: {smoother} vs {smooth}");
        }
    }

    [Fact]
    public void ResolveFieldAlpha_IsMonotonicAcrossBand()
    {
        double prev = -1;
        for (double r = 0.35; r <= 0.95; r += 0.02)
        {
            double a = OverlayWindow.ResolveFieldAlpha(r, 0.92, 0.35, 0.95);
            Assert.True(a >= prev - 1e-12, $"falloff should be monotonic, r={r}: {prev} -> {a}");
            prev = a;
        }
    }

    // ── Cell-fill fold (UVHS round 2, 2026-06-11 — replaces the field grid-term tests) ──
    //
    // The grid is no longer rasterized INTO the quarter-res field (a 1.2 px hairline cannot
    // survive the downscale — "grid FORM wrong / grid JUMPS"). It is now full-res WPF vector
    // geometry on a separate surface. The field carries ONLY low-frequency content: the FOV
    // mask dim WITH the legacy cell-fill tint folded in (the reference _cellFillRect's
    // #08080A@120 windowed by the SAME inverse-feather, composited UNDER the tier mask). These
    // pin the pure per-pixel fold ComposeFieldPixel(window, maskR/G/B, maskPeak, gamma,
    // cellStrength):
    //   (a) centre is CLEAN: at window 0 the composed pixel is fully transparent (no dim, no
    //       tint) — the centre-clean invariant survives the fold;
    //   (b) the periphery carries BOTH the mask dim AND the cell tint (composed alpha exceeds
    //       a mask-only field at the same window — the tint deepens the periphery tone to
    //       match 3762a42);
    //   (c) the fold is monotonic in the window (a deeper window ⇒ a deeper composed alpha);
    //   (d) composed alpha never exceeds 1.0 (premultiplied-safe encode);
    //   (e) the tint is modulated by cellStrength — the SAME ApplyGridIntensity target that
    //       drives GridCanvas.Opacity (at 3762a42 the _cellFillRect rode that opacity).
    //       cellStrength = 0 (L1 resting, tierFraction = 0) ⇒ tint contributes NOTHING, so
    //       the A2 hard-stop "resting 0.00 at L1" holds (no permanent peripheral vignette).

    private const double GGamma = SenseDefaults.OverlayFieldGamma;

    [Fact]
    public void ComposeFieldPixel_AtCentre_IsFullyTransparent_CentreClean()
    {
        // In the clean centre the shared window is 0 (r ≤ featherStart) — the cell-fill tint
        // AND the mask dim are both windowed by it, so the composed pixel is fully transparent.
        double windowCentre = OverlayWindow.ResolveFieldAlpha(0.0, 1.0, 0.35, 0.95);   // == 0
        Assert.Equal(0.0, windowCentre, 9);
        var (b, g, r, a) = OverlayWindow.ComposeFieldPixel(windowCentre, 0, 0, 4, 0.92, GGamma, 1.0);
        Assert.Equal(0, a);  // centre alpha 0 — clean hole (no dim, no tint)
        Assert.Equal(0, b);
        Assert.Equal(0, g);
        Assert.Equal(0, r);

        // Just inside the feather start the window is still 0 ⇒ still clean.
        double windowNearCentre = OverlayWindow.ResolveFieldAlpha(0.34, 1.0, 0.35, 0.95);
        var (_, _, _, a2) = OverlayWindow.ComposeFieldPixel(windowNearCentre, 0, 0, 4, 0.92, GGamma, 1.0);
        Assert.Equal(0, a2);
    }

    [Fact]
    public void ComposeFieldPixel_InPeriphery_CarriesMaskDimPlusCellTint()
    {
        // Far periphery (window 1.0): the composed pixel carries BOTH the tier mask dim and
        // the cell-fill tint folded under it. The composed alpha must EXCEED a mask-only field
        // at the same window — the tint deepens the periphery tone (the reference _cellFillRect
        // contribution), which is exactly what makes the periphery match 3762a42.
        double window = OverlayWindow.ResolveFieldAlpha(1.0, 1.0, 0.35, 0.95); // == 1.0
        Assert.Equal(1.0, window, 9);

        // Use a mid tier so there is headroom for the tint to add visible alpha (a 0.92 peak
        // gamma-shapes to a high mask alpha already; mid tier shows the fold clearly).
        double peak = 0.50;
        var (_, _, _, composedA) = OverlayWindow.ComposeFieldPixel(window, 51, 51, 51, peak, GGamma, 1.0);

        // Mask-only reference alpha at the same window (gamma-shaped, no cell-fill fold).
        byte maskOnlyA = (byte)Math.Round(
            Math.Clamp(OverlayWindow.GammaShape(window * peak, GGamma), 0.0, 1.0) * 255.0);

        Assert.True(composedA > 0, $"the periphery must carry the mask dim, alpha was {composedA}");
        Assert.True(composedA > maskOnlyA,
            $"the folded cell-fill tint must deepen the periphery tone past a mask-only field: " +
            $"composed={composedA}, maskOnly={maskOnlyA}");
    }

    [Fact]
    public void ComposeFieldPixel_AlphaIsMonotonic_InWindow()
    {
        // A deeper shared window ⇒ a deeper composed alpha (both the mask dim and the tint
        // scale WITH the window). Pin monotonicity across the band.
        int prevA = -1;
        for (double r = 0.35; r <= 1.0 + 1e-9; r += 0.02)
        {
            double window = OverlayWindow.ResolveFieldAlpha(r, 1.0, 0.35, 0.95);
            var (_, _, _, a) = OverlayWindow.ComposeFieldPixel(window, 0, 0, 4, 0.80, GGamma, 1.0);
            Assert.True(a >= prevA, $"composed alpha must be monotonic in window, r={r}: {prevA} -> {a}");
            prevA = a;
        }
    }

    [Fact]
    public void ComposeFieldPixel_NeverExceedsOpaque()
    {
        // Even at the deepest window + peak the composed alpha is premultiplied-safe (≤ 255)
        // and channels never exceed their alpha.
        var (b, g, r, a) = OverlayWindow.ComposeFieldPixel(1.0, 0, 0, 4, 0.92, GGamma, 1.0);
        Assert.InRange(a, (byte)0, (byte)255);
        Assert.True(b <= a && g <= a && r <= a,
            $"premultiplied channels must not exceed alpha: B={b} G={g} R={r} A={a}");
    }

    [Fact]
    public void ComposeFieldPixel_AtRest_TintContributesNothing_A2RestingInvariant()
    {
        // L1 resting: maskPeak = 0 (VimsLevels L1) AND cellStrength = 0 (the ApplyGridIntensity
        // target is tierFraction-driven — 0 at L1). The composed pixel must be FULLY transparent
        // even in the far periphery: no permanent vignette at rest (A2 hard-stop "resting 0.00
        // at L1"). This is the 3762a42 modulation — the tint rode GridCanvas.Opacity.
        var (b, g, r, a) = OverlayWindow.ComposeFieldPixel(1.0, 0, 0, 4, 0.0, GGamma, 0.0);
        Assert.Equal(0, a);
        Assert.Equal(0, b);
        Assert.Equal(0, g);
        Assert.Equal(0, r);

        // And with a live mask but zero cell strength, the field equals the mask-only value
        // (the tint adds nothing when its strength is zero).
        double window = 1.0;
        double peak = 0.50;
        var (_, _, _, composedA) = OverlayWindow.ComposeFieldPixel(window, 51, 51, 51, peak, GGamma, 0.0);
        byte maskOnlyA = (byte)Math.Round(
            Math.Clamp(OverlayWindow.GammaShape(window * peak, GGamma), 0.0, 1.0) * 255.0);
        Assert.Equal(maskOnlyA, composedA);
    }

    // ── MaskDepth (D-2) → bounded effective peak ────────────────────────────

    [Fact]
    public void ResolveEffectivePeakAlpha_AtDefaultMaskDepth_IsTableIdentical()
    {
        // Multiplier is exactly 1.0 at the default ⇒ effective == the tier ceiling, byte-identical.
        foreach (var (_, _, _, tableAlpha) in OverlayWindow.VimsLevels)
        {
            double eff = OverlayWindow.ResolveEffectivePeakAlpha(
                tableAlpha, ShaderParams.DefaultMaskDepth, maskActive: tableAlpha > 0);
            // For a commanded-on tier the floor (min(0.20, ceiling)) may apply; at the
            // default the multiplier is identity so effective == ceiling whenever ceiling
            // >= floor, and == ceiling (its own table value) when ceiling < floor.
            if (tableAlpha == 0.0) { Assert.Equal(0.0, eff, 9); continue; }
            double expected = Math.Max(Math.Min(0.20, tableAlpha), tableAlpha);
            Assert.Equal(expected, eff, 9);
        }
    }

    [Fact]
    public void ResolveEffectivePeakAlpha_NeverExceedsTableCeiling()
    {
        // Even MaskDepth driven to 1.0 cannot push the effective peak above the tier ceiling.
        Assert.Equal(0.92, OverlayWindow.ResolveEffectivePeakAlpha(0.92, 1.0, maskActive: true), 9);
        Assert.Equal(0.50, OverlayWindow.ResolveEffectivePeakAlpha(0.50, 1.0, maskActive: true), 9);
    }

    [Fact]
    public void ResolveEffectivePeakAlpha_NeverBelowFloor_WhenMaskOn()
    {
        // MaskDepth driven to 0 with a high ceiling: the floor catches it at 0.20.
        Assert.Equal(SenseDefaults.OverlayMaskEffectiveAlphaFloor,
            OverlayWindow.ResolveEffectivePeakAlpha(0.80, 0.0, maskActive: true), 9);
        Assert.Equal(0.20, OverlayWindow.ResolveEffectivePeakAlpha(0.92, 0.0, maskActive: true), 9);
    }

    [Fact]
    public void ResolveEffectivePeakAlpha_FloorNeverRaisesAboveOwnTier()
    {
        // A tier whose ceiling is BELOW 0.20 (e.g. L2 0.14) is never pushed above its own
        // table value by the floor — the floor is min(0.20, ceiling).
        Assert.Equal(0.14, OverlayWindow.ResolveEffectivePeakAlpha(0.14, 0.0, maskActive: true), 9);
        Assert.True(OverlayWindow.ResolveEffectivePeakAlpha(0.14, 1.0, maskActive: true) <= 0.14 + 1e-9);
    }

    [Fact]
    public void ResolveEffectivePeakAlpha_MaskOff_FloorDoesNotApply()
    {
        // Commanded off ⇒ no floor; L1's 0.00 stays 0.00.
        Assert.Equal(0.0, OverlayWindow.ResolveEffectivePeakAlpha(0.0, ShaderParams.DefaultMaskDepth, maskActive: false), 9);
    }

    [Fact]
    public void ResolveEffectivePeakAlpha_ShallowerDepth_LowersEffective_WhenHeadroom()
    {
        // With headroom above the floor, a shallower depth lowers the effective peak.
        double deep    = OverlayWindow.ResolveEffectivePeakAlpha(0.80, ShaderParams.DefaultMaskDepth, maskActive: true);
        double shallow = OverlayWindow.ResolveEffectivePeakAlpha(0.80, 0.55, maskActive: true);
        Assert.True(shallow < deep, $"shallower MaskDepth should lower effective peak: deep={deep}, shallow={shallow}");
        Assert.True(shallow >= SenseDefaults.OverlayMaskEffectiveAlphaFloor - 1e-9);
    }

    // ── FeatherWidth (D-2) → band start slide ───────────────────────────────

    [Fact]
    public void ResolveFeatherStart_AtDefault_Is035()
    {
        Assert.Equal(0.35, OverlayWindow.ResolveFeatherStart(ShaderParams.DefaultFeatherWidth), 9);
    }

    [Fact]
    public void ResolveFeatherStart_Wider_PullsStartInward_Narrower_PushesOut()
    {
        double wide   = OverlayWindow.ResolveFeatherStart(0.90);
        double narrow = OverlayWindow.ResolveFeatherStart(0.10);
        Assert.True(wide < 0.35, $"wider feather should pull start inward, was {wide}");
        Assert.True(narrow > 0.35, $"narrower feather should push start outward, was {narrow}");
    }

    [Fact]
    public void ResolveFeatherStart_StaysWithinSafeClamps()
    {
        for (double fw = 0.0; fw <= 1.0; fw += 0.05)
        {
            double s = OverlayWindow.ResolveFeatherStart(fw);
            Assert.InRange(s, 0.05, 0.95 - 0.05);
        }
    }

    // ── Gamma shaping (banding mitigation, no dithering) ────────────────────

    [Fact]
    public void GammaShape_IsMonotonic_AndPinsEndpoints()
    {
        Assert.Equal(0.0, OverlayWindow.GammaShape(0.0, SenseDefaults.OverlayFieldGamma), 9);
        Assert.Equal(1.0, OverlayWindow.GammaShape(1.0, SenseDefaults.OverlayFieldGamma), 9);

        double prev = -1;
        for (double a = 0.0; a <= 1.0; a += 0.05)
        {
            double g = OverlayWindow.GammaShape(a, SenseDefaults.OverlayFieldGamma);
            Assert.True(g >= prev - 1e-12, $"gamma shape should be monotonic, a={a}: {prev} -> {g}");
            Assert.InRange(g, 0.0, 1.0 + 1e-9);
            prev = g;
        }
    }

    [Fact]
    public void GammaShape_GreaterThanOne_PullsMidtonesDown()
    {
        // With gamma > 1 the near-black band is compressed (codes shifted toward 0),
        // distributing the limited 8-bit codes where the eye is most sensitive.
        Assert.True(OverlayWindow.GammaShape(0.5, 2.2) < 0.5);
    }

    [Fact]
    public void GammaShape_Identity_AtGammaOne()
    {
        for (double a = 0.0; a <= 1.0; a += 0.1)
            Assert.Equal(a, OverlayWindow.GammaShape(a, 1.0), 9);
    }

    // ── FIX 1 (Kirby round-4, 2026-06-11): HALVE the corner-approach growth ─────────
    //
    // The field normalizes each pixel's radial coordinate by the distance to the farthest
    // screen corner FROM THE CURRENT CENTER (the CornerNormR denominator). As the One-Euro
    // center approaches an edge/corner that denominator GROWS, enlarging the clear hole +
    // feather band (the "corner-approach zoom"). The renderer now blends the live denominator
    // toward the REST denominator: effectiveDenom = restDenom + (liveDenom − restDenom)·damping.
    // These pin: (a) byte-identity at rest (damping inert when liveDenom == restDenom); (b) HALF
    // the growth at a full corner; (c) protection never weakens (the farthest screen corner's
    // normalized r still saturates the feather to full dim — r ≥ 1).

    private const double Damp = SenseDefaults.OverlayFieldCornerGrowthDamping;

    [Fact]
    public void EffectiveCornerDenom_AtRestCenter_EqualsRestDenom_ForAnyDamping()
    {
        // INVARIANT (a): at the rest center the live denom IS the rest denom, so the blend is a
        // no-op for ANY damping — the field is byte-identical to today when not moving.
        foreach (double aspect in new[] { 16.0 / 9.0, 1.0, 21.0 / 9.0 })
        {
            double rest = OverlayWindow.RestCornerDenom(aspect);
            foreach (double d in new[] { 0.0, 0.5, 1.0 })
                Assert.Equal(rest, OverlayWindow.EffectiveCornerDenom(0.5, 0.5, aspect, d), 12);
        }
    }

    [Fact]
    public void EffectiveCornerDenom_AtRest_FieldIsByteIdenticalToUndampedCornerNormR()
    {
        // At rest the damped r equals the original CornerNormR exactly (the field byte-identity
        // hinges on this): for every sampled pixel, dist/effectiveDenom == dist/liveDenom.
        double aspect = 16.0 / 9.0;
        double denom = OverlayWindow.EffectiveCornerDenom(0.5, 0.5, aspect, Damp);
        for (double px = 0.0; px <= 1.0; px += 0.13)
            for (double py = 0.0; py <= 1.0; py += 0.13)
            {
                double dx = (px - 0.5) * aspect, dy = py - 0.5;
                double rDamped = Math.Sqrt(dx * dx + dy * dy) / denom;
                double rOrig   = OverlayWindow.CornerNormR(px, py, 0.5, 0.5, aspect);
                Assert.Equal(rOrig, rDamped, 12);
            }
    }

    [Fact]
    public void EffectiveCornerDenom_AtFullCorner_GrowsByHalfTheOldGrowth()
    {
        // INVARIANT (b): the denominator GROWTH (over rest) at a full-corner center is halved.
        // FullFollow lets the center reach (1,1). Old growth = liveDenom − restDenom; the
        // damped effective denom must sit exactly half-way: restDenom + 0.5·(liveDenom−restDenom).
        double aspect = 16.0 / 9.0;
        double rest = OverlayWindow.RestCornerDenom(aspect);
        double live = OverlayWindow.LiveCornerDenom(1.0, 1.0, aspect);
        double oldGrowth = live - rest;
        Assert.True(oldGrowth > 0, "the live denom must grow at a full corner");

        double eff = OverlayWindow.EffectiveCornerDenom(1.0, 1.0, aspect, Damp);
        double newGrowth = eff - rest;
        // Exactly half the old growth at damping 0.5 (assert the arithmetic directly).
        Assert.Equal(oldGrowth * Damp, newGrowth, 12);
        Assert.Equal(oldGrowth * 0.5, newGrowth, 12);
    }

    [Fact]
    public void EffectiveCornerDenom_HoleRadius_GrowsByHalf_InScreenUnits()
    {
        // The clear-hole edge sits at r == featherStart, i.e. screen radius = featherStart·denom.
        // Halving the denominator growth halves the hole-radius growth — pin it in screen units.
        double aspect = 16.0 / 9.0;
        const double featherStart = 0.35;
        double restHole = featherStart * OverlayWindow.RestCornerDenom(aspect);
        double liveHole = featherStart * OverlayWindow.LiveCornerDenom(1.0, 1.0, aspect);
        double dampHole = featherStart * OverlayWindow.EffectiveCornerDenom(1.0, 1.0, aspect, Damp);

        double oldHoleGrowth  = liveHole - restHole;
        double dampHoleGrowth = dampHole - restHole;
        Assert.Equal(oldHoleGrowth * 0.5, dampHoleGrowth, 12);
    }

    [Fact]
    public void EffectiveCornerDenom_ProtectionNeverWeakens_FarthestCornerStaysFullyDimmed()
    {
        // INVARIANT (c): with the center at an extreme, the FARTHEST screen corner pixel must
        // still render FULLY dimmed. A smaller (damped) denominator makes that corner's
        // normalized r LARGER, not smaller — so r ≥ 1 (≥ featherEnd 0.95) and ResolveFieldAlpha
        // SATURATES to the peak. Damping can only deepen protection, never weaken it.
        double aspect = 16.0 / 9.0;
        double cx = 0.9, cy = 0.9;                  // center near a corner
        double denom = OverlayWindow.EffectiveCornerDenom(cx, cy, aspect, Damp);

        // The farthest screen corner from (0.9,0.9) is (0,0).
        double dx = (0.0 - cx) * aspect, dy = 0.0 - cy;
        double rCorner = Math.Sqrt(dx * dx + dy * dy) / denom;
        Assert.True(rCorner >= 1.0,
            $"the damped denom must push the farthest corner to r >= 1 (saturated), was {rCorner}");

        // ResolveFieldAlpha saturates for r >= featherEnd: the corner alpha == the peak mask value.
        const double peak = 0.92, featherStart = 0.35, featherEnd = 0.95;
        double cornerAlpha = OverlayWindow.ResolveFieldAlpha(rCorner, peak, featherStart, featherEnd);
        Assert.Equal(peak, cornerAlpha, 12);

        // And the damped corner alpha is >= the UNDAMPED corner alpha (protection only deepens).
        double liveDenom = OverlayWindow.LiveCornerDenom(cx, cy, aspect);
        double rCornerUndamped = Math.Sqrt(dx * dx + dy * dy) / liveDenom;
        double undampedAlpha = OverlayWindow.ResolveFieldAlpha(rCornerUndamped, peak, featherStart, featherEnd);
        Assert.True(cornerAlpha >= undampedAlpha - 1e-12,
            $"damping must not weaken corner protection: damped={cornerAlpha}, undamped={undampedAlpha}");
    }

    [Fact]
    public void ResolveFieldAlpha_SaturatesForRAboveOne()
    {
        // Explicit pin that the feather SATURATES (clamps) for r > 1 — the post-damping
        // far-corner regime. Anything past featherEnd holds at exactly the peak.
        const double peak = 0.92;
        Assert.Equal(peak, OverlayWindow.ResolveFieldAlpha(1.0, peak, 0.35, 0.95), 12);
        Assert.Equal(peak, OverlayWindow.ResolveFieldAlpha(1.5, peak, 0.35, 0.95), 12);
        Assert.Equal(peak, OverlayWindow.ResolveFieldAlpha(3.0, peak, 0.35, 0.95), 12);
    }

    // ── FIX 2 (Kirby round-4): LUT accuracy — < 1/512 (half an 8-bit step) ─────────
    //
    // The per-pixel Math.Pow (gamma, ×2/px) and Math.Sqrt (CornerNormR, ×1/px) are replaced by
    // linear-interpolated table lookups. The LUT must reproduce the exact math within half an
    // 8-bit step (1/512) across the whole domain so it can NEVER drift the rendered field
    // visibly. These sweep the LUTs against the exact curves and pin the bound.

    private const double HalfByteStep = 1.0 / 512.0;

    [Fact]
    public void GammaLut_MatchesExactMath_WithinHalfAStep()
    {
        // Sweep the gamma LUT against Math.Pow(a, 2.2) across [0,1] at fine density; the max abs
        // error must stay under half an 8-bit step (so a rounded byte can differ by at most 1).
        double maxErr = 0;
        double worstAt = -1;
        for (double a = 0.0; a <= 1.0; a += 1.0 / 4096.0)
        {
            double exact = OverlayWindow.GammaShape(a, SenseDefaults.OverlayFieldGamma);
            double lut   = OverlayWindow.GammaLutLookup(a);
            double err   = Math.Abs(exact - lut);
            if (err > maxErr) { maxErr = err; worstAt = a; }
        }
        Assert.True(maxErr < HalfByteStep,
            $"gamma LUT max abs error {maxErr:E4} (at a={worstAt:F4}) must be < 1/512 ({HalfByteStep:E4})");
    }

    [Fact]
    public void WindowLut_MatchesExactMath_WithinHalfAStep()
    {
        // Build the window LUT at the default feather start (0.35→0.95) and sweep it against the
        // exact ResolveFieldAlpha(sqrt(r²),1,0.35,0.95) across r² ∈ [0,1].
        const int size = 2048;
        var lut = new float[size + 1];
        OverlayWindow.BuildWindowLut(lut, size, 0.35, 0.95);

        double maxErr = 0;
        double worstAt = -1;
        for (double r2 = 0.0; r2 <= 1.0; r2 += 1.0 / 4096.0)
        {
            double r = Math.Sqrt(r2);
            double exact = OverlayWindow.ResolveFieldAlpha(r, 1.0, 0.35, 0.95);
            double lut2  = OverlayWindow.WindowLutLookup(lut, size, r2);
            double err   = Math.Abs(exact - lut2);
            if (err > maxErr) { maxErr = err; worstAt = r2; }
        }
        Assert.True(maxErr < HalfByteStep,
            $"window LUT max abs error {maxErr:E4} (at r²={worstAt:F4}) must be < 1/512 ({HalfByteStep:E4})");
    }

    [Fact]
    public void WindowLut_SaturatesPastFeatherEnd_ForRSquaredAboveOne()
    {
        // r² ≥ 1 (the post-damping far-corner regime) clamps to the saturated last entry (1.0).
        const int size = 2048;
        var lut = new float[size + 1];
        OverlayWindow.BuildWindowLut(lut, size, 0.35, 0.95);
        Assert.Equal(1.0, OverlayWindow.WindowLutLookup(lut, size, 1.0), 9);
        Assert.Equal(1.0, OverlayWindow.WindowLutLookup(lut, size, 2.5), 9);
    }

    [Fact]
    public void ComposeFieldPixelLut_MatchesExactCompose_WithinOneByte()
    {
        // The LUT-backed compose (what the hot loop runs) must reproduce the exact-math
        // ComposeFieldPixel within ONE byte per channel across a representative grid of
        // (window, peak, cellStrength, color). The < 1/512 LUT error cannot flip a rounded byte
        // by more than 1, so the rendered field is visually indistinguishable.
        double gamma = SenseDefaults.OverlayFieldGamma;
        (byte, byte, byte)[] colors = { (0, 0, 4), (51, 51, 51), (102, 102, 102), (20, 20, 24) };
        int maxDiff = 0;
        foreach (var (mr, mg, mb) in colors)
            foreach (double window in new[] { 0.0, 0.05, 0.2, 0.4, 0.6, 0.8, 0.95, 1.0 })
                foreach (double peak in new[] { 0.0, 0.14, 0.5, 0.8, 0.92 })
                    foreach (double cs in new[] { 0.0, 0.4, 1.0 })
                    {
                        var exact = OverlayWindow.ComposeFieldPixel(window, mr, mg, mb, peak, gamma, cs);
                        var lut   = OverlayWindow.ComposeFieldPixelLut(window, mr, mg, mb, peak, cs);
                        maxDiff = Math.Max(maxDiff, Math.Abs(exact.B - lut.B));
                        maxDiff = Math.Max(maxDiff, Math.Abs(exact.G - lut.G));
                        maxDiff = Math.Max(maxDiff, Math.Abs(exact.R - lut.R));
                        maxDiff = Math.Max(maxDiff, Math.Abs(exact.A - lut.A));
                    }
        Assert.True(maxDiff <= 1, $"LUT compose must be within 1 byte of exact math, max diff was {maxDiff}");
    }

    /// <summary>Parked-storm fix (2026-06-11) invisibility, part A: snapping cellStrength to the
    /// 1/256 grid (what ApplyGridIntensity does before the field re-dirt gate) changes the composed
    /// premultiplied pixel by AT MOST one 8-bit code vs the exact un-quantized strength. So the
    /// quantization the storm gate relies on is below one 8-bit step end-to-end — invisible (Kirby:
    /// the change must be invisible, not a visible step). cellStrength reaches the buffer scaled by
    /// CellFillAlpha (≈0.47) × window × gamma, so a 1/256 snap is already heavily attenuated; the
    /// only ever-1-byte case is the unavoidable rounding-boundary flip. Sweep window/peak/color and
    /// a fine cellStrength grid.</summary>
    [Fact]
    public void Quantize8Bit_CellStrengthSnap_ComposesWithinOneByte()
    {
        (byte, byte, byte)[] colors = { (0, 0, 4), (51, 51, 51), (102, 102, 102), (20, 20, 24) };
        int maxDiff = 0;
        foreach (var (mr, mg, mb) in colors)
            foreach (double window in new[] { 0.0, 0.2, 0.5, 0.8, 1.0 })
                foreach (double peak in new[] { 0.0, 0.2, 0.5, 0.92 })
                    for (int k = 0; k <= 200; k++)
                    {
                        double cs = k / 200.0;                       // exact strength
                        double qcs = OverlayWindow.Quantize8Bit(cs); // what the gate stores
                        var exact = OverlayWindow.ComposeFieldPixelLut(window, mr, mg, mb, peak, cs);
                        var snap  = OverlayWindow.ComposeFieldPixelLut(window, mr, mg, mb, peak, qcs);
                        maxDiff = Math.Max(maxDiff, Math.Abs(exact.B - snap.B));
                        maxDiff = Math.Max(maxDiff, Math.Abs(exact.G - snap.G));
                        maxDiff = Math.Max(maxDiff, Math.Abs(exact.R - snap.R));
                        maxDiff = Math.Max(maxDiff, Math.Abs(exact.A - snap.A));
                    }
        Assert.True(maxDiff <= 1,
            $"snapping cellStrength to 1/256 must change the composed pixel by <= 1 byte " +
            $"(invisible); max diff was {maxDiff}");
    }

    /// <summary>Parked-storm fix (2026-06-11) invisibility, part B: two cellStrength inputs that
    /// quantize to the SAME 1/256 code compose to the BYTE-IDENTICAL pixel — this is exactly the
    /// case the storm gate exploits (the EMA wobbling WITHIN one code is dropped, no re-dirt). For
    /// each code, jitter the strength by ±0.49 quantum (staying in the same bucket) and confirm the
    /// composed pixels are bit-for-bit equal.</summary>
    // ── BANDING ROUND-5 (2026-06-11): DEVICE-PIXEL field resolution ─────────────────
    //
    // EnsureFieldBitmap now sizes the field from PHYSICAL device pixels (DIP extent ×
    // CompositionTarget.TransformToDevice.M11/M22 — the SAME DPI dance DrawGrid does) divided by
    // OverlayFieldDownscale, instead of from WPF DIPs. At 200% scaling that quadruples the field
    // pixel count (360×225 → 720×450), halving every banding band's on-screen width and doubling
    // the gradient sample density. These pin the sizing arithmetic + the adaptive-downscale step
    // for very large screens. (The live EnsureFieldBitmap reads a real PresentationSource; an
    // unshown STA test window has none, so it falls back to DPI 1.0 — that fallback is exactly why
    // every existing STA test keeps its prior dimensions. Here we pin the pure derived math.)

    /// <summary>The device-pixel field dimension is the DIP extent × DPI scale ÷ downscale
    /// (ceil). At 200% scaling a 1440×900-DIP window (2880×1800 physical) /4 ⇒ 720×450 — 4× the
    /// DIP-sized 360×225 the pre-round-5 code produced. At 100% (DPI 1.0) the result is the
    /// UNCHANGED DIP sizing (byte-identical to before — no regression on non-scaled machines).</summary>
    [Fact]
    public void DevicePixelFieldDims_AreDipExtentTimesDpiOverDownscale()
    {
        // Mirror EnsureFieldBitmap's per-axis sizing: ds is resolved from the FULL physical
        // extent (both axes), then each axis dim is ceil(physAxis / ds).
        (int w, int h) Dims(double dipW, double dipH, double dpi)
        {
            double physW = dipW * dpi, physH = dipH * dpi;
            int ds = OverlayWindow.ResolveFieldDownscale(physW, physH);
            return (Math.Max(1, (int)Math.Ceiling(physW / ds)),
                    Math.Max(1, (int)Math.Ceiling(physH / ds)));
        }

        // 200% scaling: a 1440×900-DIP window → 2880×1800 physical /4 = 720×450 (4× the DIP-sized
        // pixel count). 720×450 = 324k field px ≤ the 480k cap ⇒ the base /4 holds.
        Assert.Equal((720, 450), Dims(1440, 900, 2.0));
        // The DIP-sized baseline the OLD code produced at the same window (÷4 of the DIP extent).
        Assert.Equal(360, (int)Math.Ceiling(1440 / 4.0));
        Assert.Equal(225, (int)Math.Ceiling(900 / 4.0));

        // 100% scaling (DPI 1.0): device-pixel sizing == the old DIP sizing, unchanged.
        Assert.Equal((360, 270), Dims(1440, 1080, 1.0));
    }

    /// <summary>ResolveFieldDownscale keeps the base factor (4) for every common panel up to true
    /// 4K-at-200%, and steps the downscale UP only when the field would otherwise exceed the
    /// device-pixel cap — so the band-path recompute stays bounded on a very large screen while a
    /// normal screen keeps the finest (÷4) field. Never returns below the base.</summary>
    [Fact]
    public void ResolveFieldDownscale_StepsUpOnlyForVeryLargeScreens()
    {
        int baseDs = SenseDefaults.OverlayFieldDownscale;

        // 2880×1800 physical (1440×900 DIP @200%) — 324k px @ /4, under the cap ⇒ base downscale.
        Assert.Equal(baseDs, OverlayWindow.ResolveFieldDownscale(2880, 1800));
        // 1920×1080 physical @100% — well under the cap ⇒ base downscale.
        Assert.Equal(baseDs, OverlayWindow.ResolveFieldDownscale(1920, 1080));
        // 3840×2160 physical @100% — 518k px @ /4 exceeds the 480k cap ⇒ steps to /5 (768×432).
        int big = OverlayWindow.ResolveFieldDownscale(3840, 2160);
        Assert.True(big > baseDs, $"a true-4K physical field must step the downscale up, was {big}");
        Assert.True((3840.0 / big) * (2160.0 / big) <= SenseDefaults.OverlayFieldMaxFieldPixels,
            "the stepped-up field must fit under the device-pixel cap");

        // The result is never below the base no matter how tiny the screen.
        Assert.True(OverlayWindow.ResolveFieldDownscale(640, 480) >= baseDs);
    }

    [Fact]
    public void Quantize8Bit_SameBucket_ComposesByteIdentical()
    {
        (byte, byte, byte)[] colors = { (0, 0, 4), (51, 51, 51), (20, 20, 24) };
        double q = SenseDefaults.OverlayEightBitQuantum;
        foreach (var (mr, mg, mb) in colors)
            foreach (double window in new[] { 0.0, 0.5, 1.0 })
                foreach (double peak in new[] { 0.0, 0.5, 0.92 })
                    for (int code = 0; code <= 256; code++)
                    {
                        double centre = code * q;                 // exactly on the grid
                        double a = OverlayWindow.Quantize8Bit(Math.Clamp(centre + q * 0.49, 0, 1));
                        double b = OverlayWindow.Quantize8Bit(Math.Clamp(centre - q * 0.49, 0, 1));
                        if (a != b) continue;                     // skip the clamp endpoints
                        var pa = OverlayWindow.ComposeFieldPixelLut(window, mr, mg, mb, peak, a);
                        var pb = OverlayWindow.ComposeFieldPixelLut(window, mr, mg, mb, peak, b);
                        Assert.True(pa == pb,
                            $"same-bucket cellStrength must compose byte-identical at code={code}, " +
                            $"window={window}, peak={peak}, color=({mr},{mg},{mb}): {pa} != {pb}");
                    }
    }
}
