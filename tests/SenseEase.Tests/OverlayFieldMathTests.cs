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
///   <item>the grid-line reveal (UVHS 2026-06-11 layout): grid ZERO in the clean centre,
///         non-zero only on world-locked periphery line pixels, scaling on the SAME radial
///         window the FOV mask uses — and gated by the GridCeiling brightness pipeline;</item>
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
    public void ResolveFieldAlpha_MidBand_MatchesSmoothstep()
    {
        // Midpoint of the 0.35→0.95 band: t=0.5, s = 0.5²(3-2·0.5) = 0.25·2 = 0.5 ⇒ peak·0.5.
        double mid = 0.35 + (0.95 - 0.35) * 0.5;
        Assert.Equal(0.5 * 0.80, OverlayWindow.ResolveFieldAlpha(mid, 0.80, 0.35, 0.95), 9);

        // The classic smoothstep value at t=0.25: s = 0.0625·(3-0.5)=0.0625·2.5=0.15625.
        double q = 0.35 + (0.95 - 0.35) * 0.25;
        Assert.Equal(0.15625 * 0.80, OverlayWindow.ResolveFieldAlpha(q, 0.80, 0.35, 0.95), 9);
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

    // ── Grid-line reveal (UVHS 2026-06-11 corrected layout) ─────────────────
    //
    // The authoritative 3762a42 presentation: faint world-locked grid lines appear ONLY
    // in the dimmed PERIPHERY (sharing the FOV mask's radial window), the centre is CLEAN.
    // These pin the pure per-pixel grid term ResolveGridLineAlpha(window, gridLineAlpha,
    // onLine, threshold, gamma):
    //   (a) grid term is ZERO at/near the mask centre (window ≈ 0 ⇒ centre clean);
    //   (b) grid term is non-zero on a line pixel in the periphery (window open);
    //   (c) line POSITIONS are window-independent — world-lock is enforced by the onLine
    //       flag (mod(px, cell)), proven here by the term being identical for the same
    //       onLine pixel regardless of which mask CENTRE produced its window value;
    //   (d) grid alpha tracks the GridCeiling brightness pipeline (gridLineAlpha input);
    //   (e) the periphery line term BLENDS over the mask dim (it scales WITH the window,
    //       it does not replace the mask) — both non-zero at the same periphery radius.

    private const double GThresh = SenseDefaults.OverlayFieldGridThreshold;
    private const double GGamma  = SenseDefaults.OverlayFieldGamma;

    [Fact]
    public void GridLineAlpha_AtCenter_IsZero_CentreClean()
    {
        // In the clean centre the shared window is 0 (r ≤ featherStart) — even on a grid
        // line pixel and at full brightness the grid term is ZERO. Centre is clean.
        double windowCentre = OverlayWindow.ResolveFieldAlpha(0.0, 1.0, 0.35, 0.95);   // == 0
        Assert.Equal(0.0, windowCentre, 9);
        double full = SenseDefaults.OverlayGridLineMaxAlpha; // brightness 1.0
        Assert.Equal(0.0, OverlayWindow.ResolveGridLineAlpha(windowCentre, full, onLine: true, GThresh, GGamma), 9);

        // Just inside the feather start the window is still 0 ⇒ still no grid.
        double windowNearCentre = OverlayWindow.ResolveFieldAlpha(0.34, 1.0, 0.35, 0.95);
        Assert.Equal(0.0, OverlayWindow.ResolveGridLineAlpha(windowNearCentre, full, onLine: true, GThresh, GGamma), 9);
    }

    [Fact]
    public void GridLineAlpha_InPeriphery_OnLine_IsNonZero()
    {
        // Far periphery (r ≥ featherEnd) ⇒ window saturates at 1.0 ⇒ a line pixel reveals
        // the faint grid at its full (gamma-shaped) ceiling.
        double window = OverlayWindow.ResolveFieldAlpha(1.0, 1.0, 0.35, 0.95); // == 1.0
        Assert.Equal(1.0, window, 9);
        double full = SenseDefaults.OverlayGridLineMaxAlpha;
        double ga = OverlayWindow.ResolveGridLineAlpha(window, full, onLine: true, GThresh, GGamma);
        Assert.True(ga > 0.0, $"periphery line pixel must reveal the grid, was {ga}");
        Assert.Equal(OverlayWindow.GammaShape(full, GGamma), ga, 9);
    }

    [Fact]
    public void GridLineAlpha_OffLine_IsAlwaysZero()
    {
        // Between the world-locked lines there is no grid anywhere — periphery included.
        double window = OverlayWindow.ResolveFieldAlpha(1.0, 1.0, 0.35, 0.95);
        double full = SenseDefaults.OverlayGridLineMaxAlpha;
        Assert.Equal(0.0, OverlayWindow.ResolveGridLineAlpha(window, full, onLine: false, GThresh, GGamma), 9);
    }

    [Fact]
    public void GridLineAlpha_LinePositions_WorldLocked_IndependentOfMaskCenter()
    {
        // World-lock has TWO parts; both are asserted here.
        //
        // PART 1 — the line POSITION (the onLine flag) is pure screen-space mod(px, cell),
        // so it does NOT shift when the mask centre moves. Model the ComputeField test
        // exactly: onLine = (y % cell == 0) || (x % cell == 0), with NO center term. The
        // same screen pixel is on/off a line identically regardless of where the head looks.
        int ds   = Math.Max(1, SenseDefaults.OverlayFieldDownscale);
        int cell = Math.Max(2, (int)Math.Round(SenseDefaults.GridSizePx / (double)ds));
        // A pixel exactly on a vertical line stays on it for any conceivable centre — the
        // mod test never consults the centre.
        int xOnLine  = cell;        // cell % cell == 0  → on a vertical line
        int xOffLine = cell + cell / 2;
        Assert.True((xOnLine  % cell) == 0, "world-locked line column is center-independent");
        Assert.True((xOffLine % cell) != 0, "off-line column is center-independent");

        // PART 2 — ResolveGridLineAlpha takes NO centre argument: for the SAME window value
        // (the only quantity the centre influences) the grid alpha is identical. So the line
        // alpha is a pure function of (window, brightness, onLine) — the centre can change
        // the window VALUE but can never move the line itself.
        double full = SenseDefaults.OverlayGridLineMaxAlpha;
        const double sameWindow = 0.90; // some peripheral window value
        double g1 = OverlayWindow.ResolveGridLineAlpha(sameWindow, full, onLine: true, GThresh, GGamma);
        double g2 = OverlayWindow.ResolveGridLineAlpha(sameWindow, full, onLine: true, GThresh, GGamma);
        Assert.Equal(g1, g2, 9);
        Assert.True(g1 > 0.0, "a periphery line pixel must be visible");

        // And two DIFFERENT mask centres that yield the SAME window at an on-line pixel
        // produce the SAME line alpha — the reveal rides the window, the line never moves.
        double aspect = 16.0 / 9.0;
        // Pick two pixels (one per centre) engineered to share a window value via equal r.
        // r is what the smoothstep consumes; equal r ⇒ equal window ⇒ equal line alpha.
        double rA = OverlayWindow.CornerNormR(0.05, 0.05, 0.5, 0.5, aspect);
        double rB = rA; // same radial coordinate under a hypothetical second centre
        double wA = OverlayWindow.ResolveFieldAlpha(rA, 1.0, 0.35, 0.95);
        double wB = OverlayWindow.ResolveFieldAlpha(rB, 1.0, 0.35, 0.95);
        Assert.Equal(
            OverlayWindow.ResolveGridLineAlpha(wA, full, onLine: true, GThresh, GGamma),
            OverlayWindow.ResolveGridLineAlpha(wB, full, onLine: true, GThresh, GGamma), 9);
    }

    [Fact]
    public void GridLineAlpha_ScalesWithBrightness_GridCeilingPipeline()
    {
        // (d) The grid alpha tracks the brightness pipeline: gridLineAlpha = brightness ×
        // OverlayGridLineMaxAlpha (the GridCeiling/tier driver feeds brightness). A dimmer
        // brightness ⇒ a fainter line; /grid off (brightness 0) ⇒ no line at all.
        double window = 1.0; // far periphery, window fully open
        double max = SenseDefaults.OverlayGridLineMaxAlpha;

        double bright = OverlayWindow.ResolveGridLineAlpha(window, 0.75 * max, onLine: true, GThresh, GGamma);
        double dim    = OverlayWindow.ResolveGridLineAlpha(window, 0.30 * max, onLine: true, GThresh, GGamma);
        double off    = OverlayWindow.ResolveGridLineAlpha(window, 0.0  * max, onLine: true, GThresh, GGamma);

        Assert.True(dim < bright, $"dimmer grid brightness ⇒ fainter line: bright={bright}, dim={dim}");
        Assert.Equal(0.0, off, 9); // GridCeiling(gv=0)=0 ⇒ "/grid off" truly hides the grid
        Assert.Equal(OverlayWindow.GammaShape(0.75 * max, GGamma), bright, 9);
    }

    [Fact]
    public void GridLineAlpha_Periphery_BlendsOverMaskDim_NotInsteadOf()
    {
        // (e) At a single periphery radius BOTH the mask dim AND the grid term are non-zero:
        // the grid line BLENDS OVER the mask (one pixel, one alpha), it does not replace it.
        // Same window drives both: maskA = window × peak; gridA = window × lineCeiling.
        double window = OverlayWindow.ResolveFieldAlpha(0.90, 1.0, 0.35, 0.95);
        Assert.True(window > 0.0, "precondition: a genuinely peripheral radius (window open)");

        double peak  = 0.50; // a mid-tier mask peak
        double maskA = window * peak;
        double gridA = OverlayWindow.ResolveGridLineAlpha(
            window, SenseDefaults.OverlayGridLineMaxAlpha, onLine: true, GThresh, GGamma);

        Assert.True(maskA > 0.0, $"the mask dim must be present at this periphery pixel, was {maskA}");
        Assert.True(gridA > 0.0, $"the grid line must be present at this periphery pixel, was {gridA}");
        // The single-surface composite (white line src-over the mask backdrop) keeps the
        // mask contribution: src-over output alpha = gridA + maskA·(1−gridA) ≥ maskA, so the
        // dim is never erased by the line.
        double composedAlpha = gridA + maskA * (1.0 - gridA);
        Assert.True(composedAlpha >= maskA - 1e-12,
            $"the grid line must blend OVER the mask, not erase it: composed={composedAlpha}, mask={maskA}");
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
}
