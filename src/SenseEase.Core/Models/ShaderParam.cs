namespace SenseEase.Core.Models;

/// <summary>
/// G1 (Alpha-2): the single shader knob an absolute-set command targets. Distinct
/// from <see cref="ShaderParams"/> (the live value container) — this enum is the
/// *selector* the chatbot slash-command / strict-language path passes to
/// <c>IInferenceEngine.SetShaderParam</c>.
///
/// <para><see cref="MaskDepth"/>, <see cref="GridVisibility"/> and
/// <see cref="FeatherWidth"/> map 1:1 onto the normalized [0,1] fields of
/// <see cref="ShaderParams"/>. <see cref="FovRadius"/> is NOT a
/// <see cref="ShaderParams"/> field — it is the composed FOV-radius percent the
/// engine resolves via its FOV-offset path (best-effort absolute set).</para>
/// </summary>
public enum ShaderParam
{
    /// <summary>Composed FOV clear-zone radius (percent). Engine-resolved via the
    /// user FOV offset; clamped to [MinComposedFovRadius, 100].</summary>
    FovRadius,

    /// <summary>Core VIMS mask depth [0,1]. Safety-floored at 0.20 — never disable.</summary>
    MaskDepth,

    /// <summary>Chang-Hain grid visibility [0,1]. Opt-in visual aid; 0 = fully hidden.</summary>
    GridVisibility,

    /// <summary>Mask edge feather width [0,1].</summary>
    FeatherWidth,
}
