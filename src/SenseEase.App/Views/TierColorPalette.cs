using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SenseEase.App.Views;

/// <summary>
/// Single source of truth for the <b>5級 動態感</b> colour mapping used by the
/// persistent bar, the system tray icon, and the notification card. Lives here
/// rather than on <see cref="PersistentBarWindow"/> so the tray service can reuse
/// the mapping without depending on a Window class — and so every UI surface
/// converges on one table.
///
/// <para>Adaptive Fit Layer Q2=B: this replaced the legacy 3-band (Grey/Amber/Red)
/// projection. The intensity scale is the native 12 levels; this projects them to
/// the 5級 動態感 the user actually sees (拍板-1, spec §1.1):</para>
/// <list type="bullet">
///   <item>L1–2  → VeryHigh (極高 — most natural, least intervention)</item>
///   <item>L3–4  → High     (高)</item>
///   <item>L5–7  → Medium   (中 — default L5)</item>
///   <item>L8–10 → Low      (低)</item>
///   <item>L11–12→ VeryLow  (極低 — most stabilized, persistent mask)</item>
/// </list>
///
/// <para>Colours are a calm cool ramp (plugin-grounded, MasterPlan §13.6): the
/// ui-ux-pro-max palette DB reserves red for <c>Destructive</c> only — every calm
/// product (Mental Health, Meditation, Medical) uses calm blue/teal — so there is
/// deliberately <b>no alarm red</b>; "more intervention" reads as a deeper, calmer
/// blue, matching the app Accent #3B6FC4 and the non-alarming 動態感 framing.</para>
///
/// <para>Two shapes are exposed:</para>
/// <list type="bullet">
///   <item><see cref="BrushForTier"/> — ambient-overlay alpha ladder (0.15→0.45) for
///         the bar. Soft, deliberately under-visible.</item>
///   <item><see cref="IconForTier"/> — full-alpha 32×32 rendered icon for the tray.
///         The tray icon must be visible against any taskbar background, so it
///         can't reuse the bar's low-alpha brush directly. Same RGB hue, alpha
///         bumped to 1.0 — the level-signal intent is preserved across surfaces.</item>
/// </list>
///
/// Brushes and icons are static + Frozen so they're shareable across threads
/// and produce zero allocation on level-change ticks.
/// </summary>
public static class TierColorPalette
{
    // ── 5級 動態感 hue RGB channels (calm cool ramp, plugin-grounded §13.6) ──
    // VeryHigh = 極高 (most natural) → VeryLow = 極低 (most stabilized). No alarm
    // red: the ui-ux-pro-max palette DB reserves red for Destructive only; every
    // calm product (Mental Health / Meditation / Medical) uses calm blue/teal.
    // Harmonised with the app Accent #3B6FC4. Pinned byte-for-byte by
    // TierColorPaletteTests.BrushForTier_MatchesSpecArgb — keep these numerically
    // identical to the §13.6 table.
    private static readonly Color VeryHighHue = Color.FromRgb(0x9B, 0xB4, 0xD4); // #9BB4D4 airy blue-grey
    private static readonly Color HighHue     = Color.FromRgb(0x6F, 0x93, 0xC2); // #6F93C2 light calm blue
    private static readonly Color MediumHue   = Color.FromRgb(0x4A, 0x77, 0xB5); // #4A77B5 mid calm blue
    private static readonly Color LowHue       = Color.FromRgb(0x34, 0x5E, 0x97); // #345E97 deeper blue
    private static readonly Color VeryLowHue  = Color.FromRgb(0x24, 0x3F, 0x6B); // #243F6B deep calm slate

    // ── Opacity ladder (alpha bytes) ─────────────────────────────────────────
    // The bar deliberately under-renders the level colour; the ladder climbs by
    // 級 so a more-stabilized state is a touch more present without ever shouting.
    // Two rungs per 級: a resting ambient-overlay alpha and a near-opaque hover
    // alpha. Pinned by TierColorPaletteTests.BrushForTier_MatchesSpecArgb.
    private const byte VeryHighAlpha = 0x26; // 0.15 — 極高, barely-there
    private const byte HighAlpha     = 0x38; // 0.22 — 高
    private const byte MediumAlpha   = 0x4D; // 0.30 — 中
    private const byte LowAlpha      = 0x61; // 0.38 — 低
    private const byte VeryLowAlpha  = 0x73; // 0.45 — 極低, most present resting rung
    private const byte VeryHighHoverAlpha = 0xA6; // 0.65 — readable on hover, still soft
    private const byte HighHoverAlpha     = 0xBD; // 0.74
    private const byte MediumHoverAlpha   = 0xD4; // 0.83
    private const byte LowHoverAlpha      = 0xEB; // 0.92
    private const byte VeryLowHoverAlpha  = 0xFF; // 1.00 — fully opaque (most-stabilized 級)

