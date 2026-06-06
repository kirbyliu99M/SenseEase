using SenseEase.Core.AdaptiveFit;
using SenseEase.Core.Models;

namespace SenseEase.Core.Abstractions;

/// <summary>
/// Why a <see cref="IInferenceEngine.SetLevel"/> was invoked. Alpha-2 core GROUP A:
/// distinguishes the user's manual +/- nudge from the Adaptive Fit selector's
/// automatic snaps, so later groups (cooldown state machine, Q4/Q5 ordering) can
/// branch on the cause without inspecting call sites.
/// </summary>
public enum LevelChangeCause
{
    /// <summary>The user pressed the +/- fine-tune control (manual nudge).</summary>
    UserPlusMinus,

    /// <summary>The selector moved to an adjacent set; the level snaps onto the
    /// new set's 12-level curve (Q4 felt-snap clears the chatbot delta first, Q5).</summary>
    SetChangeSnap,

    /// <summary>A felt-intensity report snaps the level onto the resolved curve
    /// (Q4 felt-snap clears the chatbot delta first, Q5).</summary>
    FeltIntensitySnap,

    /// <summary>A cross-segment set-move proposal was confirmed by the user.</summary>
    ProposalConfirm,
}

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

    // ── Adaptive Fit selector surface (Alpha-2 core GROUP A) ────────────────────
    // M1-gated; reachable ONLY when AppConfig.AdaptiveFitSelectorEnabled == true.
    // These three members carry DEFAULT interface implementations (C# 8) so the
    // build stays green for any implementer that does not provide its own —
    // MockInferenceEngine overrides all three with real engine-state logic, while
    // an unseen test double / stub inherits the safe defaults below WITHOUT any
    // behaviour change on the flag-off (today's) path. set0 ≡ today.

    /// <summary>The engine's current discrete intensity level (1..12) — the same
    /// value exposed as <c>SenseState.VimsTier</c>. Default no-op implementers
    /// report the neutral default level (5, the median set's resting rung).</summary>
    int CurrentLevel => 5;

    /// <summary>Resolve the per-level render envelope (peak mask alpha, FOV floor,
    /// discretizer scale, grid visibility) for a 1-based level (1..12) on the
    /// engine's active Adaptive Fit set. For <see cref="GridCoord.Set0"/> the
    /// resolved params reproduce today's engine exactly (set0 ≡ today). The default
    /// implementation resolves against the pinned median <see cref="GridCoord.Set0"/>
    /// curve so any implementer is at least byte-correct at the default set.</summary>
    LevelParams ResolveLevel(int level) => SetGrid.Curve(GridCoord.Set0).ForLevel(level);

    /// <summary>Set the engine's discrete level, tagging WHY
    /// (<see cref="LevelChangeCause"/>). Any behavioural effect is gated behind
    /// <c>AppConfig.AdaptiveFitSelectorEnabled</c>; for SetChange/FeltIntensity
    /// snaps the chatbot delta is cleared (Q5) before the snap (Q4), preserving the
    /// earned habituation <c>_offset</c>. The default implementation is a safe no-op
    /// so flag-off / non-selector implementers are unaffected.</summary>
    void SetLevel(int level, LevelChangeCause cause) { /* no-op default; see MockInferenceEngine */ }
}
