namespace SenseEase.Core.AdaptiveFit;

/// <summary>
/// The lifecycle state of the Adaptive Fit selector (Alpha-2 P3/P4 GROUP C). A DEDICATED
/// type — deliberately NOT reusing <c>HabituationEngine.EngineState</c> — so the selector's
/// soft-定型 cooldown governance evolves independently of the habituation engine.
///
/// <para>Lifecycle (see <see cref="AdaptiveFitStateMachine"/>):
/// <c>Default(set0,L5) → Settling → Active ⇄ Cooldown</c>.</para>
/// <list type="bullet">
///   <item><see cref="Default"/> — fresh start, pinned to set0 / L5; no stats yet.</item>
///   <item><see cref="Settling"/> — accumulating samples; not yet eligible to move.</item>
///   <item><see cref="Active"/> — eligible to evaluate a set move (the only state where
///   <c>CanEvaluate</c> is true).</item>
///   <item><see cref="Cooldown"/> — a soft-定型 hold after a set change; no evaluation until
///   the timer elapses back to <see cref="Active"/>.</item>
/// </list>
/// </summary>
public enum CooldownState
{
    /// <summary>Fresh start: pinned to set0 / L5, no stats yet.</summary>
    Default,

    /// <summary>Accumulating samples — not yet eligible to move.</summary>
    Settling,

    /// <summary>Eligible to evaluate a set move (CanEvaluate == true here only).</summary>
    Active,

    /// <summary>Soft-定型 hold after a set change; elapses back to Active.</summary>
    Cooldown,
}
