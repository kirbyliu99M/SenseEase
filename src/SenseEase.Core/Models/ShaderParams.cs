namespace SenseEase.Core.Models;

/// <summary>
/// Normalized 0–1 overlay render knobs. The chatbot/NLU adjusts these from user
/// feedback; OverlayWindow maps them to concrete brush values. Each knob is clamped
/// to [0,1]. The per-knob defaults are tuned to reproduce the calibrated baseline
/// overlay: MaskDepth 0.74, GridVisibility 0.75, FeatherWidth 0.50.
/// </summary>
public sealed class ShaderParams
{
    // CW-2: MaskDepth + FeatherWidth are currently RECORDED-but-NOT-RENDERED — the
    // chatbot/NLU persists user preferences here, but no live overlay path consumes
    // them yet; they are wired into the renderer in Beta A2 (see
    // docs/BETA_FEATHERING_COHERENCE_PLAN.md). GridVisibility is the rendered knob today.
    public const double DefaultMaskDepth      = 0.74;   // recorded-but-not-rendered until Beta A2 (renderer will map md→mask opacity)
    public const double DefaultGridVisibility = 0.75;   // RENDERED knob — reproduces tuned-good grid floor=0.30, ceiling=1.00 with formula (0.40*gv / 0.40+0.80*gv)
    public const double DefaultFeatherWidth   = 0.50;   // recorded-but-not-rendered until Beta A2 (renderer will map fw→edge feather)

    public double MaskDepth      { get; set; } = DefaultMaskDepth;
    public double GridVisibility { get; set; } = DefaultGridVisibility;
    /// <summary>Edge feather amount, clamped to [0,1] by <see cref="Clamp"/>; default 0.50 (<see cref="DefaultFeatherWidth"/>).</summary>
    public double FeatherWidth   { get; set; } = DefaultFeatherWidth;

    /// <summary>A fresh instance at neutral baseline values.</summary>
    public static ShaderParams Default => new();

    public ShaderParams Clone() => new()
    {
        MaskDepth = MaskDepth, GridVisibility = GridVisibility, FeatherWidth = FeatherWidth,
    };

    /// <summary>Clamp every knob into [0,1].</summary>
    public void Clamp()
    {
        MaskDepth      = Math.Clamp(MaskDepth, 0.0, 1.0);
        GridVisibility = Math.Clamp(GridVisibility, 0.0, 1.0);
        FeatherWidth   = Math.Clamp(FeatherWidth, 0.0, 1.0);
    }

    /// <summary>Reset all knobs to their Default* constants in place.</summary>
    public void ResetToDefault()
    {
        MaskDepth      = DefaultMaskDepth;
        GridVisibility = DefaultGridVisibility;
        FeatherWidth   = DefaultFeatherWidth;
    }
}
