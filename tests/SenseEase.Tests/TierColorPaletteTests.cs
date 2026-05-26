using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SenseEase.App.Views;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Pop-up sprint Task 3 — tests for the single source of truth on
/// tier → colour mapping. Bar and tray both consume this; tests pin the
/// invariants so a future "I'll just tweak the amber a bit" edit can't
/// silently desync the two surfaces.</summary>
public class TierColorPaletteTests
{
    // ── Band lookup ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-5,  TierColorPalette.TierBand.Grey)]
    [InlineData(0,   TierColorPalette.TierBand.Grey)]
    [InlineData(1,   TierColorPalette.TierBand.Grey)]
    [InlineData(5,   TierColorPalette.TierBand.Grey)]
    [InlineData(6,   TierColorPalette.TierBand.Amber)]
    [InlineData(7,   TierColorPalette.TierBand.Amber)]
    [InlineData(8,   TierColorPalette.TierBand.Red)]
    [InlineData(10,  TierColorPalette.TierBand.Red)]
    [InlineData(11,  TierColorPalette.TierBand.Red)]   // defensive clamp
    [InlineData(999, TierColorPalette.TierBand.Red)]
    public void BandForTier_ReturnsExpectedBand(int tier, TierColorPalette.TierBand expected)
    {
        Assert.Equal(expected, TierColorPalette.BandForTier(tier));
    }

    // ── Brush (bar surface) ──────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 0x26, 0x66, 0x66, 0x66)] // grey, alpha 0.15
    [InlineData(5, 0x26, 0x66, 0x66, 0x66)]
    [InlineData(6, 0x4D, 0xE6, 0x8C, 0x28)] // amber, alpha 0.30
    [InlineData(7, 0x4D, 0xE6, 0x8C, 0x28)]
    [InlineData(8, 0x73, 0xDC, 0x32, 0x28)] // red,   alpha 0.45
    [InlineData(10, 0x73, 0xDC, 0x32, 0x28)]
    public void BrushForTier_MatchesSpecArgb(int tier, byte a, byte r, byte g, byte b)
    {
        var brush = TierColorPalette.BrushForTier(tier);
        Assert.Equal(a, brush.Color.A);
        Assert.Equal(r, brush.Color.R);
        Assert.Equal(g, brush.Color.G);
        Assert.Equal(b, brush.Color.B);
    }

    [Fact]
    public void BrushForTier_ReturnsFrozenSharedInstances()
    {
        // Same band → same frozen instance (no per-call allocation on the hot path).
        var grey1 = TierColorPalette.BrushForTier(1);
        var grey5 = TierColorPalette.BrushForTier(5);
        Assert.Same(grey1, grey5);
        Assert.True(grey1.IsFrozen);

        var amber6 = TierColorPalette.BrushForTier(6);
        var amber7 = TierColorPalette.BrushForTier(7);
        Assert.Same(amber6, amber7);

        var red8  = TierColorPalette.BrushForTier(8);
        var red10 = TierColorPalette.BrushForTier(10);
        Assert.Same(red8, red10);
    }

    // ── Icon (tray surface) ──────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(10)]
    public void IconForTier_ReturnsTypeAcceptableToHNotifyIcon(int tier)
    {
        // Bugfix on Task 3 (overlay-bar-redesign): the original assertion was
        // IsType<RenderTargetBitmap>, which pinned the IMPLEMENTATION
        // (concrete return type) instead of the CONTRACT (the consumer must
        // be able to render it). H.NotifyIcon.Wpf 2.3.0's IconSource
        // converter explicitly throws on RenderTargetBitmap — so the original
        // test "passed" by asserting exactly the type that crashed in prod.
        //
        // New assertions: must be a BitmapSource subclass (the widest type
        // H.NotifyIcon's converter handles) AND must NOT be a RenderTargetBitmap
        // (the one BitmapSource subclass that crashes). For the canonical
        // contract verification — "can H.NotifyIcon actually consume this?" —
        // see IconForTier_CanBeConvertedByHNotifyIcon below.
        var icon = TierColorPalette.IconForTier(tier);
        var bmp = Assert.IsAssignableFrom<BitmapSource>(icon);
        Assert.IsNotType<RenderTargetBitmap>(icon);
        Assert.Equal(32, bmp.PixelWidth);
        Assert.Equal(32, bmp.PixelHeight);
        Assert.True(bmp.IsFrozen);
    }

    // ── Contract test attempted, parked ──────────────────────────────────────
    //
    // The "strongest test" per sprint guidance would directly invoke
    // H.NotifyIcon's ToStreamAsync converter — proving "the production library
    // ACTUALLY accepts this icon". I attempted this via two paths and both
    // hit barriers:
    //
    //   1. Reflection into H.NotifyIcon.ImageExtensions: the static method
    //      lookup returned non-null (Assert.NotNull passed) but Invoke produced
    //      a null Task. The library's partial-class layout
    //      (ImageExtensions.Wpf.cs + ImageExtensions.cs) and likely InternalsVisibleTo
    //      pattern make the call site fragile. A working invocation would
    //      require matching the partial-class method dispatch exactly.
    //
    //   2. Instantiating TaskbarIcon directly and assigning IconSource: this
    //      WORKS as a regression guard but requires an STA thread + WPF
    //      dispatcher, which xUnit doesn't provide by default.
    //
    // The honest fallback is the two assertions in
    // `IconForTier_ReturnsTypeAcceptableToHNotifyIcon` above:
    //   • IsAssignableFrom<BitmapSource> — establishes the contract floor
    //   • IsNotType<RenderTargetBitmap>  — pins the specific known-bad type
    //
    // Together they catch the exact regression that crashed Task 3 ship. A
    // proper contract test (real TaskbarIcon on a real STA dispatcher, or
    // a reflection harness that works) is one of the deliverables of the
    // E2E WPF test infrastructure scoped before Habituation Phase 6/7/8.
    // See PR description "Lessons Learned" section.

