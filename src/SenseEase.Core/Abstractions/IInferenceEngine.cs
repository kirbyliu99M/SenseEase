using SenseEase.Core.Models;

namespace SenseEase.Core.Abstractions;

/// <summary>
/// Inference layer contract: Leaky Integrator, Noise Gate, Driver/Passenger mode,
/// and mask-attenuation thresholding.
/// </summary>
public interface IInferenceEngine
{
    /// <summary>The most recent computed state.</summary>
    SenseState Current { get; }

    /// <summary>
    /// Process one optical-flow sample under the given mode and return the updated state.
    /// </summary>
    /// <param name="sampledAt">
    /// Timestamp of the sample. When provided, the engine computes the real dt between
    /// successive calls so accumulation is independent of update frequency.
    /// Omit (null) for unit-step / test usage.
    /// </param>
    SenseState Update(double opticalFlowIndex, string mode, DateTime? sampledAt = null);

    /// <summary>
    /// Apply a user-feedback FOV offset (negative = shrink). Persists across Update() calls
    /// so chatbot discomfort reports accumulate until the session is reset.
    /// </summary>
    /// <param name="isDeliberatePreference">
    /// True when the negative delta is a standing user *preference* ("give me more
    /// protection") rather than a discomfort recurrence. When true the FOV offset is
    /// applied but the habituation engine is NOT notified — the earned _offset (Q4.5)
    /// is preserved. Default false keeps the legacy discomfort-recurrence behavior.
    /// Sensor-driven recurrence does NOT use this path (it lives in HabituationEngine.Step).
    /// </param>
    void ApplyUserFeedback(int fovDelta, bool isDeliberatePreference = false);

    /// <summary>
    /// Apply normalized shader-knob deltas (each typically [-0.3,0.3]) from chatbot
    /// feedback. Persists across Update() calls; values are clamped to [0,1].
    /// </summary>
    void ApplyShaderAdjustment(double maskDepthDelta, double gridVisibilityDelta, double featherWidthDelta);

    /// <summary>Reset all personalization (FOV offset + shader knobs) back to baseline.</summary>
    void ResetPersonalization();

    /// <summary>Manually pause or resume the Habituation Engine from the dashboard.</summary>
    void SetHabituationPaused(bool paused);

    /// <summary>The unmodified baseline shader values the engine resets to.</summary>
    ShaderParams BaselineShader { get; }

    /// <summary>Current accumulated user-feedback FOV offset (negative = shrink), clamped [-60,0].</summary>
    int UserFovOffset { get; }
}
