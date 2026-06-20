using SenseEase.Core.Configuration;
using SenseEase.Core.Models;

namespace SenseEase.Core.AdaptiveFit;

/// <summary>
/// The deterministic 10×5 = 50-set Adaptive Fit grid (Alpha-2 P3/P4, GROUP A foundation).
///
/// <para><b>Gated by <c>AppConfig.AdaptiveFitSelectorEnabled</c> (default TRUE since c1461a9).</b> Nothing
/// here is read on the hot path unless that flag is on. With the flag OFF the engine behaves
/// byte-identically to today and never consults this grid.</para>
///
/// <para><b>set0 ≡ today (the pinned invariant).</b> <see cref="GridCoord.Set0"/> =
/// <c>(Band 5, Shape 2)</c>, the median set. Its 12-level curve reproduces the live engine:
/// <list type="bullet">
///   <item><c>PeakMaskAlpha[L−1]</c> = today's <c>OverlayWindow.VimsLevels[L−1].Alpha</c>
///   (0.00 .. 0.92, mirrored in <see cref="Set0MaskAlphas"/> below — see the SSOT note).</item>
///   <item><c>FovFloorPercent</c> = 15 (the tightest-natural floor;
///   <c>SenseDefaults.TightestNaturalFovRadiusPercent</c>).</item>
///   <item><c>GridVisibility</c> = 0.75 (the engine default;
///   <c>ShaderParams.DefaultGridVisibility</c>).</item>
///   <item><c>Scale</c> = 11.0 (the discretizer gain; <c>levelF = 1.0 + 11.0·t</c> in
///   <c>MockInferenceEngine.UpdateVimsTier</c>).</item>
/// </list>
/// Pinned invariants (a drift test asserts these):
/// <code>
///   SetGrid.Curve(GridCoord.Set0).Levels[0].PeakMaskAlpha   == 0.00   // L1, D6 floor
///   SetGrid.Curve(GridCoord.Set0).Levels[L-1].PeakMaskAlpha == VimsLevels[L-1].Alpha
/// </code></para>
///
/// <para><b>The other 49 sets are PROVISIONAL deformations of set0 (valid until M1).</b>
/// M1 anchors the OFI norm-factors; until then these are placeholder envelopes, NOT
/// calibrated values:
/// <list type="bullet">
///   <item><b>Band</b> applies a provisional affine scale-up of the mask-alpha / FOV
///   envelope: higher Band ⇒ deeper mask + tighter FOV floor. PeakMaskAlpha is clamped to
///   <c>[0.20 floor .. 0.92 peak]</c>, and <b>L1 is always kept at 0.00</b> (the D6 day-1
///   floor — never scaled up).</item>
///   <item><b>Shape</b> is the RENDER-CHARACTER axis (Kirby ruling 2026-06-11). Per resolved
///   Q4 it is shader / overlay-render-only and does NOT touch the LeakyIntegrator, Scale,
///   PeakMaskAlpha or FovFloorPercent (those stay Band-only). It drives TWO render outputs per
///   set: <c>GridVisibility</c> (the grid baseline strength) AND <c>FeatherWidth</c> (the edge
///   feather character) — see <see cref="SenseEase.Core.Configuration.SenseDefaults.ShapeGridVisibilityTable"/>
///   / <c>ShapeFeatherOffsetTable</c>. Shape 2 (the Set0 column) maps to the byte-identical
///   defaults (gv 0.75, feather 0.50). Both are per-SET baselines: constant across a set's 12
///   levels and re-seeded only on a set move (nothing jumps mid-set).</item>
/// </list></para>
///
/// <para><b>The 10×5 grid is now genuinely 2D:</b> <b>Band</b> = protection DEPTH (mask alpha /
/// FOV floor), <b>Shape</b> = render CHARACTER (grid baseline + feather), and within a set
/// <b>Tier</b> (the 1..12 level) is the within-session ramp. Band and Shape are orthogonal:
/// two sets differing only in Band share gv/feather; two differing only in Shape share the
/// mask-alpha/FOV envelope.</para>
///
/// <para>Pure Core — no engine / WPF dependency, fully deterministic, built once at static init.</para>
/// </summary>
public static class SetGrid
{
    // ── SSOT mirror ──────────────────────────────────────────────────────────
    // SOURCE OF TRUTH: src/SenseEase.App/Views/OverlayWindow.xaml.cs → VimsLevels
    // (the 12 mask alphas), reflected in docs/SenseEase-Intensity-Ladder-12-Levels.md §2.
    //
    // SenseEase.Core is the pure leaf assembly and has ZERO project references, so it
    // CANNOT read OverlayWindow.VimsLevels (which lives in SenseEase.App) at static init.
    // Per the GROUP-A spec's fallback, the 12 alphas are mirrored here verbatim with this
    // pointer to the SSOT + OverlayWindow as the authoritative source. A drift test pins
    // SetGrid.Curve(Set0).Levels[L-1].PeakMaskAlpha == VimsLevels[L-1].Alpha so this mirror
    // cannot silently diverge from the live engine.
    //
    // L1 = 0.00 is the pinned D6 day-1 floor; alpha climbs monotonically to 0.92 at L12.
    internal static readonly double[] Set0MaskAlphas =
    {
        0.00, // L1  #666666  (day-1 floor — pinned 0%)
        0.14, // L2  #666666
        0.25, // L3  #555555
        0.34, // L4  #444444
        0.42, // L5  #444444  (neutral default)
        0.50, // L6  #333333
        0.58, // L7  #222222
        0.66, // L8  #141418  (cool-shift begins)
        0.73, // L9  #08080E
        0.80, // L10 #04040A
        0.86, // L11 #020208
        0.92, // L12 #000004  (peak)
    };

