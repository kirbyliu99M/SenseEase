using System;
using System.Globalization;
using System.Windows.Data;
using SenseEase.App.Views;

namespace SenseEase.App.Converters;

/// <summary>
/// D1 (M5) read-only converters that turn the engine's VIMS tier into the calm
/// Home hero's plain-language headline / subline / comfort-band token. They are
/// the ONLY place tier→copy lives, and they deliberately delegate the tier→band
/// classification to <see cref="TierColorPalette.BandForTier"/> so there is exactly
/// one band-threshold source (no ≤5 / 6-7 / 8-10 duplication that could drift).
///
/// <para>Hero copy is APP-ACTION, never felt-state ("Watching your screen", not
/// "You're protected") per the locked HARD_CONSTRAINTS. All bindings using these
/// must be Mode=OneWay (the source properties are read-only — a TwoWay binding
/// crashes window construction and the smoke test catches it).</para>
/// </summary>
internal static class ComfortBands
{
    /// <summary>The three comfort bands map 1:1 onto the tier colour bands:
    /// Grey→Steady, Amber→Rising, Red→High. Tier ≤ 0 is the pre-first-tick Idle
    /// state. Single source: delegates to <see cref="TierColorPalette.BandForTier"/>.</summary>
    public static string For(int tier)
    {
        if (tier <= 0) return "Idle";
        return TierColorPalette.BandForTier(tier) switch
        {
            TierColorPalette.TierBand.Grey  => "Steady",
            TierColorPalette.TierBand.Amber => "Rising",
            TierColorPalette.TierBand.Red   => "High",
            _                               => "Steady",
        };
    }

    private static int AsTier(object? value) => value is int t ? t : 0;
    public static string BandOf(object? value) => For(AsTier(value));
}

/// <summary>Tier → plain-language hero headline (app-action register).</summary>
public sealed class TierToHeadlineConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => ComfortBands.BandOf(value) switch
        {
            "Idle"   => "準備中 / Getting ready",
            "Steady" => "守護你的畫面 / Watching your screen",
            "Rising" => "正在緩和邊緣 / Easing the edges",
            "High"   => "正在強力緩和 / Strain is high — easing now",
            _        => "守護你的畫面 / Watching your screen",
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Tier → supporting subline beneath the headline.</summary>
public sealed class TierToSublineConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => ComfortBands.BandOf(value) switch
        {
            "Idle"   => "正在啟動 / Settling in",
            "Steady" => "畫面平穩 / Screen is calm",
            "Rising" => "畫面開始活躍 / Things are picking up",
            "High"   => "畫面變化很大 / A lot of motion right now",
            _        => "畫面平穩 / Screen is calm",
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Tier → the band's short comfort label (locked lexicon).</summary>
public sealed class TierToComfortLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => ComfortBands.BandOf(value) switch
        {
            "Idle"   => "準備中",
            "Steady" => "平穩",
            "Rising" => "上升",
            "High"   => "較高",
            _        => "平穩",
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Tier → comfort-band token name ("Idle"/"Steady"/"Rising"/"High").
/// The Home hero binds this and uses DataTriggers to pick the matching Comfort*
/// theme brushes — so the band→colour mapping stays in the theme, not in code.</summary>
public sealed class TierToComfortBandConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => ComfortBands.BandOf(value);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
