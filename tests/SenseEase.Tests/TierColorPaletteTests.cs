using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using SenseEase.App.Views;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Pop-up sprint Task 3 — tests for the single source of truth on
/// tier → colour mapping. Bar and tray both consume this; tests pin the
/// invariants so a future "I'll just tweak the 中 級 hue a bit" edit can't
/// silently desync the two surfaces.</summary>
public class TierColorPaletteTests
{
    // ── Band lookup ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0,   TierColorPalette.TierBand.VeryHigh)] // defensive clamp ≤ 0
    [InlineData(1,   TierColorPalette.TierBand.VeryHigh)]
    [InlineData(2,   TierColorPalette.TierBand.VeryHigh)]
    [InlineData(3,   TierColorPalette.TierBand.High)]
    [InlineData(4,   TierColorPalette.TierBand.High)]
    [InlineData(5,   TierColorPalette.TierBand.Medium)]
    [InlineData(6,   TierColorPalette.TierBand.Medium)]
    [InlineData(7,   TierColorPalette.TierBand.Medium)]
    [InlineData(8,   TierColorPalette.TierBand.Low)]
    [InlineData(9,   TierColorPalette.TierBand.Low)]
    [InlineData(10,  TierColorPalette.TierBand.Low)]
    [InlineData(11,  TierColorPalette.TierBand.VeryLow)]
    [InlineData(12,  TierColorPalette.TierBand.VeryLow)]
    [InlineData(999, TierColorPalette.TierBand.VeryLow)] // defensive clamp ≥ 13
    public void BandForTier_ReturnsExpectedBand(int tier, TierColorPalette.TierBand expected)
    {
        Assert.Equal(expected, TierColorPalette.BandForTier(tier));
    }

    // ── Brush (bar surface) ──────────────────────────────────────────────────

    [Theory]
    [InlineData(1,  0x26, 0x9B, 0xB4, 0xD4)] // VeryHigh (極高), alpha 0.15
    [InlineData(3,  0x38, 0x6F, 0x93, 0xC2)] // High     (高),   alpha 0.22
    [InlineData(5,  0x4D, 0x4A, 0x77, 0xB5)] // Medium   (中),   alpha 0.30
    [InlineData(8,  0x61, 0x34, 0x5E, 0x97)] // Low      (低),   alpha 0.38
    [InlineData(11, 0x73, 0x24, 0x3F, 0x6B)] // VeryLow  (極低),  alpha 0.45
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
        // Same 級 → same frozen instance (no per-call allocation on the hot path).
        var veryHigh1 = TierColorPalette.BrushForTier(1);
        var veryHigh2 = TierColorPalette.BrushForTier(2);
        Assert.Same(veryHigh1, veryHigh2);
        Assert.True(veryHigh1.IsFrozen);

        var medium5 = TierColorPalette.BrushForTier(5);
        var medium6 = TierColorPalette.BrushForTier(6);
        var medium7 = TierColorPalette.BrushForTier(7);
        Assert.Same(medium5, medium6);
        Assert.Same(medium6, medium7);

        var low8  = TierColorPalette.BrushForTier(8);
        var low9  = TierColorPalette.BrushForTier(9);
        var low10 = TierColorPalette.BrushForTier(10);
        Assert.Same(low8, low9);
        Assert.Same(low9, low10);
    }

    // ── Icon (tray surface) ──────────────────────────────────────────────────

    // ── Fix 3 contract tests REMOVED (Alpha-1 tray-ICO fix) ──────────────────
    //
    // The System.Drawing.Icon path on this class (IconForTier_Win) and its
    // single-32px-PNG-in-ICO wrapper were deleted: that single frame forced the
    // Win11 shell to down-scale 32 px → 16 px, producing the blurred / blank
    // tray icon. The tray now builds its own MULTI-SIZE ICO (16/24/32/48 px
    // uncompressed BGRA-DIB frames) in TrayIconService, covered by the new
    // TrayIconResourceTests (frame count > 1 + a 16×16 / 32-bit ICONDIRENTRY).
    // The five tests that pinned the deleted single-PNG path
    // (IconForTier_Win_ReturnsIconWithValidHandle / _ReturnsSameInstancePerBand /
    // _DifferentBandsReturnDifferentInstances / _ProducesProperIcoFormat and
    // RenderIconWin_HasValidIconDirectoryHeader) were removed with it.

    [Fact(Skip = "Manual STA smoke — instantiates real Hardcodet TaskbarIcon " +
                  "and exercises the tray's multi-size ICO across all 3 bands + Dispose. " +
                  "Runs on net8.0-windows but xUnit's default scheduler isn't STA. " +
                  "Will be promoted to [Fact] once the E2E WPF test infrastructure " +
                  "(FlaUI / StaFact / WpfApplicationFactory pattern) is scoped " +
                  "before Habituation Phase 6/7/8 sprint opens. Until then, the " +
                  "TrayIconResourceTests cover the ICO structure (multi-frame + 16×16); " +
                  "the unit-test gap is in the " +
                  "library→Shell_NotifyIcon registration handshake which only " +
                  "fires at app launch (the gap that hid Patch 5's silent reg failure).")]
    public void TrayIconService_FullLifecycle_NoCrash_E2EPlaceholder()
    {
        // Future test outline (when STA-capable infra lands):
        //   1. Construct Hardcodet.Wpf.TaskbarNotification.TaskbarIcon on
        //      an STA thread
        //   2. Assign Icon = TrayIconService's band icon (1) — must not throw
        //   3. Assign Icon = TrayIconService's band icon (6) — must not throw
        //   4. Assign Icon = TrayIconService's band icon (9) — must not throw
        //   5. Dispose TaskbarIcon — must not throw
        //   6. TrayIconService.Dispose() releases its per-band icon cache — must not throw
        //   7. Assert no DispatcherUnhandledException fired in any step
        //   8. Verify the icon was actually registered with the OS shell —
        //      this is the step that would have caught Patch 5's
        //      H.NotifyIcon silent registration failure (library reported
        //      success but Shell_NotifyIcon never received the call).
        //      Mechanism TBD by E2E infra (FlaUI can query shell state;
        //      reflection approach hit dead ends; Win32 P/Invoke into the
        //      shell tray rect lookup is the heaviest option).
        //
        // This is the test that would have caught the original two crashes
        // (RenderTargetBitmap NotImplementedException, BitmapImage URL NRE)
        // AND the silent registration failure that motivated Patch 5,
        // before they reached production. The PR description carries the
        // commitment to fix this infrastructure gap before next sprint.
    }

}