    // ── set0 anchor constants (mirror the live engine defaults) ──────────────
    /// <summary>set0 FOV floor = tightest-natural floor (SenseDefaults.TightestNaturalFovRadiusPercent).</summary>
    internal const int Set0FovFloorPercent = 15;

    /// <summary>set0 grid visibility = engine default (ShaderParams.DefaultGridVisibility).
    /// This is the Shape-2 (Set0 column) anchor only; the live per-set gv now comes from
    /// <see cref="SenseEase.Core.Configuration.SenseDefaults.ShapeGridVisibilityTable"/>
    /// (whose index 2 equals this 0.75). Kept as the documented Set0 invariant.</summary>
    internal const double Set0GridVisibility = 0.75;

    /// <summary>Discretizer gain (levelF = 1.0 + 11.0·t), MockInferenceEngine.UpdateVimsTier.
    /// The 12-rung level span is locked; higher bands scale the envelope, not this gain.</summary>
    internal const double DiscretizerScale = 11.0;

    /// <summary>Hard mask floor once engaged (Guardrails / SSOT §4). PeakMaskAlpha for
    /// deformed sets clamps to this minimum — except L1, which stays at the 0.00 D6 floor.</summary>
    internal const double MaskFloor = 0.20;

    /// <summary>Hard mask peak (the L12 ceiling, SSOT §2). Deformed sets clamp to this.</summary>
    internal const double MaskPeak = 0.92;

    // ── Provisional Band deformation (until M1) ───────────────────────────────
    // Affine scale of the mask-alpha envelope around set0's Band. Band == Set0.Band ⇒
    // factor 1.0 (no change → set0 reproduces today). Each band step away changes the
    // multiplier by ±BandAlphaStep. PROVISIONAL placeholder gain, NOT calibrated.
    private const double BandAlphaStep = 0.06;

    // Provisional FOV-floor tightening per band above set0 (deeper band ⇒ tighter clear
    // zone). Clamped to never go below the absolute 15% natural floor. PROVISIONAL.
    private const int BandFovTightenPerStep = 1;

    // The 50 curves, built once, indexed [Band, Shape].
    private static readonly SetCurve[,] _curves = BuildAll();

    /// <summary>The median set (Band 5, Shape 2) — its curve reproduces today's engine.</summary>
    public static GridCoord Set0 => GridCoord.Set0;

    /// <summary>The deterministic 12-level curve for a coordinate. <paramref name="coord"/>
    /// is clamped into the 10×5 grid first (see <see cref="Clamp"/>).</summary>
    public static SetCurve Curve(GridCoord coord)
    {
        var c = Clamp(coord);
        return _curves[c.Band, c.Shape];
    }

    /// <summary>The in-bounds von-Neumann (4-neighbour, no-diagonal) neighbourhood of
    /// <paramref name="coord"/>. Delegates to <see cref="GridCoord.AdjacentCells"/>.</summary>
    public static IEnumerable<GridCoord> AdjacentCells(GridCoord coord) =>
        GridCoord.AdjacentCells(Clamp(coord));

    /// <summary>Clamp a coordinate into the valid 10×5 grid (Band 0..9, Shape 0..4).</summary>
    public static GridCoord Clamp(GridCoord coord) => new(
        Math.Clamp(coord.Band, GridCoord.MinBand, GridCoord.MaxBand),
        Math.Clamp(coord.Shape, GridCoord.MinShape, GridCoord.MaxShape));

