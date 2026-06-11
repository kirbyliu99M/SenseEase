namespace SenseEase.Core.Models;

/// <summary>
/// Normalized 0–1 overlay render knobs. The chatbot/NLU adjusts these from user
/// feedback; OverlayWindow maps them to concrete brush values. Each knob is clamped
/// to [0,1]. The per-knob defaults are tuned to reproduce the calibrated baseline
/// overlay: MaskDepth 0.74, GridVisibility 0.75, FeatherWidth 0.50.
/// </summary>
public sealed class ShaderParams
{
    // CW-2 CLOSED (D-2, Beta A2): MaskDepth + FeatherWidth are now REAL renderer inputs —
    // rendered by the single-pass field (A2). MaskDepth maps to a bounded multiplier on the
    // tier-resolved peak alpha (clamped to the ≥0.20 floor / VimsLevels ceiling); FeatherWidth
    // slides the smoothstep ramp start within safe clamps. At each knob's default the field
    // output is byte-identical to the prior baseline (the VimsLevels table / the 0.35→0.95
    // envelope). GridVisibility was already the rendered grid-brightness knob.
    // See docs/BETA_FEATHERING_COHERENCE_PLAN.md §A2 and OverlayWindow's field math.
    public const double DefaultMaskDepth      = 0.74;   // rendered by the single-pass field (A2): md→bounded peak-alpha multiplier (mult=1.0 at 0.74 ⇒ table-identical)
    public const double DefaultGridVisibility = 0.75;   // RENDERED knob — reproduces tuned-good grid floor=0.30, ceiling=1.00 with formula (0.40*gv / 0.40+0.80*gv)
    public const double DefaultFeatherWidth   = 0.50;   // rendered by the single-pass field (A2): fw→feather-band start slide (start=0.35 at 0.50 ⇒ envelope-identical)

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
