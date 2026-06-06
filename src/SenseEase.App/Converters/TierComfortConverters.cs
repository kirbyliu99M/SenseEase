using System;
using System.Globalization;
using System.Windows.Data;
using SenseEase.App.Localization;
using SenseEase.App.Views;

namespace SenseEase.App.Converters;

/// <summary>
/// Read-only converters that turn the engine's VIMS tier into the SCREEN-MOTION
/// indicator shown on the Home hero, the comfort pill, the pop-up card and the tray.
///
/// <para>The user-facing wording is the spec-locked <b>5級 動態感</b> set
/// (Alpha_1_MASTERPLAN §13.6): 極高/高/中/低/極低 · Most Natural/Light/Balanced/Steady/
/// Most Steady — "動態感" describes the screen (極高 = most natural motion, 極低 = most
/// stabilized). The single source is <see cref="TierColorPalette.FeelLabel"/> /
/// <see cref="TierColorPalette.FeelLabelEn"/> so the language can't drift between
/// surfaces. The legacy 3-band copy (守護你的畫面 / 平穩 / 上升 / 較高) is retired.</para>
///
/// <para>All copy is SINGLE-LANGUAGE, chosen via <see cref="LocalizationManager"/> (the
/// bilingual "zh / en" display is gone). Surfaces that must re-pick the instant the user
/// toggles language use <see cref="TierToFeelLabelConverter"/> (a MultiBinding on tier +
/// language); the single-value converters re-pick on the next tier change and are kept
/// for read-only a11y names. All bindings are Mode=OneWay (the sources are read-only).</para>
/// </summary>
internal static class ComfortBands
{
    /// <summary>3-band classification kept ONLY for the colour DataTriggers
    /// (<see cref="TierToComfortBandConverter"/>): VeryHigh|High→Steady, Medium→Rising,
    /// Low|VeryLow→High; tier ≤ 0 → Idle. The user-facing LABEL is the 5級 set
    /// (<see cref="FeelLabelLocalized"/>); colours stay on the existing 3-band ramp.</summary>
    public static string For(int tier)
    {
        if (tier <= 0) return "Idle";
        return TierColorPalette.BandForTier(tier) switch
        {
            TierColorPalette.TierBand.VeryHigh => "Steady",
            TierColorPalette.TierBand.High     => "Steady",
            TierColorPalette.TierBand.Medium   => "Rising",
            TierColorPalette.TierBand.Low      => "High",
            TierColorPalette.TierBand.VeryLow  => "High",
            _                                  => "Steady",
        };
    }

    private static int AsTier(object? value) => value is int t ? t : 0;
    public static string BandOf(object? value) => For(AsTier(value));

    /// <summary>The SCREEN-MOTION indicator: the 5級 動態感 label in the active UI
    /// language. Tier ≤ 0 is the pre-first-tick Idle state. Single source for every
    /// motion surface (hero, pill, pop-up, tray).</summary>
    public static string FeelLabelLocalized(int tier, bool english)
    {
        if (tier <= 0) return english ? "Getting ready" : "準備中";
        var band = TierColorPalette.BandForTier(tier);
        return english ? TierColorPalette.FeelLabelEn(band) : TierColorPalette.FeelLabel(band);
    }
}

/// <summary>Tier → the 5級 動態感 motion label in the active language (single-value;
/// re-picks on tier change). Used for read-only a11y names; live language-toggle
/// surfaces use <see cref="TierToFeelLabelConverter"/>.</summary>
public sealed class TierToHeadlineConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => ComfortBands.FeelLabelLocalized(value is int t ? t : 0, LocalizationManager.Current.IsEnglish);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Alias of <see cref="TierToHeadlineConverter"/> retained for binding
/// compatibility — same 5級 motion label.</summary>
public sealed class TierToSublineConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => ComfortBands.FeelLabelLocalized(value is int t ? t : 0, LocalizationManager.Current.IsEnglish);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Tier → the 5級 動態感 label for the comfort pill (single-value).</summary>
public sealed class TierToComfortLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => ComfortBands.FeelLabelLocalized(value is int t ? t : 0, LocalizationManager.Current.IsEnglish);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>(tier, UILanguage) → 5級 動態感 label, live on BOTH tier and language so the
/// hero / pill / pop-up re-pick the instant the user toggles language. MultiBinding
/// order: [0] VimsTier (int), [1] LocalizationManager.Current.Language.</summary>
public sealed class TierToFeelLabelConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        int tier = values.Length > 0 && values[0] is int t ? t : 0;
        bool english = values.Length > 1 && values[1] is UILanguage l && l == UILanguage.English;
        return ComfortBands.FeelLabelLocalized(tier, english);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => Array.Empty<object>();
}

/// <summary>Tier → comfort-band token ("Idle"/"Steady"/"Rising"/"High") for the COLOUR
/// DataTriggers. Language-independent — the colour ramp stays on the 3-band mapping.</summary>
public sealed class TierToComfortBandConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => ComfortBands.BandOf(value);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