    // ── Construction ──────────────────────────────────────────────────────────

    private static SetCurve[,] BuildAll()
    {
        int bands = GridCoord.MaxBand - GridCoord.MinBand + 1;   // 10
        int shapes = GridCoord.MaxShape - GridCoord.MinShape + 1; // 5
        var grid = new SetCurve[bands, shapes];

        for (int band = GridCoord.MinBand; band <= GridCoord.MaxBand; band++)
        {
            for (int shape = GridCoord.MinShape; shape <= GridCoord.MaxShape; shape++)
            {
                var coord = new GridCoord(band, shape);
                grid[band, shape] = new SetCurve(coord, BuildLevels(coord));
            }
        }

        return grid;
    }

    private static LevelParams[] BuildLevels(GridCoord coord)
    {
        // Provisional Band envelope multiplier. Band == Set0.Band ⇒ 1.0 (identity), so
        // set0 — and every Shape at set0's Band — reproduces the raw VimsLevels alphas.
        int bandDelta = coord.Band - GridCoord.Set0.Band;
        double alphaMultiplier = 1.0 + (BandAlphaStep * bandDelta);

        // Higher band ⇒ tighter FOV floor (but never below the absolute 15% natural floor).
        int fovFloor = Math.Max(
            Set0FovFloorPercent,
            Set0FovFloorPercent + (BandFovTightenPerStep * Math.Max(0, bandDelta)));
        // NOTE: bandDelta < 0 (weaker bands) keeps the 15% floor — we never RELAX below
        // the natural floor; weaker bands express via reduced mask alpha, not a wider floor.

        // ── Shape axis = render character (Kirby ruling 2026-06-11) — the FIRST read of
        // coord.Shape on the curve. Shape drives TWO render-only (Q4) per-SET baselines that
        // are CONSTANT across all 12 levels of this set (Band still drives alpha / fovFloor
        // exactly as above; Scale is untouched). Both are re-seeded only on a set move, so
        // nothing jumps mid-set. The GridCeiling render saturates at gv>=0.75, so the
        // strongest shapes differentiate via the softer FEATHER edge instead of grid
        // brightness (see SenseDefaults.Shape* tables). Shape 2 (the Set0 column) maps to
        // gv 0.75 + feather offset 0.0 ⇒ the byte-identical Set0 anchor (0.75 / 0.50).
        double gridVisibility = SenseDefaults.ShapeGridVisibilityTable[coord.Shape];
        // Feather = profile default + the per-Shape offset, CLAMPED into the ShaderParams
        // [0,1] valid range (mirrors ShaderParams.Clamp); Shape 2's 0.0 offset resolves to
        // exactly the default (0.50) so Set0 stays parity-exact.
        double featherWidth = Math.Clamp(
            ShaderParams.DefaultFeatherWidth + SenseDefaults.ShapeFeatherOffsetTable[coord.Shape],
            0.0, 1.0);

        var levels = new LevelParams[SetCurve.LevelCount];
        for (int i = 0; i < SetCurve.LevelCount; i++)
        {
            double alpha;
            if (i == 0)
            {
                // L1 is ALWAYS the 0.00 D6 day-1 floor — never scaled up. (Invariant.)
                alpha = 0.00;
            }
            else if (bandDelta == 0)
            {
                // set0's Band (5) is an EXACT pass-through of the raw VimsLevels alphas —
                // this is what pins set0 ≡ today. The [0.20..0.92] mask-floor clamp is NOT
                // applied here: it is scoped to the deformed sets only (per spec). Note that
                // the raw L2 alpha (0.14) sits BELOW MaskFloor by design; it is the authoritative
                // set0 curve and must reproduce VimsLevels verbatim, so it must NOT be clamped.
                alpha = Set0MaskAlphas[i];
            }
            else
            {
                // Provisional affine scale-up/down of the mask-alpha envelope for the 49
                // deformed sets, clamped to the [0.20 floor .. 0.92 peak] band per spec.
                double scaled = Set0MaskAlphas[i] * alphaMultiplier;
                alpha = Math.Clamp(scaled, MaskFloor, MaskPeak);
            }

            levels[i] = new LevelParams(
                PeakMaskAlpha: alpha,
                FovFloorPercent: fovFloor,
                Scale: DiscretizerScale,        // 12-rung span locked across all sets
                GridVisibility: gridVisibility, // Shape-driven render character (Band-independent)
                FeatherWidth: featherWidth);    // Shape-driven feather character (Q4 render-only)
        }

        return levels;
    }
}
