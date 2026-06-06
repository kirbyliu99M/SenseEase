namespace SenseEase.Core.AdaptiveFit;

/// <summary>
/// The resolved per-level render envelope for one rung of a <see cref="SetCurve"/>
/// (one of the 12 native levels — index L−1). Pure Core data; the engine maps these
/// to concrete brush values only when the Adaptive Fit selector flag is ON.
/// </summary>
/// <param name="PeakMaskAlpha">
/// Peak FOV-dimming mask opacity for this level, in [0,1]. For <see cref="GridCoord.Set0"/>
/// this equals today's <c>OverlayWindow.VimsLevels[L−1].Alpha</c> exactly (0.00 .. 0.92).
/// L1 is pinned to <b>0.00</b> (D6 day-1 floor); once engaged the mask is clamped to the
/// <b>0.20</b> floor (Guardrails / §4 of the 12-level SSOT).
/// </param>
/// <param name="FovFloorPercent">
/// Minimum clear-zone radius percent — the tightest the FOV is allowed to close to for
/// this set. set0 uses <b>15</b> (the tightest-natural floor; matches
/// <c>SenseDefaults.TightestNaturalFovRadiusPercent</c>). The centre is never fully occluded.
/// </param>
/// <param name="Scale">
/// The discretizer gain used to map continuous pressure → real-valued level
/// (<c>levelF = 1.0 + Scale·t</c>). For set0 this is <b>11.0</b> (the native 12-level
/// span, MockInferenceEngine.UpdateVimsTier). Higher-band sets carry a provisional
/// envelope scale-up but the level-span gain itself stays 11.0 (12 rungs).
/// </param>
/// <param name="GridVisibility">
/// Peripheral-grid visibility knob in [0,1]. set0 uses the engine default
/// <b>0.75</b> (<c>ShaderParams.DefaultGridVisibility</c>).
/// </param>
public readonly record struct LevelParams(
    double PeakMaskAlpha,
    int FovFloorPercent,
    double Scale,
    double GridVisibility);
