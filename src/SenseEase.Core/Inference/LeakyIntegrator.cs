using SenseEase.Core.Configuration;

namespace SenseEase.Core.Inference;

/// <summary>
/// Leaky Integrator with a Noise Gate (design draft §3).
/// Accumulates visual-stimulus "pressure" over time: sub-threshold optical flow is
/// discarded as sensory noise, supra-threshold flow decays geometrically each step.
/// This is real Phase-1 logic — not a stub.
/// </summary>
public sealed class LeakyIntegrator
{
    private readonly double _decayAlpha;
    private readonly double _noiseGateThreshold;
    private readonly double _quadraticTerm;

    /// <summary>Current accumulated pressure (never negative).</summary>
    public double Pressure { get; private set; }

    public LeakyIntegrator(
        double decayAlpha = SenseDefaults.BaseDecayAlpha,
        double noiseGateThreshold = SenseDefaults.NoiseGateThreshold,
        double quadraticTerm = SenseDefaults.QuadraticTerm)
    {
        _decayAlpha = decayAlpha;
        _noiseGateThreshold = noiseGateThreshold;
        _quadraticTerm = quadraticTerm;
    }

    /// <summary>
    /// Apply one optical-flow sample and return the updated pressure.
    /// </summary>
    /// <param name="opticalFlowIndex">Raw OFI for this step.</param>
    /// <param name="flowWeight">Mode-dependent weight (Passenger weighs passive flow more).</param>
    /// <param name="dtSeconds">
    /// Time elapsed since the last step in seconds (default 1.0 for unit-step / test usage).
    /// Pass the real inter-sample interval to make accumulation rate independent of update
    /// frequency — a 30 Hz provider passes 0.033; a single slider interaction passes 1.0.
    /// </param>
    public double Step(double opticalFlowIndex, double flowWeight, double dtSeconds = 1.0)
    {
        // Noise Gate: ignore sub-threshold flow entirely.
        double gated = opticalFlowIndex < _noiseGateThreshold ? 0.0 : opticalFlowIndex;

        // Stimulus scaled by dt so 30 Hz and 1 Hz providers accumulate at the same real-time rate.
        double stimulus = ((gated * flowWeight) + (_quadraticTerm * gated * gated)) * dtSeconds;

        // Time-correct decay based on 100 ms reference alpha from the spec:
        // alpha(dt) = alpha_100ms^(dt / 0.1s), keeping tau stable across variable sample periods.
        double alphaDt = Math.Pow(_decayAlpha, dtSeconds / 0.1);
        Pressure = (alphaDt * Pressure) + stimulus;
        if (Pressure < 0) Pressure = 0;
        return Pressure;
    }

    /// <summary>Reset accumulated pressure to zero.</summary>
    public void Reset() => Pressure = 0;
}
