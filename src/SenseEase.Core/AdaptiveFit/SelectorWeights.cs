using SenseEase.Core.Configuration;

namespace SenseEase.Core.AdaptiveFit;

/// <summary>
/// The tuning weights + gates for <see cref="DeterministicSetSelector"/> (Alpha-2 P3/P4
/// GROUP C). Pure value type — carries the scoring coefficients, the anti-overfit
/// confidence margin, and the fit-kernel tolerance.
///
/// <para>The score for an adjacent candidate is
/// <code>
///   score = W1Pressure·fit(pressureP90, band)
///         + W2Spike   ·fit(spikeRate,   shape)
///         + W3Calm    ·fit(calmRatio,   band)
///         − W4Distance·ManhattanDistance(current, candidate)
///         − W5ManualConflict·manualConflict(candidate, recentManualLevelDelta)
/// </code>
/// where <c>fit(feature,cell) = 1 − clamp(|featureNorm − cellTargetNorm| / FitTolerance, 0, 1)</c>.
/// A move only fires when <c>argmax.score − holdScore ≥ ConfidenceMargin</c> (see the selector).</para>
///
/// <para>Pure Core — no engine / WPF dependency, no I/O. Read behind the
/// <c>AppConfig.AdaptiveFitSelectorEnabled</c> flag (default ON since c1461a9).
/// <see cref="Default"/> reads the provisional, conservative constants from
/// <see cref="SenseDefaults"/> (the SSOT).</para>
/// </summary>
/// <param name="W1Pressure">Weight on the pressure→Band fit term (primary intensity signal).</param>
/// <param name="W2Spike">Weight on the spike-recurrence→Shape fit term.</param>
/// <param name="W3Calm">Weight on the calm-ratio→Band fit term (relax-when-calm).</param>
/// <param name="W4Distance">Penalty per unit Manhattan step from the current cell (tie-break).</param>
/// <param name="W5ManualConflict">Penalty for a candidate opposing a recent manual adjustment.</param>
/// <param name="W6Fms">Weight on the measured-FMS→Band fit term (B1 outcome signal); applied only
/// when the window has ≥1 measured FMS report.</param>
/// <param name="ConfidenceMargin">Minimum score advantage argmax needs over Hold to move (anti-overfit).</param>
/// <param name="FitTolerance">Half-width (normalised [0,1] units) of the linear fit kernel.</param>
/// <param name="W7GateConflict">B6 Stage 3: penalty for a candidate Band move OPPOSING the B5 gate's standing
/// auto-delta direction (the one-coordinated-loop brake). Defaults to 0 so callers/tests that don't pass it
/// are byte-identical; the engine supplies the live direction.</param>
public readonly record struct SelectorWeights(
    double W1Pressure,
    double W2Spike,
    double W3Calm,
    double W4Distance,
    double W5ManualConflict,
    double W6Fms,
    double ConfidenceMargin,
    double FitTolerance,
    double W7GateConflict = 0.0)
{
    /// <summary>The provisional, conservative default weights — the SSOT is the
    /// <c>// Adaptive Fit selector (F1/F2 governance)</c> region of <see cref="SenseDefaults"/>.</summary>
    public static SelectorWeights Default => new(
        W1Pressure:       SenseDefaults.SelectorW1Pressure,
        W2Spike:          SenseDefaults.SelectorW2Spike,
        W3Calm:           SenseDefaults.SelectorW3Calm,
        W4Distance:       SenseDefaults.SelectorW4Distance,
        W5ManualConflict: SenseDefaults.SelectorW5ManualConflict,
        W6Fms:            SenseDefaults.SelectorW6Fms,
        ConfidenceMargin: SenseDefaults.SelectorConfidenceMargin,
        FitTolerance:     SenseDefaults.SelectorFitTolerance,
        W7GateConflict:   SenseDefaults.SelectorW7GateConflict);
}