    // ── Fix 3 contract tests (System.Drawing.Icon path) ─────────────────────
    //
    // These cover the path the tray ACTUALLY uses in production. The
    // ImageSource path (IconForTier above) is back-compat only — the tray
    // doesn't set IconSource anymore. If a future sprint deletes the
    // ImageSource path entirely, delete IconForTier_ReturnsTypeAcceptableToHNotifyIcon
    // with it; the assertions below are the load-bearing ones.

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(9)]
    public void IconForTier_Win_ReturnsIconWithValidHandle(int tier)
    {
        // Pop-up sprint Fix 3 (Task 7 patch 3): contract test for the
        // System.Drawing.Icon path. Non-zero Handle is the minimum the
        // Win32 Shell_NotifyIcon contract requires — if GetHicon() ever
        // regresses to returning IntPtr.Zero (unlikely but possible across
        // GDI+ updates), this fires before reaching production.
        //
        // We do NOT dispose the icon — TierColorPalette caches it and owns
        // the lifecycle. Disposing here would invalidate the cached handle
        // for the production tray.
        var icon = TierColorPalette.IconForTier_Win(tier);
        Assert.NotNull(icon);
        Assert.NotEqual(IntPtr.Zero, icon.Handle);
    }

    [Fact]
    public void IconForTier_Win_ReturnsSameInstancePerBand()
    {
        // Two tiers in the same band must return the cached icon. Re-creating
        // the icon (and HICON) per state-change tick would leak handles
        // because the cache index, not GC, is what eventually calls
        // DestroyIcon. Same-band re-fetch MUST hit the cache.
        Assert.Same(TierColorPalette.IconForTier_Win(1),  TierColorPalette.IconForTier_Win(5));
        Assert.Same(TierColorPalette.IconForTier_Win(6),  TierColorPalette.IconForTier_Win(7));
        Assert.Same(TierColorPalette.IconForTier_Win(8),  TierColorPalette.IconForTier_Win(10));
    }

    [Fact]
    public void IconForTier_Win_DifferentBandsReturnDifferentInstances()
    {
        Assert.NotSame(TierColorPalette.IconForTier_Win(1),  TierColorPalette.IconForTier_Win(6));
        Assert.NotSame(TierColorPalette.IconForTier_Win(6),  TierColorPalette.IconForTier_Win(10));
        Assert.NotSame(TierColorPalette.IconForTier_Win(1),  TierColorPalette.IconForTier_Win(10));
    }

    [Fact(Skip = "Manual STA smoke — instantiates real H.NotifyIcon.TaskbarIcon " +
                  "and exercises IconForTier_Win across all 3 bands + Dispose. " +
                  "Runs on net8.0-windows but xUnit's default scheduler isn't STA. " +
                  "Will be promoted to [Fact] once the E2E WPF test infrastructure " +
                  "(FlaUI / StaFact / WpfApplicationFactory pattern) is scoped " +
                  "before Habituation Phase 6/7/8 sprint opens. Until then, the " +
                  "IconForTier_Win_ReturnsIconWithValidHandle theory above covers " +
                  "the Win32 HICON contract; the unit-test gap is in the " +
                  "H.NotifyIcon→shell handshake which only fires at app launch.")]
    public void TrayIconService_FullLifecycle_NoCrash_E2EPlaceholder()
    {
        // Future test outline (when STA-capable infra lands):
        //   1. Construct H.NotifyIcon.TaskbarIcon on an STA thread
        //   2. Assign Icon = TierColorPalette.IconForTier_Win(1) — must not throw
        //   3. Assign Icon = TierColorPalette.IconForTier_Win(6) — must not throw
        //   4. Assign Icon = TierColorPalette.IconForTier_Win(9) — must not throw
        //   5. Dispose TaskbarIcon — must not throw
        //   6. Call TierColorPalette.DisposeWinIconCache() — must not throw
        //   7. Assert no DispatcherUnhandledException fired in any step
        //
        // This is the test that would have caught the original two crashes
        // (RenderTargetBitmap NotImplementedException, BitmapImage URL NRE)
        // before they reached production. It cannot run today because xUnit
        // doesn't default to STA and H.NotifyIcon.TaskbarIcon requires WPF
        // dispatcher initialisation. The PR description carries the
        // commitment to fix this infrastructure gap before next sprint.
    }

    [Fact]
    public void IconForTier_ReturnsSameInstancePerBand()
    {
        // Two tiers in the same band must return the same cached bitmap —
        // tray re-render every state tick would allocate a 32×32 RTBitmap
        // otherwise, which is wasteful.
        Assert.Same(TierColorPalette.IconForTier(1),  TierColorPalette.IconForTier(5));
        Assert.Same(TierColorPalette.IconForTier(6),  TierColorPalette.IconForTier(7));
        Assert.Same(TierColorPalette.IconForTier(8),  TierColorPalette.IconForTier(10));
    }

    [Fact]
    public void IconForTier_DifferentBandsReturnDifferentInstances()
    {
        // Sanity: bands must be visually distinct cache entries.
        var grey  = TierColorPalette.IconForTier(1);
        var amber = TierColorPalette.IconForTier(6);
        var red   = TierColorPalette.IconForTier(10);
        Assert.NotSame(grey, amber);
        Assert.NotSame(amber, red);
        Assert.NotSame(grey, red);
    }
}