    // Bar brushes — ambient-overlay alpha. Frozen for cross-thread reuse +
    // zero allocation on the StateChanged hot path.
    private static readonly SolidColorBrush VeryHighBrush = MakeFrozen(VeryHighHue, VeryHighAlpha); // 0.15
    private static readonly SolidColorBrush HighBrush     = MakeFrozen(HighHue,     HighAlpha);     // 0.22
    private static readonly SolidColorBrush MediumBrush   = MakeFrozen(MediumHue,   MediumAlpha);   // 0.30
    private static readonly SolidColorBrush LowBrush      = MakeFrozen(LowHue,      LowAlpha);      // 0.38
    private static readonly SolidColorBrush VeryLowBrush  = MakeFrozen(VeryLowHue,  VeryLowAlpha);  // 0.45

    // Hover variants — near-opaque so the level colour reads cleanly when the
    // cursor approaches the bar. 極低 goes fully opaque (the most-stabilized 級).
    private static readonly SolidColorBrush VeryHighHoverBrush = MakeFrozen(VeryHighHue, VeryHighHoverAlpha); // 0.65
    private static readonly SolidColorBrush HighHoverBrush     = MakeFrozen(HighHue,     HighHoverAlpha);     // 0.74
    private static readonly SolidColorBrush MediumHoverBrush   = MakeFrozen(MediumHue,   MediumHoverAlpha);   // 0.83
    private static readonly SolidColorBrush LowHoverBrush      = MakeFrozen(LowHue,      LowHoverAlpha);      // 0.92
    private static readonly SolidColorBrush VeryLowHoverBrush  = MakeFrozen(VeryLowHue,  VeryLowHoverAlpha);  // 1.00

    // D3 liveness contract: the FAULTED bar fill. Muted clay (#B65C4E) at low
    // alpha — "needs attention," explicitly NEVER alarm red, per the locked
    // palette's "muted clay for high strain, never alarm red." A single frozen
    // source so the Faulted bar can't drift into a raw XAML hex literal; it is
    // intentionally OUTSIDE the 級 table (liveness is orthogonal to intensity).
    private static readonly Color ClayHue = Color.FromRgb(0xB6, 0x5C, 0x4E);
    // 0x66 (~0.40): on a 4-dip bar the FILL must carry the calm clay tone so the
    // bar reads "warm / needs attention," not an alarm-red outline. Paired with a
    // softened outline (#80B65C4E in XAML) so the frame defines without shouting.
    public static readonly SolidColorBrush FaultedBarBrush = MakeFrozen(ClayHue, 0x66); // ~0.40

    // Tray icons — full-alpha 32×32 rendered once, frozen, cached by 級.
    private static readonly Dictionary<int, ImageSource> _iconCache = new();

    // System.Drawing.Icon cache for the tray, keyed by 級.
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
    // NOTE (Alpha-1 tray-ICO fix): the producer (IconForTier_Win) and its
    // byte-cache (_winIconBytesCache) were removed — the tray now builds its
    // own multi-size ICO in TrayIconService. _winIconCache is retained only so
    // DisposeWinIconCache() (still called from TrayIconService.Dispose) can
    // best-effort clear any stray entry a future caller might add.
    private static readonly Dictionary<int, System.Drawing.Icon> _winIconCache = new();

    private static SolidColorBrush MakeFrozen(Color rgb, byte alpha)
    {
        var b = new SolidColorBrush(Color.FromArgb(alpha, rgb.R, rgb.G, rgb.B));
        b.Freeze();
        return b;
    }

    /// <summary>Resolves a 1–12 intensity level to the bar's ambient-overlay brush.
    /// Out-of-range levels clamp defensively to the nearest 級 (≤0 → VeryHigh,
    /// ≥13 → VeryLow). Same frozen instance returned for every call within a
    /// 級 — assignable from any thread without WPF freezing complaints.</summary>
    public static SolidColorBrush BrushForTier(int tier) => BandForTier(tier) switch
    {
        TierBand.VeryHigh => VeryHighBrush,
        TierBand.High     => HighBrush,
        TierBand.Medium   => MediumBrush,
        TierBand.Low      => LowBrush,
        TierBand.VeryLow  => VeryLowBrush,
        _                 => VeryHighBrush, // unreachable; satisfies exhaustiveness
    };

    /// <summary>Hover-state variant of <see cref="BrushForTier"/>. Same hue,
    /// higher alpha — used by <see cref="PersistentBarWindow"/> when the cursor
    /// enters the hover-proximity zone so the level colour reads clearly.
    /// Frozen + cached, identical lifetime to the base brushes.</summary>
    public static SolidColorBrush HoverBrushForTier(int tier) => BandForTier(tier) switch
    {
        TierBand.VeryHigh => VeryHighHoverBrush,
        TierBand.High     => HighHoverBrush,
        TierBand.Medium   => MediumHoverBrush,
        TierBand.Low      => LowHoverBrush,
        TierBand.VeryLow  => VeryLowHoverBrush,
        _                 => VeryHighHoverBrush,
    };

