namespace SenseEase.Core.AdaptiveFit;

/// <summary>
/// One set in the 50-set Adaptive Fit grid: a <see cref="GridCoord"/> plus its 12-level
/// render curve. <see cref="Levels"/> is always length 12, indexed by <c>L − 1</c>
/// (so <c>Levels[0]</c> = L1 .. <c>Levels[11]</c> = L12), matching the native 12-level
/// ladder (SSOT: docs/SenseEase-Intensity-Ladder-12-Levels.md §2).
///
/// <para>Pure Core record — built deterministically by <see cref="SetGrid"/>. For
/// <see cref="GridCoord.Set0"/> the curve reproduces today's engine exactly; the other
/// 49 sets are PROVISIONAL deformations until M1 anchors the OFI norm-factors.</para>
/// </summary>
public sealed record SetCurve(GridCoord Coord, LevelParams[] Levels)
{
    /// <summary>The native ladder length — 12 rungs (L1..L12).</summary>
    public const int LevelCount = 12;

    /// <summary>Resolve the render envelope for a 1-based level L (1..12).
    /// Out-of-range L is clamped into [1,12] (defensive; mirrors the engine's tier clamp).</summary>
    public LevelParams ForLevel(int level)
    {
        int idx = Math.Clamp(level, 1, LevelCount) - 1;
        return Levels[idx];
    }
}
