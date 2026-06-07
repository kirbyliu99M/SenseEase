using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SenseEase.Core.Sensing;

// ─────────────────────────────────────────────────────────────────────────────
// M1 calibration — PURE-CORE recommend half (backlog D1 reducible half).
//
// Given reduced per-scene stats (from MadCalibrationReducer) plus a set of TARGET
// OFI bands per scene, this recommends a single WgcMadNormFactor that lands every
// matched scene's MEAN MAD inside its target OFI band. It is the analysis output
// Kirby reviews before MANUALLY setting SenseDefaults.WgcMadNormFactor.
//
// RECOMMENDS ONLY — pure, no I/O, never mutates SenseDefaults. The current live
// factor stays put until Kirby reviews a real capture and edits the constant by
// hand. (See WgcSensingProvider cross-ref in MadCalibration.cs.)
//
// Math: for a scene with mean MAD m and target OFI band [lo, hi], the admissible
// norm-factor range that maps m into the band is [lo/m, hi/m] (m > 0). With
// several scenes the recommended factor must satisfy ALL of them, so we intersect
// the per-scene ranges. RecommendedFactor is the midpoint of the intersection.
// An empty intersection (a scene wanting a low factor conflicting with one
// wanting a high factor) is reported via HasConflict + Notes rather than guessed.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Output of <see cref="NormFactorRecommender.Recommend"/>. Advisory only — the
/// caller decides whether to apply <see cref="RecommendedFactor"/> to
/// SenseDefaults. When <see cref="HasConflict"/> is true the scenes cannot all be
/// satisfied by one factor; <see cref="RecommendedFactor"/> is then 0 and
/// <see cref="Notes"/> explains the conflict.
/// </summary>
/// <param name="RecommendedFactor">Midpoint of the admissible-factor intersection
/// across all matched scenes; 0 when there is no admissible factor.</param>
/// <param name="FactorRangeLow">Lower bound of the admissible intersection (0 when none).</param>
/// <param name="FactorRangeHigh">Upper bound of the admissible intersection (0 when none).</param>
/// <param name="HasConflict">True when no single factor satisfies every matched
/// scene (empty intersection), or when no scene matched a target at all.</param>
/// <param name="Notes">Human-readable summary of what matched and, on conflict,
/// which scenes pull in opposite directions.</param>
public sealed record NormFactorRecommendation(
    double RecommendedFactor,
    double FactorRangeLow,
    double FactorRangeHigh,
    bool HasConflict,
    string Notes);

/// <summary>
/// Pure recommender that derives a WgcMadNormFactor candidate from reduced scene
/// stats and per-scene target OFI bands. No I/O; never writes SenseDefaults.
/// </summary>
public static class NormFactorRecommender
{
    /// <summary>
    /// Recommend a single norm-factor that places every matched scene's mean MAD
    /// inside its target OFI band.
    /// </summary>
    /// <param name="scenes">Reduced per-scene stats (from MadCalibrationReducer).
    /// Scenes whose mean MAD is non-positive, or that have no matching target, are
    /// skipped (a static idle-desktop scene with MAD≈0 can't constrain the factor).</param>
    /// <param name="targets">Per-scene target OFI bands keyed by scene label
    /// (case-insensitive). <c>TargetOfiMin</c>/<c>TargetOfiMax</c> are the desired
    /// derived-OFI window for that scene's MEAN.</param>
    /// <returns>An advisory <see cref="NormFactorRecommendation"/>; never throws on
    /// empty/null input (returns a no-match conflict result instead).</returns>
    public static NormFactorRecommendation Recommend(
        IReadOnlyList<MadSceneStats> scenes,
        IReadOnlyList<(string SceneLabel, double TargetOfiMin, double TargetOfiMax)> targets)
    {
        if (scenes is null || scenes.Count == 0 || targets is null || targets.Count == 0)
        {
            return new NormFactorRecommendation(
                RecommendedFactor: 0.0,
                FactorRangeLow:    0.0,
                FactorRangeHigh:   0.0,
                HasConflict:       true,
                Notes:             "No scenes or no targets supplied — nothing to constrain a norm-factor.");
        }

        // Index targets by scene label (case-insensitive); last one wins on dup.
        var targetByLabel = new Dictionary<string, (double Min, double Max)>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
            targetByLabel[t.SceneLabel] = (t.TargetOfiMin, t.TargetOfiMax);

        // Per matched scene the admissible factor range is [Min/meanMad, Max/meanMad].
        // Intersect across scenes. Track which scenes contributed and which pins the
        // running bounds so the conflict Notes can name the offenders.
        double low  = double.NegativeInfinity;
        double high = double.PositiveInfinity;
        var matched   = new List<string>();
        string? lowOwner  = null; // scene currently pinning `low`
        string? highOwner = null; // scene currently pinning `high`

        foreach (MadSceneStats s in scenes)
        {
            if (!targetByLabel.TryGetValue(s.SceneLabel, out var band))
                continue;                       // no target for this scene
            if (s.MeanMad <= 0.0)
                continue;                       // static scene can't constrain the factor

            double sceneLow  = band.Min / s.MeanMad;
            double sceneHigh = band.Max / s.MeanMad;
            // Guard a swapped band (Min > Max): normalise so the intersection math holds.
            if (sceneLow > sceneHigh) (sceneLow, sceneHigh) = (sceneHigh, sceneLow);

            matched.Add(s.SceneLabel);

            if (sceneLow > low)  { low  = sceneLow;  lowOwner  = s.SceneLabel; }
            if (sceneHigh < high) { high = sceneHigh; highOwner = s.SceneLabel; }
        }

        if (matched.Count == 0)
        {
            return new NormFactorRecommendation(
                0.0, 0.0, 0.0, true,
                "No scene matched a target (or every matched scene had mean MAD ≤ 0). " +
                "Capture at least one motion scene (video / game / scene-cut) before recommending.");
        }

        if (low > high)
        {
            // Empty intersection — the scenes pull the factor in opposite directions.
            var sb = new StringBuilder();
            sb.Append("Conflict: no single norm-factor satisfies all matched scenes. ");
            sb.Append(string.Format(CultureInfo.InvariantCulture,
                "Lowest admissible upper bound {0:0.####} (from '{1}') is below the highest admissible lower bound {2:0.####} (from '{3}'). ",
                high, highOwner, low, lowOwner));
            sb.Append("Widen a target OFI band or re-capture the conflicting scene(s). Matched: ");
            sb.Append(string.Join(", ", matched));
            sb.Append('.');
            return new NormFactorRecommendation(
                RecommendedFactor: 0.0,
                FactorRangeLow:    0.0,
                FactorRangeHigh:   0.0,
                HasConflict:       true,
                Notes:             sb.ToString());
        }

        double mid = (low + high) / 2.0;
        string notes = string.Format(CultureInfo.InvariantCulture,
            "Recommended factor {0:0.####} (admissible {1:0.####}–{2:0.####}) from {3} scene(s): {4}. " +
            "Advisory only — review then set SenseDefaults.WgcMadNormFactor by hand.",
            mid, low, high, matched.Count, string.Join(", ", matched));

        return new NormFactorRecommendation(
            RecommendedFactor: mid,
            FactorRangeLow:    low,
            FactorRangeHigh:   high,
            HasConflict:       false,
            Notes:             notes);
    }
}
