using System;

namespace SenseEase.Core.Adaptation;

/// <summary>
/// B5 Stage 5 — the orchestration spine, kept WPF-free + dependency-injected so its logic is unit-tested.
/// One <see cref="RunOnce"/> call: gate the whole thing on consent, run the deterministic
/// <see cref="AdaptationGate"/> over the assembled <see cref="MeasuredState"/>, and — only on an
/// <see cref="GateDecision.Apply"/> — invoke the caller's apply + notice callbacks. The App supplies the
/// callbacks (apply → engine.ApplyAutoAdaptDelta; notice → the subtle in-chat system message + Undo).
///
/// <para>Consent default is OFF, and this is the ONLY entry point that moves the auto-channel from the
/// learning loop, so with consent off the gate never runs and protection is exactly as today.</para>
/// </summary>
public sealed class AdaptationCoordinator
{
    private readonly AdaptationGateConfig _config;

    public AdaptationCoordinator(AdaptationGateConfig? config = null)
        => _config = config ?? AdaptationGateConfig.Default;

    /// <summary>Evaluate once. Returns the gate decision. Invokes <paramref name="applyAutoDelta"/> and
    /// <paramref name="onApplied"/> ONLY when consent is on AND the gate Applies. Never throws on the
    /// happy path; a callback that throws is the caller's concern (kept outside any lock here).</summary>
    public GateDecision RunOnce(
        bool consentEnabled,
        AdaptationHint hint,
        MeasuredState state,
        Action<double> applyAutoDelta,
        Action<GateDecision.Apply, string> onApplied)
    {
        if (!consentEnabled) return new GateDecision.Hold("consent-off");

        GateDecision decision = AdaptationGate.Evaluate(hint, state, _config);
        if (decision is GateDecision.Apply apply)
        {
            applyAutoDelta?.Invoke(apply.NewAutoDeltaPp);
            onApplied?.Invoke(apply, hint?.RationaleRenderOnly ?? string.Empty);
        }
        return decision;
    }
}
