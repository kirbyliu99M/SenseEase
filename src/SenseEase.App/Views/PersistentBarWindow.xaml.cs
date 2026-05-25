using System;
using System.Windows;
using System.Windows.Media;

namespace SenseEase.App.Views;

/// <summary>
/// Pop-up sprint Task 2: the persistent ambient bar. 4 dip × 80 dip, top-centre
/// of the primary monitor's work area, always Topmost + AllowsTransparency, never
/// shown in taskbar. NO flashing/pulsing/animation per spec §1 — visual motion
/// would violate the "do not disturb" principle.
/// <para>Three-tier colour mapping (per user decision recorded 2026-05-26):</para>
/// <list type="bullet">
///   <item>Tier 1–5 → grey rgba(102,102,102,0.15)</item>
///   <item>Tier 6–7 → amber rgba(230,140,40,0.30)</item>
///   <item>Tier 8–10 → red   rgba(220, 50,40,0.45)</item>
/// </list>
/// <para>Task 2 ships render-only: hover (cursor-proximity grow), click-to-
/// expand, and Toggle B (colour-on/off) all land in Tasks 4–5. The bar is also
/// always click-through in Task 2 (IsHitTestVisible=False in XAML) — flipping
/// that back to true is Task 4's job.</para>
/// <para>Always-on mask (Option II, user decision 2026-05-26): on ContentRendered
/// the caller is expected to read GetMonitorRectInPhysicalPixels() and engage
/// WgcSensingProvider's exclude rect; on Closed the caller clears it. See
/// MainWindow.MaybeStartPersistentBar for the wiring.</para>
/// </summary>
public partial class PersistentBarWindow : Window
{
    // Three-tier colour table. Defined here (not in XAML) so the mapping is
    // testable + has a single source of truth that downstream readers can grep.
    // Tier <=5 → grey; 6-7 → amber; 8-10 → red. Out-of-range clamps to nearest.
    private static readonly SolidColorBrush GreyBrush  = MakeFrozen(Color.FromArgb(0x26, 0x66, 0x66, 0x66));
    private static readonly SolidColorBrush AmberBrush = MakeFrozen(Color.FromArgb(0x4D, 0xE6, 0x8C, 0x28));
    private static readonly SolidColorBrush RedBrush   = MakeFrozen(Color.FromArgb(0x73, 0xDC, 0x32, 0x28));

    private static SolidColorBrush MakeFrozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Resolves a 1–10 tier integer to the right brush. Clamps
    /// outside-range values to the nearest band (defensive — engine should
    /// only emit 1..10, but a future contract change shouldn't crash the bar).</summary>
    public static SolidColorBrush BrushForTier(int tier) => tier switch
    {
        <= 5 => GreyBrush,
        6 or 7 => AmberBrush,
        _ => RedBrush,    // 8..10 (and any >10 defensively)
    };

    public PersistentBarWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Top-centre on the primary monitor's work area, flush with the top edge.
        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - Width) / 2.0;
        Top  = work.Top;
    }

    /// <summary>Updates the bar's fill brush for the given VIMS tier. Called
    /// from MainWindow's StateChanged subscriber. Idempotent — same tier =
    /// no-op assignment of the same frozen brush, no extra layout work.</summary>
    public void SetTier(int tier)
    {
        BarFill.Background = BrushForTier(tier);
    }

    /// <summary>Monitor-coords physical-pixel rectangle for the WGC exclude
    /// mask. DPI-aware via VisualTreeHelper.GetDpi. Call after ContentRendered
    /// so ActualWidth/ActualHeight are populated. Same shape as
    /// BarStubWindow.GetMonitorRectInPhysicalPixels — symmetric API so the
    /// MainWindow harness uses identical code for both stub and production bar.</summary>
    public (int Left, int Top, int Width, int Height) GetMonitorRectInPhysicalPixels()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX;
        double sy = dpi.DpiScaleY;
        int left   = (int)Math.Floor(Left * sx);
        int top    = (int)Math.Floor(Top  * sy);
        int width  = (int)Math.Ceiling(ActualWidth  * sx);
        int height = (int)Math.Ceiling(ActualHeight * sy);
        return (left, top, width, height);
    }
}
