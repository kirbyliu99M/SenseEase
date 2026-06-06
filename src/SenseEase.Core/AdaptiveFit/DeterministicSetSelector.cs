namespace SenseEase.Core.AdaptiveFit;

/// <summary>
/// The deterministic, pure Adaptive Fit set selector (Alpha-2 P3/P4 GROUP C).
///
/// <para>Given the current set, a window of the single user's own intensity stats, and the
/// tuning <see cref="SelectorWeights"/>, it scores the current cell plus its von-Neumann
/// neighbours and returns either <see cref="SelectorDecision.Hold"/> or
/// <see cref="SelectorDecision.MoveTo"/> an ADJACENT cell. Adjacency-only by construction
/// (candidates = <c>SetGrid.AdjacentCells(current) ∪ {current}</c>), so no single evaluation
/// can jump more than one Manhattan step.</para>
///
/// <para><b>Anti-overfit confidence gate.</b> A move fires ONLY when all three hold:
/// <list type="number">
///   <item>the window has at least <c>SelectorMinSamplesBeforeAnyMove</c> samples (thin data Holds);</item>
///   <item>the argmax candidate is not the current cell; and</item>
///   <item><c>argmax.score − holdScore ≥ ConfidenceMargin</c> (a sub-margin win Holds).</item>
/// </list>
/// Otherwise the result is Hold. The current cell is always a candidate, so a flat/neutral
/// stats window (every neighbour scores no better than staying) deterministically Holds.</para>
///
/// <para><b>Manual authority.</b> A candidate whose move direction opposes the sign of a
/// recent manual level adjustment is penalised by <c>W5ManualConflict</c> — large enough that
/// the user's deliberate nudge defers any auto-move pushing the other way.</para>
///
/// <para>Pure, deterministic, allocation-light, NO I/O and NO engine/WPF dependency. Single
/// user's own stats only — this is NOT collaborative filtering. Reachable only behind the
/// default-OFF <c>AppConfig.AdaptiveFitSelectorEnabled</c> flag.</para>
/// </summary>
public static class DeterministicSetSelector
{
    // ── Documented operating points (provisional, until M1 anchors OFI norm-factors) ──
    //
    // Each raw feature is normalised to [0,1] against a documented operating point so the
    // fit kernel compares like-with-like against a cell's [0,1] target. These are the same
    // class of provisional placeholders as SetGrid's Band deformation: deliberately simple
    // and conservative, swapped for calibrated values at M1.

    /// <summary>Pressure operating point: PressureP90 is the median-relative ratio
    /// (Pressure / EffectiveTrigger). 1.0 == "at trigger". We treat the [0 .. 2×trigger]
    /// span as the working range, so the normaliser divides by 2.0 and clamps to [0,1].
    /// (P90 ≈ trigger ⇒ norm 0.5, the median Band's target.)</summary>
    internal const double PressureNormDivisor = 2.0;

    /// <summary>Spike operating point: spikes/hour. ~6/hour (one every 10 min) is treated as
    /// the top of the working range ⇒ norm 1.0. Higher Shape expects higher spike
    /// responsiveness.</summary>
    internal const double SpikeNormPerHourFullScale = 6.0;

    // CalmRatio and FmsAggregate:
    //   CalmRatio is already a fraction in [0,1] — used directly.
    //   FmsAggregate (0..20) is not part of the cell-fit score in this provisional pass
    //   (the spec's score uses pressure, spike, calm only); it is reserved for a later
    //   confidence/weighting refinement and intentionally not consumed here.

    /// <summary>
    /// Evaluate one selection. Returns <see cref="SelectorDecision.MoveTo"/> an adjacent cell
    /// only when the confidence gate passes; otherwise <see cref="SelectorDecision.Hold"/>.
    /// </summary>
    /// <param name="current">The set the user is currently on.</param>
    /// <param name="stats">The single user's own windowed intensity stats.</param>
    /// <param name="w">The tuning weights / gates (see <see cref="SelectorWeights.Default"/>).</param>
    /// <param name="recentManualLevelDelta">Sign-significant recent manual level change
    /// (&gt;0 = user manually went up, &lt;0 = down, 0 = none). Candidates moving opposite this
    /// sign are penalised so the user's authority defers the auto-move.</param>
    public static SelectorDecision Select(
        GridCoord current,
        UserIntensityStats stats,
        SelectorWeights w,
        int recentManualLevelDelta)
    {
        // The current cell is ALWAYS a candidate — staying must be able to win, which is
        // what makes flat/neutral stats deterministically Hold.
        double holdScore = Score(current, current, stats, w, recentManualLevelDelta);

        // Track the best NON-current adjacent candidate.
        var bestTarget = current;
        double bestScore = double.NegativeInfinity;
        bool haveCandidate = false;

        foreach (var candidate in SetGrid.AdjacentCells(current))
        {
            double s = Score(current, candidate, stats, w, recentManualLevelDelta);

            // Deterministic tie-break: strictly-greater wins; on an exact tie keep the
            // earlier candidate (AdjacentCells yields a fixed order), so the result is stable.
            if (s > bestScore)
            {
                bestScore = s;
                bestTarget = candidate;
                haveCandidate = true;
            }
        }

        // ── Confidence gate (anti-overfit) ──
        // 1) Thin data never moves the set.
        if (stats.SampleCount < Configuration.SenseDefaults.SelectorMinSamplesBeforeAnyMove)
            return SelectorDecision.HoldDecision;

        // 2) No adjacent candidate (degenerate) ⇒ Hold.
        if (!haveCandidate)
            return SelectorDecision.HoldDecision;

        // 3) argmax must be a DIFFERENT cell AND beat staying by at least the margin.
        if (bestTarget != current && bestScore - holdScore >= w.ConfidenceMargin)
            return new SelectorDecision.MoveTo(bestTarget);

        return SelectorDecision.HoldDecision;
    }

