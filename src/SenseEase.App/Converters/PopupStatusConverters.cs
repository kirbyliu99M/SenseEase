using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using SenseEase.App.Localization;
using SenseEase.App.Views;

// (TierToPopupStatusWordMultiConverter is defined at the bottom of this file.)

namespace SenseEase.App.Converters;

/// <summary>
/// UI-RESHAPE prep — read-only converters for the new popup bar's pulse status and
/// 12-segment strength meter. They map the engine's 1–12 VIMS tier (via the single
/// source <see cref="TierColorPalette.BandForTier"/>) onto the reference HTML's
/// 3-state pulse {low / mid / high} and the reference palette colours
/// (green / amber / red, user-approved for these surfaces only).
///
/// <para>Every converter is HEADLESS-SAFE: it returns frozen literal brushes /
/// parsed geometry and never looks up an Application resource, so the smoke tests
/// (bare <c>new Application()</c>) resolve them cleanly. All bindings are OneWay —
/// the tier is engine-owned and read-only.</para>
/// </summary>
internal static class PopupStatus
{
    /// <summary>0 = low/calm (band VeryHigh|High), 1 = mid (Medium), 2 = high/dynamic
    /// (Low|VeryLow). Mirrors the reference's 3-state pulse, derived from the locked
    /// 5-band mapping so the pulse can't drift from the bar/tray tier colour.</summary>
    public static int IndexFor(int tier) => TierColorPalette.BandForTier(tier) switch
    {
        TierColorPalette.TierBand.VeryHigh => 0,
        TierColorPalette.TierBand.High     => 0,
        TierColorPalette.TierBand.Medium   => 1,
        TierColorPalette.TierBand.Low      => 2,
        TierColorPalette.TierBand.VeryLow  => 2,
        _                                  => 1,
    };

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // Reference HTML status colours: --c-low #22C55E / --c-mid #F59E0B / --c-high #EF4444.
    public static readonly SolidColorBrush LowBrush  = Frozen(0x22, 0xC5, 0x5E);
    public static readonly SolidColorBrush MidBrush  = Frozen(0xF5, 0x9E, 0x0B);
    public static readonly SolidColorBrush HighBrush = Frozen(0xEF, 0x44, 0x44);

    public static readonly SolidColorBrush SegOnBrush  = Frozen(0x02, 0x4A, 0xD8); // Popup.SegOn
    private static readonly SolidColorBrush SegOffBrush = MakeArgb(0x38, 0xFF, 0xFF, 0xFF); // Popup.SegOff
    public static SolidColorBrush SegOff => SegOffBrush;

    private static SolidColorBrush MakeArgb(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush BrushForIndex(int index) => index switch
    {
        0 => LowBrush,
        1 => MidBrush,
        _ => HighBrush,
    };

    // Status glyphs, parsed once + frozen (mirrors the keys in Icons.PopupBar.xaml so
    // the meaning stays identical whether resolved here or via StaticResource).
    private static Geometry FrozenGeo(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    public static readonly Geometry GlyphLow  = FrozenGeo("M21,7 L9,19 L3.5,13.5 L4.91,12.09 L9,16.17 L19.59,5.59 Z");
    public static readonly Geometry GlyphMid  = FrozenGeo("M12,7 A5,5 0 1,0 12,17 A5,5 0 1,0 12,7 Z");
    public static readonly Geometry GlyphHigh = FrozenGeo("M12,2 L1,21 L23,21 Z M12,6 L19.53,19 L4.47,19 Z M11,10 L13,10 L13,14 L11,14 Z M11,16 L13,16 L13,18 L11,18 Z");

    public static Geometry GlyphForIndex(int index) => index switch
    {
        0 => GlyphLow,
        1 => GlyphMid,
        _ => GlyphHigh,
    };

    public static int AsTier(object? value) => value is int t ? t : 0;
}

/// <summary>VimsTier (1–12) → pulse status brush (green/amber/red). OneWay.</summary>
public sealed class TierToPopupStatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => PopupStatus.BrushForIndex(PopupStatus.IndexFor(PopupStatus.AsTier(value)));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>VimsTier → short pulse word in the active UI language
/// (低/中/高 · Calm/Moderate/Dynamic). OneWay.</summary>
public sealed class TierToPopupStatusWordConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => PopupStatus.IndexFor(PopupStatus.AsTier(value)) switch
        {
            0 => LocalizationManager.Current.Pick("低", "Calm"),
            1 => LocalizationManager.Current.Pick("中", "Moderate"),
            _ => LocalizationManager.Current.Pick("高", "Dynamic"),
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>VimsTier → pulse status glyph geometry (check / dot / alert), so status
/// is never conveyed by colour alone. OneWay.</summary>
public sealed class TierToPopupStatusGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => PopupStatus.GlyphForIndex(PopupStatus.IndexFor(PopupStatus.AsTier(value)));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>A single 12-meter segment's on/off bool → fill brush
/// (Popup.SegOn when lit, Popup.SegOff when dim). OneWay.</summary>
public sealed class SegmentFillConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool on && on) ? PopupStatus.SegOnBrush : PopupStatus.SegOff;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>VimsTier → localized pulse word (低/中/高 · Calm/Moderate/Dynamic), but as a
/// MULTI-binding: values[0] = VimsTier, values[1] = the live UI language. The language value is
/// only there to RE-TRIGGER the conversion when the user flips language — a plain VimsTier binding
/// stayed stale ("低" shown in English) because the tier doesn't change on a language switch. OneWay.</summary>
public sealed class TierToPopupStatusWordMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        int tier = values is { Length: > 0 } && values[0] is int t ? t : 0;
        return PopupStatus.IndexFor(tier) switch
        {
            0 => LocalizationManager.Current.Pick("低", "Calm"),
            1 => LocalizationManager.Current.Pick("中", "Moderate"),
            _ => LocalizationManager.Current.Pick("高", "Dynamic"),
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
