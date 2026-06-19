using System.Windows.Media;

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
/// <para>The bar's level-colour shape is exposed:</para>
/// <list type="bullet">
///   <item><see cref="BrushForTier"/> — ambient-overlay alpha ladder (0.15→0.45) for
///         the bar. Soft, deliberately under-visible.</item>
/// </list>
/// (The tray icon is built separately by <c>TrayIconService.BuildDistinctTrayIcon</c> — a
/// constant indigo lens, not a per-tier tone, since D-10 "Alpha-1 TrayBrand".)
///
/// Brushes are static + Frozen so they're shareable across threads
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

    /// <summary>The five 動態感 級 (拍板-1). Public so tests can name them. Ordered calm→stabilized
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
        TierBand.VeryHigh => "畫面動態 · 極低",  // L1–2   Very Calm  (most stable screen)
        TierBand.High     => "畫面動態 · 低",    // L3–4   Calm
        TierBand.Medium   => "畫面動態 · 中",    // L5–7   Moderate
        TierBand.Low      => "畫面動態 · 高",    // L8–10  Dynamic
        TierBand.VeryLow  => "畫面動態 · 極高",  // L11–12 Very Dynamic (most motion)
        _                 => "畫面動態 · 中",
    };

    /// <summary>English 5級「screen motion」status (§6-safe). Same root word at both ends
    /// (Calm ⇄ Dynamic) so continuity + direction read at a glance. Pairs with
    /// <see cref="FeelLabel"/>; same polarity (VeryHigh-comfort band = Very Calm).</summary>
    public static string FeelLabelEn(TierBand band) => band switch
    {
        TierBand.VeryHigh => "Screen Motion · Very Calm",    // L1–2
        TierBand.High     => "Screen Motion · Calm",         // L3–4
        TierBand.Medium   => "Screen Motion · Moderate",     // L5–7
        TierBand.Low      => "Screen Motion · Dynamic",      // L8–10
        TierBand.VeryLow  => "Screen Motion · Very Dynamic", // L11–12
        _                 => "Screen Motion · Moderate",
    };

    /// <summary>Convenience: 1–12 level → 5級 中文 label.</summary>
    public static string FeelLabelForTier(int tier) => FeelLabel(BandForTier(tier));

    /// <summary>Band-only 中文 word (no "畫面動態 ·" prefix) — used by the pop-up card,
    /// which carries its own 「目前螢幕狀態」 caption so the prefix would be redundant.
    /// Other surfaces (bar / tray / hero) keep the full <see cref="FeelLabel"/>.</summary>
    public static string FeelBand(TierBand band) => band switch
    {
        TierBand.VeryHigh => "極低",  // L1–2
        TierBand.High     => "低",    // L3–4
        TierBand.Medium   => "中",    // L5–7
        TierBand.Low      => "高",    // L8–10
        TierBand.VeryLow  => "極高",  // L11–12
        _                 => "中",
    };

    /// <summary>Band-only English word (no "Screen Motion ·" prefix). Pairs with
    /// <see cref="FeelBand"/> for the pop-up card.</summary>
    public static string FeelBandEn(TierBand band) => band switch
    {
        TierBand.VeryHigh => "Very Calm",    // L1–2
        TierBand.High     => "Calm",         // L3–4
        TierBand.Medium   => "Moderate",     // L5–7
        TierBand.Low      => "Dynamic",      // L8–10
        TierBand.VeryLow  => "Very Dynamic", // L11–12
        _                 => "Moderate",
    };
}
