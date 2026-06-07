using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SenseEase.Core.Sensing;

// ─────────────────────────────────────────────────────────────────────────────
// M1 calibration — PURE-CORE reduction half (backlog D1 reducible half).
//
// This file owns the OFFLINE reduction of a raw per-tick MAD trace (captured by
// the in-app capture path when env SENSEEASE_MAD_CALIBRATION=1) into one
// per-scene summary row. It NEVER captures, NEVER touches WGC/D3D11, and NEVER
// mutates SenseDefaults — it only describes what a given norm-factor WOULD
// produce. The recommend half lives in NormFactorRecommender.cs.
//
// CROSS-REF (must stay in lockstep): WgcSensingProvider.OnTick derives OFI as
//   rawOfi  = ComputeMad(current, prev) * SenseDefaults.WgcMadNormFactor;
//   clamped = Math.Clamp(rawOfi, 0, 150);
// MadCalibrationReducer.DerivedOfi mirrors that exact pipeline (mad * normFactor
// then clamp to [0,150]) so the offline reduction predicts the live OFI band a
// candidate norm-factor would yield. If the live clamp range or the MAD→OFI
// formula in WgcSensingProvider changes, update DerivedOfi (and OfiClampMin/Max)
// here too.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One reduced summary row for a single canonical calibration scene
/// (idle desktop / Word scroll / 1080p video / fullscreen game / scene-cut).
/// All fields are derived offline from a raw per-tick MAD trace; nothing here is
/// captured live. <see cref="DerivedOfiMean"/> / <see cref="DerivedOfiP95"/> are
/// what the WGC pipeline WOULD emit at the norm-factor used for the reduction
/// (see <see cref="MadCalibrationReducer.Reduce"/>).
/// </summary>
/// <param name="SceneLabel">Canonical scene id (snake_case, e.g. <c>idle_desktop</c>).</param>
/// <param name="DurationS">Wall-clock length of the captured trace, seconds.</param>
/// <param name="MeanMad">Arithmetic mean of the raw MAD samples (0–255 scale).</param>
/// <param name="P95Mad">95th-percentile MAD (nearest-rank).</param>
/// <param name="DerivedOfiMean">MeanMad × normFactor, clamped to [0,150] (mirrors WGC).</param>
/// <param name="DerivedOfiP95">P95Mad × normFactor, clamped to [0,150] (mirrors WGC).</param>
public sealed record MadSceneStats(
    string SceneLabel,
    double DurationS,
    double MeanMad,
    double P95Mad,
    double DerivedOfiMean,
    double DerivedOfiP95);

/// <summary>
/// Pure, deterministic reducer + CSV (de)serialiser for the M1 raw-MAD trace.
/// No I/O, no engine wiring, no live capture. Single responsibility: turn a list
/// of raw MAD samples for one scene into a <see cref="MadSceneStats"/>, and
/// round-trip those rows through the calibration CSV.
/// </summary>
public static class MadCalibrationReducer
{
    // Mirrors WgcSensingProvider.OnTick's Math.Clamp(rawOfi, 0, 150). See file header.
    private const double OfiClampMin = 0.0;
    private const double OfiClampMax = 150.0;

    /// <summary>CSV header for the reduced WGC calibration table. The on-disk
    /// baseline file <c>baseline/wgc-mad-calibration.csv</c> uses exactly this
    /// header row (and only this — no fabricated data rows until real capture).</summary>
    public const string CsvHeader =
        "scene_label,duration_s,mean_mad,p95_mad,derived_ofi_mean,derived_ofi_p95";

    /// <summary>
    /// Reduce a raw per-tick MAD trace for ONE scene into a summary row.
    /// </summary>
    /// <param name="sceneLabel">Canonical scene id (snake_case).</param>
    /// <param name="durationS">Wall-clock length of the captured trace, seconds.</param>
    /// <param name="rawMads">Raw per-tick MAD samples (0–255 scale); order irrelevant.</param>
    /// <param name="normFactor">The candidate WgcMadNormFactor to evaluate. The
    /// derived-OFI columns are computed AT this factor so the row predicts the live
    /// OFI band it would produce. RECOMMENDS only — never writes SenseDefaults.</param>
    /// <returns>A populated <see cref="MadSceneStats"/>. An empty/null sample list
    /// yields a zero row (mean = p95 = derived = 0) so the protocol surfaces an
    /// empty capture rather than throwing.</returns>
    public static MadSceneStats Reduce(
        string sceneLabel,
        double durationS,
        IReadOnlyList<double> rawMads,
        double normFactor)
    {
        if (rawMads is null || rawMads.Count == 0)
            return new MadSceneStats(sceneLabel, durationS, 0.0, 0.0, 0.0, 0.0);

        double meanMad = rawMads.Average();
        double p95Mad  = NearestRankPercentile(rawMads, 0.95);

        // CROSS-REF WgcSensingProvider: mad * WgcMadNormFactor, then clamp [0,150].
        double derivedOfiMean = Math.Clamp(meanMad * normFactor, OfiClampMin, OfiClampMax);
        double derivedOfiP95  = Math.Clamp(p95Mad  * normFactor, OfiClampMin, OfiClampMax);

        return new MadSceneStats(
            sceneLabel, durationS, meanMad, p95Mad, derivedOfiMean, derivedOfiP95);
    }

