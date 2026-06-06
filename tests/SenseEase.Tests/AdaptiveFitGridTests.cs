using System;
using System.Collections.Generic;
using System.Linq;
using SenseEase.App.Configuration;
using SenseEase.App.Views;
using SenseEase.Core.Abstractions;
using SenseEase.Core.AdaptiveFit;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Alpha-2 core GROUP A (foundation) — the 50-set grid + ResolveLevel foundation for
/// the Adaptive Fit selector, shipped BEHIND a default-OFF flag.
///
/// <para>Three pillars, matching the locked spec:</para>
/// <list type="number">
///   <item><b>GRID</b> — <see cref="GridCoord.Set0"/> == (5,2); Manhattan distance;
///   von-Neumann (no-diagonal) adjacency with fewer neighbours at edges/corners;
///   <see cref="SetGrid"/> builds exactly 50 sets; <see cref="SetGrid.Clamp"/> keeps
///   coords in [0..9]×[0..4].</item>
///   <item><b>SET0 ≡ TODAY</b> (drift pin) — for every level 1..12,
///   <c>SetGrid.Curve(Set0).Levels[L-1].PeakMaskAlpha == OverlayWindow.VimsLevels[L-1].Alpha</c>;
///   L1 == 0.00 (D6 floor); no non-zero set0 level drops below the 0.20 mask floor
///   except L2 (the authoritative raw 0.14 alpha, which must reproduce VimsLevels verbatim).</item>
///   <item><b>FLAG-OFF PARITY</b> — with <c>AppConfig.AdaptiveFitSelectorEnabled == false</c>
///   (the default) the new code path is inert: two identically-driven engines produce a
///   byte-identical state stream; and toggling the engine's selector flag at the pinned
///   median Set0 leaves VimsTier + the discretizer output unchanged (set0 ≡ today).</item>
/// </list>
///
/// <para>Every test that touches <see cref="AppConfig.AdaptiveFitSelectorEnabled"/> guards
/// it in a try/finally and restores it to <c>false</c> so there is no cross-test leakage
/// (the field is a process-wide static).</para>
/// </summary>
[Collection("AdaptiveFitSelectorFlag")]
public class AdaptiveFitGridTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // 1. GRID — coordinates, distance, adjacency, build, clamp
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Set0_IsTheMedianBand5Shape2()
    {
        Assert.Equal(new GridCoord(5, 2), GridCoord.Set0);
        Assert.Equal(5, GridCoord.Set0.Band);
        Assert.Equal(2, GridCoord.Set0.Shape);
        Assert.True(GridCoord.Set0.InBounds);
    }

    [Fact]
    public void ManhattanDistance_IsSumOfAbsoluteAxisDeltas()
    {
        Assert.Equal(0, GridCoord.ManhattanDistance(new GridCoord(5, 2), new GridCoord(5, 2)));
        Assert.Equal(1, GridCoord.ManhattanDistance(new GridCoord(5, 2), new GridCoord(6, 2)));
        Assert.Equal(1, GridCoord.ManhattanDistance(new GridCoord(5, 2), new GridCoord(5, 3)));
        // Diagonal step is distance 2 (taxicab), confirming no diagonal is "adjacent".
        Assert.Equal(2, GridCoord.ManhattanDistance(new GridCoord(5, 2), new GridCoord(6, 3)));
        // Opposite corners of the whole grid: |9-0| + |4-0| = 13.
        Assert.Equal(13, GridCoord.ManhattanDistance(new GridCoord(0, 0), new GridCoord(9, 4)));
        // Symmetry.
        Assert.Equal(
            GridCoord.ManhattanDistance(new GridCoord(2, 1), new GridCoord(7, 4)),
            GridCoord.ManhattanDistance(new GridCoord(7, 4), new GridCoord(2, 1)));
    }

    [Fact]
    public void AdjacentCells_AreExactlyTheInBoundsDistanceOneVonNeumannNeighbours()
    {
        var neighbours = GridCoord.AdjacentCells(GridCoord.Set0).ToList();

        // An interior cell has all four von-Neumann neighbours.
        Assert.Equal(4, neighbours.Count);
        Assert.Contains(new GridCoord(4, 2), neighbours); // Band-1
        Assert.Contains(new GridCoord(6, 2), neighbours); // Band+1
        Assert.Contains(new GridCoord(5, 1), neighbours); // Shape-1
        Assert.Contains(new GridCoord(5, 3), neighbours); // Shape+1

        // Every neighbour is in-bounds and at Manhattan distance EXACTLY 1 (no diagonals).
        foreach (var n in neighbours)
        {
            Assert.True(n.InBounds);
            Assert.Equal(1, GridCoord.ManhattanDistance(GridCoord.Set0, n));
        }

        // The centre cell is never its own neighbour.
        Assert.DoesNotContain(GridCoord.Set0, neighbours);

        // No diagonal sneaks in.
        Assert.DoesNotContain(new GridCoord(4, 1), neighbours);
        Assert.DoesNotContain(new GridCoord(6, 3), neighbours);
    }

    [Fact]
    public void AdjacentCells_CornerCell_HasOnlyTwoNeighbours()
    {
        // (0,0) is a corner: only Band+1 and Shape+1 are in-bounds.
        var neighbours = GridCoord.AdjacentCells(new GridCoord(0, 0)).ToList();

        Assert.Equal(2, neighbours.Count);
        Assert.Contains(new GridCoord(1, 0), neighbours);
        Assert.Contains(new GridCoord(0, 1), neighbours);

        // The far corner (9,4) likewise has only two.
        var farCorner = GridCoord.AdjacentCells(new GridCoord(9, 4)).ToList();
        Assert.Equal(2, farCorner.Count);
        Assert.Contains(new GridCoord(8, 4), farCorner);
        Assert.Contains(new GridCoord(9, 3), farCorner);
    }

    [Fact]
    public void AdjacentCells_EdgeCell_HasThreeNeighbours()
    {
        // (0,2) sits on the Band=0 edge (Shape interior): Band-1 is out of bounds,
        // leaving Band+1, Shape-1, Shape+1.
        var bandEdge = GridCoord.AdjacentCells(new GridCoord(0, 2)).ToList();
        Assert.Equal(3, bandEdge.Count);
        Assert.DoesNotContain(new GridCoord(-1, 2), bandEdge);

        // (5,0) sits on the Shape=0 edge (Band interior): Shape-1 is out of bounds,
        // leaving Band-1, Band+1, Shape+1.
        var shapeEdge = GridCoord.AdjacentCells(new GridCoord(5, 0)).ToList();
        Assert.Equal(3, shapeEdge.Count);
        Assert.DoesNotContain(new GridCoord(5, -1), shapeEdge);
    }

    [Fact]
    public void SetGrid_BuildsExactlyFiftyDistinctSets_EachWith12Levels()
    {
        var seen = new HashSet<GridCoord>();

        for (int band = GridCoord.MinBand; band <= GridCoord.MaxBand; band++)
        {
            for (int shape = GridCoord.MinShape; shape <= GridCoord.MaxShape; shape++)
            {
                var coord = new GridCoord(band, shape);
                SetCurve curve = SetGrid.Curve(coord);

                Assert.Equal(coord, curve.Coord);
                Assert.Equal(SetCurve.LevelCount, curve.Levels.Length); // 12 rungs
                Assert.True(seen.Add(coord), $"duplicate set at {coord}");
            }
        }

        // 10 bands × 5 shapes = 50 sets, all distinct.
        Assert.Equal(50, seen.Count);
    }

    [Fact]
    public void Clamp_KeepsCoordsInsideTheTenByFiveGrid()
    {
        // Below-range collapses to the minimum corner.
        Assert.Equal(new GridCoord(0, 0), SetGrid.Clamp(new GridCoord(-3, -7)));
        // Above-range collapses to the maximum corner.
        Assert.Equal(new GridCoord(9, 4), SetGrid.Clamp(new GridCoord(99, 42)));
        // Mixed out-of-range clamps each axis independently.
        Assert.Equal(new GridCoord(9, 0), SetGrid.Clamp(new GridCoord(12, -1)));
        // An in-bounds coord is unchanged.
        Assert.Equal(GridCoord.Set0, SetGrid.Clamp(GridCoord.Set0));

        // Curve(out-of-range) resolves to the clamped cell's curve, never throws.
        Assert.Equal(SetGrid.Curve(new GridCoord(9, 4)).Coord,
                     SetGrid.Curve(new GridCoord(50, 50)).Coord);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 2. SET0 ≡ TODAY — the drift pin
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Set0Curve_PeakMaskAlpha_ReproducesTodaysVimsLevelsAlpha_ForEveryLevel()
    {
        SetCurve set0 = SetGrid.Curve(GridCoord.Set0);

        for (int level = 1; level <= SetCurve.LevelCount; level++)
        {
            double expected = OverlayWindow.VimsLevels[level - 1].Alpha;
            double actual   = set0.Levels[level - 1].PeakMaskAlpha;
            Assert.Equal(expected, actual, 6); // byte-for-byte against the live engine table
        }
    }

    [Fact]
    public void Set0Curve_L1_IsPinnedToZeroAlpha_D6Floor()
    {
        // L1 (index 0) is the D6 day-1 floor: 0% mask, nothing visibly dims at rest.
        Assert.Equal(0.00, SetGrid.Curve(GridCoord.Set0).Levels[0].PeakMaskAlpha, 6);
        // And it agrees with the live table's L1 alpha.
        Assert.Equal(0.00, OverlayWindow.VimsLevels[0].Alpha, 6);
    }

    [Fact]
    public void Set0Curve_NonZeroLevels_RespectMaskFloor_ExceptTheAuthoritativeRawL2()
    {
        SetCurve set0 = SetGrid.Curve(GridCoord.Set0);

        for (int level = 1; level <= SetCurve.LevelCount; level++)
        {
            double alpha = set0.Levels[level - 1].PeakMaskAlpha;

            if (level == 1)
            {
                // L1 is the pinned 0.00 floor (exempt from the 0.20 engaged-mask floor).
                Assert.Equal(0.00, alpha, 6);
            }
            else if (level == 2)
            {
                // L2's raw alpha (0.14) sits BELOW the 0.20 engaged-mask floor BY DESIGN:
                // set0 is the authoritative curve and must reproduce VimsLevels verbatim,
                // so this rung is NOT clamped up. Pin it explicitly so the carve-out is
                // intentional, not an accident.
                Assert.Equal(OverlayWindow.VimsLevels[1].Alpha, alpha, 6);
                Assert.True(alpha < 0.20, "L2 raw alpha is expected to be below the 0.20 floor");
            }
            else
            {
                // Every other non-zero set0 level is at or above the 0.20 engaged floor.
                Assert.True(alpha >= 0.20,
                    $"set0 L{level} alpha {alpha} unexpectedly dropped below the 0.20 mask floor");
            }
        }

        // Alpha climbs monotonically across the whole set0 curve (0.00 → 0.92).
        for (int level = 2; level <= SetCurve.LevelCount; level++)
        {
            Assert.True(
                set0.Levels[level - 1].PeakMaskAlpha > set0.Levels[level - 2].PeakMaskAlpha,
                $"set0 alpha not monotonic between L{level - 1} and L{level}");
        }
    }

    [Fact]
    public void Set0Curve_AnchorParams_MatchTodaysEngineDefaults()
    {
        SetCurve set0 = SetGrid.Curve(GridCoord.Set0);

        foreach (var lp in set0.Levels)
        {
            Assert.Equal(15, lp.FovFloorPercent);      // tightest-natural FOV floor
            Assert.Equal(11.0, lp.Scale, 6);           // discretizer gain: levelF = 1 + 11·t
            Assert.Equal(0.75, lp.GridVisibility, 6);  // engine default grid visibility
        }
    }

    [Fact]
    public void ResolveLevel_AtSet0_ReturnsPeakMaskAlphaEqualToVimsLevels()
    {
        // The engine's ResolveLevel query at the pinned median Set0 must reproduce the
        // live VimsLevels alpha for every level (set0 ≡ today). Pure query — no flag,
        // no Update needed.
        var engine = new MockInferenceEngine();

        for (int level = 1; level <= SetCurve.LevelCount; level++)
        {
            LevelParams lp = engine.ResolveLevel(level);
            Assert.Equal(OverlayWindow.VimsLevels[level - 1].Alpha, lp.PeakMaskAlpha, 6);
        }
    }

    [Fact]
    public void ResolveLevel_ClampsOutOfRangeLevelsIntoTheTwelveRungSpan()
    {
        var engine = new MockInferenceEngine();

        // Below L1 clamps to L1 (0.00); above L12 clamps to L12 (0.92).
        Assert.Equal(OverlayWindow.VimsLevels[0].Alpha, engine.ResolveLevel(0).PeakMaskAlpha, 6);
        Assert.Equal(OverlayWindow.VimsLevels[0].Alpha, engine.ResolveLevel(-5).PeakMaskAlpha, 6);
        Assert.Equal(OverlayWindow.VimsLevels[11].Alpha, engine.ResolveLevel(13).PeakMaskAlpha, 6);
        Assert.Equal(OverlayWindow.VimsLevels[11].Alpha, engine.ResolveLevel(999).PeakMaskAlpha, 6);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 3. FLAG-OFF PARITY — the new path is inert when the selector is off
    // ──────────────────────────────────────────────────────────────────────────

    // A representative pressure sequence: a calm warm-up, a sustained high-OFI surge
    // that crosses the trigger and walks the tier up, then a decay back toward calm.
    // Deterministic timestamps (fixed base + 1 s steps) make dt == 1.0 every tick AND
    // let the mask cooldown elapse, so two identically-driven engines are bit-for-bit
    // reproducible.
    private static readonly double[] PressureSequence = BuildPressureSequence();

    private static double[] BuildPressureSequence()
    {
        var seq = new List<double>();
        for (int i = 0; i < 5; i++)  seq.Add(20);   // calm warm-up (sub-trigger noise)
        for (int i = 0; i < 40; i++) seq.Add(150);  // sustained surge → mask on, tier climbs
        for (int i = 0; i < 30; i++) seq.Add(0);    // decay back toward calm
        return seq.ToArray();
    }

    private static readonly DateTime ClockBase = new(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Drive an engine through the representative sequence with deterministic
    /// timestamps, capturing the full per-tick state stream for comparison.</summary>
    private static List<(int Tier, int Fov, bool Mask, double Pressure)> DriveAndCapture(
        MockInferenceEngine engine)
    {
        var stream = new List<(int, int, bool, double)>();
        for (int i = 0; i < PressureSequence.Length; i++)
        {
            SenseState s = engine.Update(PressureSequence[i], "Passenger", ClockBase.AddSeconds(i));
            stream.Add((s.VimsTier, s.FovRadiusPercent, s.MaskActive, s.Pressure));
        }
        return stream;
    }

    [Fact]
    public void AppConfig_AdaptiveFitSelector_DefaultsOff()
    {
        // The invariant's foundation: the master gate ships dormant.
        Assert.False(AppConfig.AdaptiveFitSelectorEnabled);
    }

    [Fact]
    public void FlagOff_TwoIdenticallyDrivenEngines_ProduceAByteIdenticalStateStream()
    {
        bool original = AppConfig.AdaptiveFitSelectorEnabled;
        try
        {
            AppConfig.AdaptiveFitSelectorEnabled = false; // default; pinned explicit for clarity

            var a = new MockInferenceEngine();
            var b = new MockInferenceEngine();

            var streamA = DriveAndCapture(a);
            var streamB = DriveAndCapture(b);

            // The flag-off path is fully deterministic: identical drive → identical state.
            Assert.Equal(streamA, streamB);

            // And it is not a degenerate all-calm stream — the surge really engaged the
            // mask and walked the tier above the L1 floor at some point.
            Assert.Contains(streamA, t => t.Mask);
            Assert.Contains(streamA, t => t.Tier > 1);
        }
        finally
        {
            AppConfig.AdaptiveFitSelectorEnabled = original;
        }
    }

    [Fact]
    public void TogglingSelectorFlagAtSet0_LeavesVimsTierAndDiscretizerOutputUnchanged()
    {
        bool original = AppConfig.AdaptiveFitSelectorEnabled;
        try
        {
            AppConfig.AdaptiveFitSelectorEnabled = false;

            // Engine OFF (today's path) vs engine with the selector flag ON, both pinned
            // to the median Set0. set0 ≡ today, so the discretizer (flag-independent) and
            // the resolved FOV floor (15 == today's natural floor) make ON a no-op here.
            var off = new MockInferenceEngine();
            var on  = new MockInferenceEngine { AdaptiveFitSelectorEnabled = true };
            Assert.True(on.AdaptiveFitSelectorEnabled);

            var streamOff = DriveAndCapture(off);
            var streamOn  = DriveAndCapture(on);

            // VimsTier (the discretizer output) is identical tick-for-tick — flipping the
            // flag at set0 does not perturb the tier ladder…
            Assert.Equal(
                streamOff.Select(t => t.Tier).ToList(),
                streamOn.Select(t => t.Tier).ToList());

            // …and at set0 the full mask/FOV/pressure stream is identical too (set0 ≡ today,
            // so the gated FOV-floor re-clamp to 15 changes nothing).
            Assert.Equal(streamOff, streamOn);

            // The engines really exercised the new branch decision (flag actually differed).
            Assert.False(off.AdaptiveFitSelectorEnabled);
        }
        finally
        {
            AppConfig.AdaptiveFitSelectorEnabled = original;
        }
    }

    [Fact]
    public void ResolveLevel_IsAPureQuery_DoesNotPerturbEngineStateUnderEitherFlag()
    {
        bool original = AppConfig.AdaptiveFitSelectorEnabled;
        try
        {
            AppConfig.AdaptiveFitSelectorEnabled = false;

            var engine = new MockInferenceEngine();
            var before = DriveAndCapture(engine);

            // A second engine driven identically, but interleaving ResolveLevel queries
            // (a pure read) between ticks, must land on the exact same state stream — the
            // query must never mutate engine state, regardless of the flag.
            var probed = new MockInferenceEngine { AdaptiveFitSelectorEnabled = true };
            var probedStream = new List<(int, int, bool, double)>();
            for (int i = 0; i < PressureSequence.Length; i++)
            {
                _ = probed.ResolveLevel(((i % 12) + 1)); // sweep all 12 levels, ignore result
                SenseState s = probed.Update(PressureSequence[i], "Passenger", ClockBase.AddSeconds(i));
                probedStream.Add((s.VimsTier, s.FovRadiusPercent, s.MaskActive, s.Pressure));

                // ResolveLevel after the tick still equals the set0/today alpha for the
                // engine's current level — proves the query reflects live tier, not stale.
                LevelParams lp = probed.ResolveLevel(s.VimsTier);
                Assert.Equal(OverlayWindow.VimsLevels[Math.Clamp(s.VimsTier, 1, 12) - 1].Alpha,
                             lp.PeakMaskAlpha, 6);
            }

            // set0 ≡ today: the probed (flag-on, query-interleaved) stream matches the
            // untouched flag-off baseline exactly.
            Assert.Equal(before, probedStream);
        }
        finally
        {
            AppConfig.AdaptiveFitSelectorEnabled = original;
        }
    }
}
