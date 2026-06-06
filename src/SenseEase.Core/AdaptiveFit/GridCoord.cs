namespace SenseEase.Core.AdaptiveFit;

/// <summary>
/// A coordinate in the 10×5 Adaptive Fit set grid (Alpha-2 P3/P4, Fork default Q1).
///
/// <para><b>Band</b> = intensity band, 0..9 (10 bands). Higher Band = a stronger
/// mask-alpha / FOV envelope (a provisional affine scale-up of set0; see
/// <see cref="SetGrid"/>). <b>Shape</b> = response-shape tag, 0..4 (5 shapes). Per
/// resolved decision Q4 the response-shape is <i>shader / overlay-render-only</i> — it
/// does NOT modulate the global LeakyIntegrator decay; it is a visual/mask-shape tag.</para>
///
/// <para>50 sets total (10 × 5). <see cref="Set0"/> = <c>(Band 5, Shape 2)</c>, the
/// median set (default L5) whose 12-level curve reproduces today's engine exactly.</para>
///
/// <para>Pure Core type — no engine / WPF dependency. Reachable on the hot path ONLY
/// when <c>AppConfig.AdaptiveFitSelectorEnabled == true</c> (default OFF).</para>
/// </summary>
public readonly record struct GridCoord(int Band, int Shape)
{
    /// <summary>Inclusive band range: 0..9 (10 intensity bands).</summary>
    public const int MinBand = 0;
    public const int MaxBand = 9;

    /// <summary>Inclusive shape range: 0..4 (5 response shapes).</summary>
    public const int MinShape = 0;
    public const int MaxShape = 4;

    /// <summary>The 50-set median (Fork default Q1): Band 5, Shape 2, default L5.
    /// Its 12-level curve reproduces today's engine byte-for-byte (see <see cref="SetGrid"/>).</summary>
    public static GridCoord Set0 => new(5, 2);

    /// <summary>True iff this coordinate lies inside the 10×5 grid.</summary>
    public bool InBounds =>
        Band >= MinBand && Band <= MaxBand && Shape >= MinShape && Shape <= MaxShape;

    /// <summary>L1 grid (taxicab) distance between two coordinates: |ΔBand| + |ΔShape|.
    /// Used by the selector's <c>−w4·dist</c> term and to validate adjacency.</summary>
    public static int ManhattanDistance(GridCoord a, GridCoord b) =>
        Math.Abs(a.Band - b.Band) + Math.Abs(a.Shape - b.Shape);

    /// <summary>The in-bounds 4-neighbour von-Neumann neighbourhood of <paramref name="c"/>:
    /// the cells at Manhattan distance exactly 1 (up / down / left / right). NO diagonals.
    /// Out-of-bounds neighbours (edges / corners) are omitted, so the result has 2..4 entries.
    /// The centre cell itself is never included.</summary>
    public static IEnumerable<GridCoord> AdjacentCells(GridCoord c)
    {
        // Von-Neumann (4-connected): the four axis-aligned steps. Diagonals are
        // excluded by construction (each candidate is distance==1 from c).
        var candidates = new[]
        {
            new GridCoord(c.Band - 1, c.Shape), // left  (Band−1)
            new GridCoord(c.Band + 1, c.Shape), // right (Band+1)
            new GridCoord(c.Band, c.Shape - 1), // down  (Shape−1)
            new GridCoord(c.Band, c.Shape + 1), // up    (Shape+1)
        };

        foreach (var n in candidates)
        {
            // Belt-and-suspenders: the in-bounds filter is the operative guard; the
            // distance==1 assertion documents the von-Neumann (no-diagonal) invariant.
            if (n.InBounds && ManhattanDistance(c, n) == 1)
                yield return n;
        }
    }
}
