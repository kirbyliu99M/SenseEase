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

    // System.Drawing.Icon cache for the tray, keyed by tier band.
    //
    // Evolution across the sprint:
    //   • Patch 3 (a47fb95): Bitmap.GetHicon() + Icon.FromHandle. Required
    //     manual DestroyIcon at app exit because FromHandle doesn't own
    //     the unmanaged HICON. Worked at the API level but produced an
    //     HICON without proper ICO directory structure — Win11 shell
    //     registered the tray entry but silently rejected the graphic
    //     (the "entry exists but no icon" symptom user reported after
    //     Patch 5 library swap proved registration itself was working).
    //   • Patch 5 (3984c16): library swap H.NotifyIcon → Hardcodet, no
    //     change to icon construction.
    //   • Patch 6: attempted Bitmap.Save(stream, ImageFormat.Icon) +
    //     new Icon(stream). Threw ArgumentNullException because
    //     System.Drawing.Common on .NET 8 has NO ICO encoder — the API
    //     exists but isn't implemented (open gap since .NET Core 3.x).
    //   • Patch 7 (this commit): hand-build ICO container wrapping a
    //     PNG payload (Win Vista+ accepts PNG-in-ICO). System.Drawing's
    //     PNG encoder works; we just write the 6-byte ICONDIR + 16-byte
    //     ICONDIRENTRY ourselves. Icon(Stream) ctor takes ownership, so
    //     Icon.Dispose() is sufficient — no manual DestroyIcon needed.
    //
    // _winIconBytesCache parallel-stores the raw ICO bytes so tests can
    // verify the exact structure we wrote, without round-tripping through
    // Win32's icon serialiser (which may normalise).
    private static readonly Dictionary<int, System.Drawing.Icon> _winIconCache = new();
    private static readonly Dictionary<int, byte[]> _winIconBytesCache = new();

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

    /// <summary>Returns a <see cref="System.Drawing.Icon"/> for the tier band.
    /// This is the path the tray uses in production (via Hardcodet
    /// <c>TaskbarIcon.Icon</c>, not <c>TaskbarIcon.IconSource</c>). Cached per
    /// band; cached Icons are released by <see cref="DisposeWinIconCache"/>
    /// at app shutdown.
    /// <para><b>Icon construction</b> (Patch 7): the bitmap is rendered to a
    /// PNG byte stream, then wrapped in a hand-built ICO container
    /// (6-byte ICONDIR + 16-byte ICONDIRENTRY pointing at the PNG payload).
    /// Win Vista+ shell accepts PNG-in-ICO. We do this manually because
    /// System.Drawing.Common on .NET 8 has no ICO encoder — its
    /// <c>ImageFormat.Icon</c> path throws <c>ArgumentNullException</c>
    /// (Patch 6's failed attempt). The PNG encoder works, so we lean on
    /// that for the pixel data and write the ICO header bytes ourselves.</para>
    /// <para><c>Icon(Stream)</c> constructor takes ownership of any handles
    /// it allocates, so <c>Icon.Dispose()</c> alone is sufficient — no
    /// manual <c>DestroyIcon</c> P/Invoke required.</para></summary>
    public static System.Drawing.Icon IconForTier_Win(int tier)
    {
        int bandKey = (int)BandForTier(tier);
        if (_winIconCache.TryGetValue(bandKey, out var cached)) return cached;

        Color hue = BandForTier(tier) switch
        {
            TierBand.Grey  => GreyHue,
            TierBand.Amber => AmberHue,
            TierBand.Red   => RedHue,
            _              => GreyHue,
        };

        // 32×32 GDI+ Bitmap. We do NOT round the corners — at 32 px the
        // 2-px radius the WPF version uses is barely visible after the
        // shell scales the icon down to 16×16, and FillRoundedRectangle
        // isn't built into GDI+ (would require manual GraphicsPath arcs).
        // The tier signal is the colour, not the corner radius.
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var brush = new System.Drawing.SolidBrush(
                System.Drawing.Color.FromArgb(255, hue.R, hue.G, hue.B));
            // 2 px transparent margin so the shape doesn't touch the shell's
            // icon bounding box edges.
            g.FillRectangle(brush, 2, 2, 28, 28);
        }

        byte[] icoBytes = BuildPngWrappedIco(bmp);
        using var stream = new MemoryStream(icoBytes);
        var icon = new System.Drawing.Icon(stream);
        _winIconCache[bandKey]      = icon;
        _winIconBytesCache[bandKey] = icoBytes;
        return icon;
    }

    /// <summary>Patch 7: builds an in-memory ICO file wrapping the bitmap
    /// as a PNG image (Win Vista+ accepts this format). The ICO structure:
    /// <list type="bullet">
    ///   <item>ICONDIR (6 bytes): Reserved + Type=1 + Count=1</item>
    ///   <item>ICONDIRENTRY (16 bytes): Width + Height + ColorCount + Reserved
    ///         + Planes + BitCount + ImageSize + ImageOffset</item>
    ///   <item>Image data: the raw PNG bytes</item>
    /// </list>
    /// <para>Two byte-vs-int landmines this code is careful to avoid:</para>
    /// <list type="bullet">
    ///   <item>Width / Height are written as single BYTES (not ints).
    ///         Writing as int would push subsequent fields out of alignment
    ///         by 6 bytes total and the shell would reject the ICO.</item>
    ///   <item>BitCount is 32 (RGBA, matching the PNG's actual pixel
    ///         format) — explicitly NOT 0, which some older Windows shell
    ///         renderers would reject.</item>
    /// </list></summary>
    private static byte[] BuildPngWrappedIco(System.Drawing.Bitmap bmp)
    {
        // Step 1: encode the bitmap as PNG. The GDI+ PNG encoder IS
        // present on .NET 8 / Windows (unlike the ICO encoder).
        byte[] pngBytes;
        using (var pngStream = new MemoryStream())
        {
            bmp.Save(pngStream, System.Drawing.Imaging.ImageFormat.Png);
            pngBytes = pngStream.ToArray();
        }

        // Step 2: write the ICO container around the PNG.
        using var icoStream = new MemoryStream();
        using (var writer = new BinaryWriter(
            icoStream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            // ── ICONDIR (6 bytes) ────────────────────────────────────
            writer.Write((ushort)0);   // Reserved
            writer.Write((ushort)1);   // Type = 1 (ICO; 2 = CUR cursor)
            writer.Write((ushort)1);   // ImageCount = 1

            // ── ICONDIRENTRY (16 bytes) ──────────────────────────────
            writer.Write((byte)32);    // Width  (BYTE; 0 = 256)
            writer.Write((byte)32);    // Height (BYTE; 0 = 256)
            writer.Write((byte)0);     // ColorCount: 0 for ≥ 256 colors / PNG
            writer.Write((byte)0);     // Reserved
            writer.Write((ushort)1);   // Planes
            writer.Write((ushort)32);  // BitCount: 32 (RGBA), not 0 — safer for Win11
            writer.Write((uint)pngBytes.Length); // ImageSize
            writer.Write((uint)22);    // ImageOffset = 6 (ICONDIR) + 16 (ICONDIRENTRY)
        }
        icoStream.Write(pngBytes, 0, pngBytes.Length);
        return icoStream.ToArray();
    }

    /// <summary>Test-only accessor: returns the raw ICO bytes for a tier
    /// band so structure-contract tests can verify the exact bytes we
    /// wrote (without round-tripping through Win32's Icon serialiser
    /// which may normalise). Forces icon creation if the band hasn't
    /// been used yet. Internal + InternalsVisibleTo SenseEase.Tests
    /// (already wired in the csproj).</summary>
    internal static byte[] GetIcoBytesForTesting(int tier)
    {
        int bandKey = (int)BandForTier(tier);
        if (!_winIconBytesCache.ContainsKey(bandKey))
        {
            // Force population by creating the Icon (also caches the bytes).
            _ = IconForTier_Win(tier);
        }
        return _winIconBytesCache[bandKey];
    }

    /// <summary>App-shutdown cleanup. Disposes every cached Icon. Safe to
    /// call multiple times; subsequent calls are no-ops (cache cleared
    /// after first). Called from <c>TrayIconService.Dispose()</c>.
    /// <para>Patch 6: the prior manual <c>DestroyIcon</c> P/Invoke loop is
    /// gone — Patch 3's <c>Icon.FromHandle()</c> needed it because that
    /// ctor doesn't take handle ownership. <c>Icon(Stream)</c> DOES take
    /// ownership, so <c>Icon.Dispose()</c> alone fully releases everything.</para></summary>
    public static void DisposeWinIconCache()
    {
        foreach (var icon in _winIconCache.Values)
        {
            try { icon.Dispose(); } catch { /* best-effort */ }
        }
        _winIconCache.Clear();
        _winIconBytesCache.Clear();
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

        // Historical context — H.NotifyIcon.Wpf 2.3.0 (the tray library used
        // before Patch 5's swap to Hardcodet) explicitly threw
        // NotImplementedException when a RenderTargetBitmap was assigned to
        // TaskbarIcon.IconSource (its ImageExtensions.ToStreamAsync only
        // handled a fixed set of types). The fix below — round-trip through
        // PNG so the result is a BitmapImage — became unused once production
        // switched to the System.Drawing.Icon path (IconForTier_Win above),
        // but the method is kept as back-compat for any future caller that
        // still wants an ImageSource. The "must be assignable to BitmapSource
        // AND must NOT be RenderTargetBitmap" contract still holds for any
        // ImageSource consumer; the test
        // IconForTier_ReturnsTypeAcceptableToTrayLibrary pins it.
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