    private static Color HueForTier(int tier) => BandForTier(tier) switch
    {
        TierBand.VeryHigh => VeryHighHue,
        TierBand.High     => HighHue,
        TierBand.Medium   => MediumHue,
        TierBand.Low      => LowHue,
        TierBand.VeryLow  => VeryLowHue,
        _                 => VeryHighHue,
    };

    /// <summary>Resolves a 1–12 intensity level to a 32×32 ImageSource for the
    /// system tray icon. Full-alpha rendering (the tray icon must be visible
    /// against any taskbar background — low-alpha would disappear on light
    /// themes). Cached per 級; first call allocates, subsequent calls return the
    /// cached frozen RenderTargetBitmap.</summary>
    public static ImageSource IconForTier(int tier)
    {
        int bandKey = (int)BandForTier(tier);
        if (_iconCache.TryGetValue(bandKey, out var cached)) return cached;

        var bmp = RenderIconBitmap(HueForTier(tier));
        _iconCache[bandKey] = bmp;
        return bmp;
    }

    // ── Removed (Alpha-1 tray-ICO fix): IconForTier_Win + BuildPngWrappedIco +
    // GetIcoBytesForTesting. They hand-built a SINGLE 32 px PNG-in-ICO frame,
    // which the Win11 shell down-scaled to the 16 px tray slot and rendered
    // blurred / blank (the PNG-at-16px bug). The tray no longer uses this path —
    // TrayIconService draws its own multi-size ICO (16/24/32/48 px BGRA-DIB
    // frames) via BuildMultiSizeIco. The _winIconCache + DisposeWinIconCache
    // below are kept so TrayIconService.Dispose()'s defensive cleanup of any
    // stray cache entry still compiles and runs as a no-op.

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

    /// <summary>The five 動態感 級 (拍板-1). Public so tests can name them; the enum
    /// values double as cache keys in <see cref="_iconCache"/>. Ordered calm→stabilized
    /// (VeryHigh = 極高 = most natural; VeryLow = 極低 = most stabilized) so the
    /// integer ordering matches intervention intensity.</summary>
    public enum TierBand
    {
        VeryHigh = 0, // 極高
        High     = 1, // 高
        Medium   = 2, // 中
        Low      = 3, // 低
        VeryLow  = 4, // 極低
    }

    /// <summary>Pure level → 級 lookup (spec §1.1, 拍板-1). The one place this
    /// knowledge lives. Defensive clamping: level ≤ 0 → VeryHigh, level ≥ 13 → VeryLow.</summary>
    public static TierBand BandForTier(int tier) => tier switch
    {
        <= 2  => TierBand.VeryHigh, // L1–2 (and defensively ≤ 0)
        <= 4  => TierBand.High,     // L3–4
        <= 7  => TierBand.Medium,   // L5–7 (default L5)
        <= 10 => TierBand.Low,      // L8–10
        _     => TierBand.VeryLow,  // L11–12 (and defensively ≥ 13)
    };

    /// <summary>The user-facing 5級「畫面動態感」status label (中文) — the ONLY status
    /// shown to end users, and the single source every surface (bar / tray / card / hero)
    /// uses so the wording can't drift. ONE axis, correct polarity: a calm / stable screen
    /// is LOW 動態感, a busy / dynamic screen is HIGH 動態感. Per the unified-measurement
    /// directive (2026-06-07): L1–2 極低 (Very Calm) … L11–12 極高 (Very Dynamic). The band
    /// enum is the internal comfort framing — a VeryHigh-comfort band (L1–2) is the most
    /// natural / stable screen, hence the LOWEST 動態感 (極低). §6-safe (no medical claims).</summary>
    public static string FeelLabel(TierBand band) => band switch
    {
        TierBand.VeryHigh => "動態感 · 極低",  // L1–2   Very Calm  (most stable screen)
        TierBand.High     => "動態感 · 低",    // L3–4   Calm
        TierBand.Medium   => "動態感 · 中",    // L5–7   Moderate
        TierBand.Low      => "動態感 · 高",    // L8–10  Dynamic
        TierBand.VeryLow  => "動態感 · 極高",  // L11–12 Very Dynamic (most motion)
        _                 => "動態感 · 中",
    };

    /// <summary>English 5級「screen motion」status (§6-safe). Same root word at both ends
    /// (Calm ⇄ Dynamic) so continuity + direction read at a glance. Pairs with
    /// <see cref="FeelLabel"/>; same polarity (VeryHigh-comfort band = Very Calm).</summary>
    public static string FeelLabelEn(TierBand band) => band switch
    {
        TierBand.VeryHigh => "Very Calm",    // L1–2
        TierBand.High     => "Calm",         // L3–4
        TierBand.Medium   => "Moderate",     // L5–7
        TierBand.Low      => "Dynamic",      // L8–10
        TierBand.VeryLow  => "Very Dynamic", // L11–12
        _                 => "Moderate",
    };

    /// <summary>Convenience: 1–12 level → 5級 中文 label.</summary>
    public static string FeelLabelForTier(int tier) => FeelLabel(BandForTier(tier));
}
