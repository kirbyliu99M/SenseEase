using System;
using System.Globalization;
using System.Windows.Data;
using SenseEase.App.Localization;

namespace SenseEase.App.Converters;

/// <summary>
/// UX-06 (D1 semantics): turns the raw auto-detected mode token
/// (<c>"Driver"</c> / <c>"Passenger"</c>, set by <c>UserActivityModeDetector</c>) into a
/// CALM, PLAIN-LANGUAGE protection label in the active UI language, so the hero pill never
/// shows the bare English engineering token in either language.
///
/// <para>Mapping (locked polarity — never inverted, never implies Driver gets MORE masking):
/// <list type="bullet">
///   <item><c>Driver</c>  → 操作中（輕度防護） / "Active use · lighter protection"</item>
///   <item><c>Passenger</c> → 觀看中（完整防護） / "Watching · full protection"</item>
/// </list>
/// Driver = the user is actively driving the screen (typing/cursor), where their own
/// proprioception reduces vection, so protection is LIGHTER; Passenger = the user is
/// watching passively, where peripheral motion bites hardest, so protection is FULL.
/// The wording may be reworded but this Driver=lighter / Passenger=full polarity is the
/// D1 invariant.</para>
///
/// <para>MultiBinding form so the label re-picks the instant the language toggles, exactly
/// like <see cref="TierToFeelLabelConverter"/>. Order: [0] Mode (string), [1]
/// <see cref="LocalizationManager.Current"/>.Language (<see cref="UILanguage"/>).
/// One-way (the source is read-only).</para>
/// </summary>
public sealed class ModeToProtectionLabelConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        string mode = values.Length > 0 && values[0] is string s ? s : string.Empty;
        bool english = values.Length > 1 && values[1] is UILanguage l && l == UILanguage.English;
        return Localize(mode, english);
    }

    /// <summary>Pure mapping, exposed so the unit test can assert both languages without a
    /// live MultiBinding. Unknown / unset mode strings fall back to the calm passive label
    /// (full protection) — the conservative default for a motion-sensitivity product.</summary>
    public static string Localize(string? mode, bool english)
    {
        bool isDriver = string.Equals(mode, "Driver", StringComparison.OrdinalIgnoreCase);
        if (isDriver)
            return english ? "Active use · lighter protection" : "操作中（輕度防護）";
        return english ? "Watching · full protection" : "觀看中（完整防護）";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => Array.Empty<object>();
}

/// <summary>UX-06: bool <c>MaskActive</c> → localized mask-state label ("遮罩啟用中" /
/// "Mask active" when true, "遮罩待命" / "Mask inactive" when false), live on language.
/// Used by the Dashboard Advanced expander instead of the old EN-only Setter literals (a
/// <c>Setter.Value</c> cannot hold a <c>{loc:T}</c> Binding, so the localized text is built
/// here). Colour is still carried by a sibling DataTrigger + glyph-dot, so colour never
/// conveys the state alone. MultiBinding order: [0] MaskActive (bool), [1]
/// <see cref="LocalizationManager.Current"/>.Language. One-way.</summary>
public sealed class MaskStateLabelConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool active = values.Length > 0 && values[0] is bool b && b;
        bool english = values.Length > 1 && values[1] is UILanguage l && l == UILanguage.English;
        if (active) return english ? "Mask active"   : "遮罩啟用中";
        return            english ? "Mask inactive" : "遮罩待命";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => Array.Empty<object>();
}

/// <summary>Honest FOV-card hint (Kirby-approved, 2026-06-11): the Advanced FOV card's
/// 清晰區 / "Clear zone" caption gains a parenthetical when the user's persisted chatbot FOV
/// personalization is the reason the resting clear-zone reads below the natural baseline.
/// Without this the card silently shows e.g. 72% with no explanation. The personalization
/// offset is &lt;= 0 (a tighten-only knob); the caption shows its MAGNITUDE with a minus sign,
/// exactly as approved: 清晰區（含個人化 −28） / "Clear zone (personalized −28)".
///
/// <para>Polarity / rendering rules:
/// <list type="bullet">
///   <item>offset == 0 (no personalization) ⇒ the plain caption, byte-identical to today
///         (清晰區 / Clear zone) — no parenthetical, no number.</item>
///   <item>offset &lt; 0 ⇒ the hint with the offset magnitude and an approved minus sign
///         (−|offset|). A positive offset would never occur (FOV personalization only
///         tightens), but is defensively treated as "no hint".</item>
/// </list>
/// MultiBinding form so the caption re-flips the instant the language toggles, exactly like
/// the sibling label converters. Order: [0] UserFovOffsetPp (int), [1]
/// <see cref="LocalizationManager.Current"/>.Language. One-way.</para>
/// </summary>
public sealed class FovClearZoneCaptionConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        int offset = values.Length > 0 && values[0] is int i ? i : 0;
        bool english = values.Length > 1 && values[1] is UILanguage l && l == UILanguage.English;
        return Localize(offset, english);
    }

    /// <summary>Pure mapping, exposed so the unit test can assert both languages without a
    /// live MultiBinding. <paramref name="offset"/> is the engine's accumulated chatbot FOV
    /// personalization in pp (&lt;= 0). Zero / positive ⇒ the plain caption; negative ⇒ the
    /// caption plus the approved "（含個人化 −N）" / "(personalized −N)" parenthetical, where N
    /// is the offset MAGNITUDE and the minus sign is the U+2212 glyph as approved.</summary>
    public static string Localize(int offset, bool english)
    {
        // FOV personalization only tightens (offset <= 0). >= 0 ⇒ nothing to explain.
        if (offset >= 0)
            return english ? "Clear zone" : "清晰區";

        int magnitude = -offset; // offset is negative here ⇒ magnitude is the positive pp count
        return english
            ? $"Clear zone (personalized −{magnitude})"
            : $"清晰區（含個人化 −{magnitude}）";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => Array.Empty<object>();
}

/// <summary>UX-06: bool <c>OverlayVisible</c> → localized visibility word ("顯示中" /
/// "Visible" / "隱藏" / "Hidden"), live on language, for the Advanced overlay-status strip.
/// Same Setter-can't-bind reason as <see cref="MaskStateLabelConverter"/>. MultiBinding
/// order: [0] OverlayVisible (bool), [1] <see cref="LocalizationManager.Current"/>.Language.
/// One-way.</summary>
public sealed class OverlayVisibilityLabelConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = values.Length > 0 && values[0] is bool b && b;
        bool english = values.Length > 1 && values[1] is UILanguage l && l == UILanguage.English;
        if (visible) return english ? "Visible" : "顯示中";
        return             english ? "Hidden"  : "隱藏";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => Array.Empty<object>();
}