    /// <summary>
    /// Score a single candidate (pure). Higher is better.
    /// <code>
    ///   W1·fit(pressure,band) + W2·fit(spike,shape) + W3·fit(calm,band)
    ///   − W4·ManhattanDistance(current,candidate) − W5·manualConflict
    /// </code>
    /// </summary>
    internal static double Score(
        GridCoord current,
        GridCoord candidate,
        UserIntensityStats stats,
        SelectorWeights w,
        int recentManualLevelDelta)
    {
        // Feature norms in [0,1] against the documented operating points.
        double pressureNorm = Clamp01(stats.PressureP90 / PressureNormDivisor);
        double spikeNorm    = Clamp01(stats.SpikeRecurrencePerHour / SpikeNormPerHourFullScale);
        double calmNorm     = Clamp01(stats.CalmRatio); // already a fraction

        // Cell target norms in [0,1]: higher Band expects higher pressure and LOWER calm;
        // Shape ~ spike responsiveness (higher Shape expects more spikes).
        double bandTarget  = BandToNorm(candidate.Band);
        double shapeTarget = ShapeToNorm(candidate.Shape);

        double fitPressure = Fit(pressureNorm, bandTarget, w.FitTolerance);
        double fitSpike    = Fit(spikeNorm, shapeTarget, w.FitTolerance);
        // Calm pulls the OTHER way on Band: more calm ⇒ prefer a LOWER band, so the calm
        // feature is fit against (1 − bandTarget).
        double fitCalm     = Fit(calmNorm, 1.0 - bandTarget, w.FitTolerance);

        double distance = GridCoord.ManhattanDistance(current, candidate);
        double conflict = ManualConflict(current, candidate, recentManualLevelDelta);

        return w.W1Pressure * fitPressure
             + w.W2Spike    * fitSpike
             + w.W3Calm     * fitCalm
             - w.W4Distance * distance
             - w.W5ManualConflict * conflict;
    }

    /// <summary>fit ∈ [0,1] — a linear kernel: 1 at the cell target, falling to 0 once the
    /// normalised feature is <paramref name="tolerance"/> away. A non-positive tolerance
    /// degenerates to an exact-match indicator (1 iff equal, else 0).</summary>
    internal static double Fit(double featureNorm, double cellTargetNorm, double tolerance)
    {
        if (tolerance <= 0.0)
            return featureNorm == cellTargetNorm ? 1.0 : 0.0;

        double d = Math.Abs(featureNorm - cellTargetNorm) / tolerance;
        return 1.0 - Clamp01(d);
    }

    /// <summary>manualConflict ∈ {0,1}: 1 when the candidate's Band move opposes the sign of
    /// a recent manual level change (user authority defers). 0 when there is no recent manual
    /// change, when the candidate doesn't change Band, or when it moves WITH the user.
    /// Band is the intensity axis, so it is the axis a manual level nudge speaks to.</summary>
    internal static int ManualConflict(GridCoord current, GridCoord candidate, int recentManualLevelDelta)
    {
        if (recentManualLevelDelta == 0)
            return 0;

        int bandMove = candidate.Band - current.Band; // >0 up, <0 down, 0 = no intensity move
        if (bandMove == 0)
            return 0;

        // Opposite signs ⇒ conflict. (user went up but candidate goes down, or vice-versa).
        return Math.Sign(bandMove) != Math.Sign(recentManualLevelDelta) ? 1 : 0;
    }

    // ── Cell-axis → [0,1] target maps ─────────────────────────────────────────
    // Linear across the inclusive band/shape ranges. Set0's Band (5) maps near the centre
    // (5/9 ≈ 0.556), so a neutral pressure/calm window fits the median band best ⇒ Hold.

    /// <summary>Band 0..9 → [0,1] (higher Band ⇒ higher expected pressure).</summary>
    internal static double BandToNorm(int band)
    {
        int span = GridCoord.MaxBand - GridCoord.MinBand; // 9
        return span == 0 ? 0.0 : (double)(band - GridCoord.MinBand) / span;
    }

    /// <summary>Shape 0..4 → [0,1] (higher Shape ⇒ higher expected spike responsiveness).</summary>
    internal static double ShapeToNorm(int shape)
    {
        int span = GridCoord.MaxShape - GridCoord.MinShape; // 4
        return span == 0 ? 0.0 : (double)(shape - GridCoord.MinShape) / span;
    }

    private static double Clamp01(double v) => Math.Clamp(v, 0.0, 1.0);
}
