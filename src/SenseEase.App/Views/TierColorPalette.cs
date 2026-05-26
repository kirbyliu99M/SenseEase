using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SenseEase.App.Views;

/// <summary>
/// Pop-up sprint single source of truth for the 3-tier colour mapping used by
/// the persistent bar (Task 2) AND the system tray icon (Task 3). Lives here
/// rather than on <see cref="PersistentBarWindow"/> so the tray service can
/// reuse the mapping without taking a dependency on a Window class — and so
/// future UI surfaces (notification card, settings indicators, etc.) all
/// converge on one table.
///
/// <para>Tier bands (user decision 2026-05-26):</para>
/// <list type="bullet">
///   <item>Tier 1–5  → grey  (RGB 102,102,102)</item>
///   <item>Tier 6–7  → amber (RGB 230,140,40)</item>
///   <item>Tier 8–10 → red   (RGB 220, 50,40)</item>
/// </list>
///
/// <para>Two shapes are exposed:</para>
/// <list type="bullet">
///   <item><see cref="BrushForTier"/> — ambient-overlay alpha (0.15 / 0.30 / 0.45) for
///         the bar. Soft, deliberately under-visible.</item>
///   <item><see cref="IconForTier"/> — full-alpha 32×32 rendered icon for the tray.
///         The tray icon must be visible against any taskbar background, so it
///         can't reuse the bar's low-alpha brush directly. Same RGB hue, alpha
///         bumped to 1.0 — the tier-signal intent is preserved across surfaces.</item>
/// </list>
///
/// Brushes and icons are static + Frozen so they're shareable across threads
/// and produce zero allocation on tier-change ticks.
/// </summary>
public static class TierColorPalette
{
    // Shared hue table — RGB only. Bar adds low alpha; tray icon renders at
    // full alpha. Changing a hue here changes every consuming surface in lock
    // step (the only way to avoid bar-amber-but-tray-still-grey drift bugs).
    private static readonly Color GreyHue  = Color.FromRgb(102, 102, 102);
    private static readonly Color AmberHue = Color.FromRgb(230, 140,  40);
    private static readonly Color RedHue   = Color.FromRgb(220,  50,  40);

    // Bar brushes — ambient-overlay alpha. Frozen for cross-thread reuse +
    // zero allocation on the StateChanged hot path.
    private static readonly SolidColorBrush GreyBrush  = MakeFrozen(GreyHue,  0x26); // 0.15
    private static readonly SolidColorBrush AmberBrush = MakeFrozen(AmberHue, 0x4D); // 0.30
    private static readonly SolidColorBrush RedBrush   = MakeFrozen(RedHue,   0x73); // 0.45

    // Tray icons — full-alpha 32×32 rendered once, frozen, cached by tier band.
    private static readonly Dictionary<int, ImageSource> _iconCache = new();

    private static SolidColorBrush MakeFrozen(Color rgb, byte alpha)
    {
        var b = new SolidColorBrush(Color.FromArgb(alpha, rgb.R, rgb.G, rgb.B));
        b.Freeze();
        return b;
    }

    /// <summary>Resolves a 1–10 VIMS tier to the bar's ambient-overlay brush.
    /// Out-of-range tiers clamp defensively to the nearest band (≤0 → grey,
    /// ≥11 → red). Same frozen instance returned for every call within a
    /// band — assignable from any thread without WPF freezing complaints.</summary>
    public static SolidColorBrush BrushForTier(int tier) => BandForTier(tier) switch
    {
        TierBand.Grey  => GreyBrush,
        TierBand.Amber => AmberBrush,
        TierBand.Red   => RedBrush,
        _              => GreyBrush, // unreachable; satisfies exhaustiveness
    };

    /// <summary>Resolves a 1–10 VIMS tier to a 32×32 ImageSource for the system
    /// tray icon. Full-alpha rendering (the tray icon must be visible against
    /// any taskbar background — low-alpha would disappear on light themes).
    /// Cached per band; first call allocates, subsequent calls return the
    /// cached frozen RenderTargetBitmap.</summary>
    public static ImageSource IconForTier(int tier)
    {
        int bandKey = (int)BandForTier(tier);
        if (_iconCache.TryGetValue(bandKey, out var cached)) return cached;

        Color hue = BandForTier(tier) switch
        {
            TierBand.Grey  => GreyHue,
            TierBand.Amber => AmberHue,
            TierBand.Red   => RedHue,
            _              => GreyHue,
        };
        var bmp = RenderIconBitmap(hue);
        _iconCache[bandKey] = bmp;
        return bmp;
    }

    private static ImageSource RenderIconBitmap(Color hue)
    {
        // 32×32 rounded square, fully opaque, with a 2 px transparent margin
        // so the shape doesn't touch taskbar edges. Hue is full-alpha — alpha
        // signalling is the BAR's job, not the tray icon's (tray icons need
        // to be visible regardless of taskbar background).
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            var fill = new SolidColorBrush(hue);
            fill.Freeze();
            ctx.DrawRoundedRectangle(fill, null, new Rect(2, 2, 28, 28), 6, 6);
        }
        var rtb = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        // H.NotifyIcon.Wpf 2.3.0 explicitly throws NotImplementedException when
        // a RenderTargetBitmap is assigned to TaskbarIcon.IconSource (its
        // ImageExtensions.ToStreamAsync only handles a fixed set of types).
        // Round-trip through PNG so the result is a BitmapImage — the canonical
        // type H.NotifyIcon's converter accepts. Tiny allocation cost on first
        // call per band (~50 bytes of PNG); cached afterwards.
        //
        // This bug shipped through Task 3's unit tests because the test
        // asserted IsType<RenderTargetBitmap> — pinning the implementation
        // (return type) instead of the contract (must be convertible by the
        // production consumer). Replacement tests in TierColorPaletteTests
        // now assert via IsAssignableFrom<BitmapSource> + IsNotType<RTB> AND
        // call H.NotifyIcon.ImageExtensions.ToStreamAsync directly to verify
        // the production library can actually consume the icon.
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption  = BitmapCacheOption.OnLoad; // detach from stream so we can dispose ms
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Three colour bands. Public so tests can name them; the enum
    /// values double as cache keys in <see cref="_iconCache"/>.</summary>
    public enum TierBand
    {
        Grey  = 0,
        Amber = 1,
        Red   = 2,
    }

    /// <summary>Pure tier → band lookup. The one place this knowledge lives.
    /// Defensive clamping: tier ≤ 0 → Grey, tier ≥ 11 → Red.</summary>
    public static TierBand BandForTier(int tier) => tier switch
    {
        <= 5  => TierBand.Grey,
        6 or 7 => TierBand.Amber,
        _     => TierBand.Red, // 8..10 and defensively ≥11
    };
}