    /// <summary>
    /// Nearest-rank percentile (the "C = 0" / NIST nearest-rank method). For
    /// <paramref name="q"/>=0.95 this returns the smallest sample at or above the
    /// 95% rank — never interpolated, so the result is always an actually-observed
    /// MAD value. Distinct from <c>IntensityStatsBuilder.Percentile</c> (which
    /// interpolates): calibration wants a real worst-case-ish sample, not a blend.
    /// </summary>
    private static double NearestRankPercentile(IReadOnlyList<double> samples, double q)
    {
        // Materialise a sorted copy; the input order is not guaranteed.
        double[] sorted = samples.ToArray();
        Array.Sort(sorted);
        int n = sorted.Length;
        if (n == 1) return sorted[0];

        if (q <= 0.0) return sorted[0];
        if (q >= 1.0) return sorted[n - 1];

        // Nearest-rank: rank = ceil(q * n), 1-based → index rank-1, clamped to [0,n-1].
        int rank = (int)Math.Ceiling(q * n);
        if (rank < 1) rank = 1;
        if (rank > n) rank = n;
        return sorted[rank - 1];
    }

    /// <summary>Format one reduced row as a CSV line matching <see cref="CsvHeader"/>.
    /// Invariant-culture, fixed precision so the file is diff-stable across locales.
    /// Does NOT emit a trailing newline (the writer joins rows).</summary>
    public static string ToCsvRow(MadSceneStats stats)
    {
        return string.Join(",",
            stats.SceneLabel,
            F(stats.DurationS),
            F(stats.MeanMad),
            F(stats.P95Mad),
            F(stats.DerivedOfiMean),
            F(stats.DerivedOfiP95));
    }

    /// <summary>Parse reduced rows from CSV lines, skipping the header row, blank
    /// lines, and <c>#</c>-comment lines. Tolerant of surrounding whitespace.
    /// Throws <see cref="FormatException"/> on a malformed data row (wrong column
    /// count or non-numeric numeric column) so a corrupt capture is loud, not
    /// silently dropped.</summary>
    public static IReadOnlyList<MadSceneStats> ParseCsv(IEnumerable<string> lines)
    {
        var result = new List<MadSceneStats>();
        if (lines is null) return result;

        foreach (string rawLine in lines)
        {
            if (rawLine is null) continue;
            string line = rawLine.Trim();

            if (line.Length == 0) continue;                 // blank
            if (line.StartsWith("#", StringComparison.Ordinal)) continue; // comment
            // Skip the header row (compare the canonical header, whitespace-trimmed).
            if (string.Equals(line, CsvHeader, StringComparison.OrdinalIgnoreCase))
                continue;

            string[] cols = line.Split(',');
            if (cols.Length != 6)
                throw new FormatException(
                    $"MAD calibration CSV row has {cols.Length} columns, expected 6: '{rawLine}'");

            result.Add(new MadSceneStats(
                SceneLabel:     cols[0].Trim(),
                DurationS:      P(cols[1], "duration_s",       rawLine),
                MeanMad:        P(cols[2], "mean_mad",         rawLine),
                P95Mad:         P(cols[3], "p95_mad",          rawLine),
                DerivedOfiMean: P(cols[4], "derived_ofi_mean", rawLine),
                DerivedOfiP95:  P(cols[5], "derived_ofi_p95",  rawLine)));
        }

        return result;
    }

    private static string F(double v) =>
        v.ToString("0.####", CultureInfo.InvariantCulture);

    private static double P(string cell, string column, string rawLine)
    {
        if (double.TryParse(cell.Trim(),
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out double v))
            return v;
        throw new FormatException(
            $"MAD calibration CSV column '{column}' is not numeric ('{cell.Trim()}'): '{rawLine}'");
    }
}
