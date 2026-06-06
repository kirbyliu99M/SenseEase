using System;
using System.Threading;
using System.Windows.Media;
using SenseEase.App.Views;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Pins the native 12-level → 5級 動態感 colour mapping for the persistent
/// ambient bar (Adaptive Fit Q2=B; was the legacy Tier 1–5 grey / 6–7 amber / 8–10 red
/// 3-band). Verifies the §1.1 ranges (L1–2 極高, L3–4 高, L5–7 中, L8–10 低, L11–12 極低)
/// against the calm-cool ramp (§13.6) and clamping at edges. The bar's positioning +
/// show path is not unit-testable without a WPF dispatcher harness (manual acceptance).</summary>
public class PersistentBarWindowTests
{
    // (A, R, G, B) for the calm-cool 5級 ramp (plugin-grounded §13.6) — direct
    // comparison against Color.FromArgb. No alarm red; deeper blue = more stabilized.
    private const byte VeryHighA = 0x26, VeryHighR = 0x9B, VeryHighG = 0xB4, VeryHighB = 0xD4; // 極高 #9BB4D4
    private const byte HighA     = 0x38, HighR     = 0x6F, HighG     = 0x93, HighB     = 0xC2; // 高   #6F93C2
    private const byte MediumA   = 0x4D, MediumR   = 0x4A, MediumG   = 0x77, MediumB   = 0xB5; // 中   #4A77B5
    private const byte LowA      = 0x61, LowR      = 0x34, LowG      = 0x5E, LowB      = 0x97; // 低   #345E97
    private const byte VeryLowA  = 0x73, VeryLowR  = 0x24, VeryLowG  = 0x3F, VeryLowB  = 0x6B; // 極低 #243F6B

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Level1And2_ReturnsVeryHigh(int tier)
        => AssertColor(PersistentBarWindow.BrushForTier(tier), VeryHighA, VeryHighR, VeryHighG, VeryHighB);

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Level3And4_ReturnsHigh(int tier)
        => AssertColor(PersistentBarWindow.BrushForTier(tier), HighA, HighR, HighG, HighB);

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Level5To7_ReturnsMedium(int tier)
        => AssertColor(PersistentBarWindow.BrushForTier(tier), MediumA, MediumR, MediumG, MediumB);

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void Level8To10_ReturnsLow(int tier)
        => AssertColor(PersistentBarWindow.BrushForTier(tier), LowA, LowR, LowG, LowB);

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    public void Level11And12_ReturnsVeryLow(int tier)
        => AssertColor(PersistentBarWindow.BrushForTier(tier), VeryLowA, VeryLowR, VeryLowG, VeryLowB);

    [Theory]
    [InlineData(0)]   // engine should never emit this, but defensive
    [InlineData(-5)]
    public void LevelAtOrBelowZero_ClampsToVeryHigh(int tier)
        => AssertColor(PersistentBarWindow.BrushForTier(tier), VeryHighA, VeryHighR, VeryHighG, VeryHighB);

    [Theory]
    [InlineData(13)]  // defensive — clamps to the nearest 級 (極低)
    [InlineData(99)]
    public void LevelAbove12_ClampsToVeryLow(int tier)
        => AssertColor(PersistentBarWindow.BrushForTier(tier), VeryLowA, VeryLowR, VeryLowG, VeryLowB);

    [Fact]
    public void BrushesAreFrozen_ShareableAcrossThreads()
    {
        // Brushes are returned from the static cache; they must be Frozen so
        // they can be assigned on any thread without WPF freezing complaints.
        Assert.True(PersistentBarWindow.BrushForTier(1).IsFrozen);   // 極高
        Assert.True(PersistentBarWindow.BrushForTier(6).IsFrozen);   // 中
        Assert.True(PersistentBarWindow.BrushForTier(10).IsFrozen);  // 低
    }

    private static void AssertColor(SolidColorBrush brush, byte a, byte r, byte g, byte b)
    {
        Assert.Equal(a, brush.Color.A);
        Assert.Equal(r, brush.Color.R);
        Assert.Equal(g, brush.Color.G);
        Assert.Equal(b, brush.Color.B);
    }

    /// <summary>Test 6 (overlay-bar-redesign D3): EnterSleepMode swaps the
    /// bar's background brush; ExitSleepMode restores the exact pre-sleep
    /// instance. Verifies the snapshot-and-restore behaviour the host
    /// relies on when the user toggles Power Save off.</summary>
    [Fact]
    public void PersistentBarWindow_EnterExitSleepMode_RestoresBrush()
    {
        Exception? captured = null;
        var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                var w = new PersistentBarWindow();
                w.ApplyTemplate();
                // Force a known tier so the snapshot brush is deterministic.
                w.SetTier(1);
                Brush original = w.BarFill.Background;

                w.EnterSleepMode();
                Assert.NotSame(original, w.BarFill.Background);
                Assert.Equal(System.Windows.Visibility.Visible, w.SleepOverlay.Visibility);

                w.ExitSleepMode();
                Assert.Same(original, w.BarFill.Background);
                Assert.Equal(System.Windows.Visibility.Collapsed, w.SleepOverlay.Visibility);
            }
            catch (Exception ex) { captured = ex; }
            finally { done.Set(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(15)), "STA test timed out");
        if (captured != null)
            throw new Xunit.Sdk.XunitException("STA test threw: " + captured);
    }

    /// <summary>D3 liveness contract §5.1 — THE anti-freeze test. A faulted bar
    /// must NOT equal a healthy bar on the bound surface; that non-identity is
    /// the entire point of the contract (a frozen healthy bar and a faulted bar
    /// were previously pixel-identical). EnterFaultedMode swaps BarFill to the
    /// clay FaultedBarBrush + shows the solid FaultOutline; ExitFaultedMode
    /// restores the exact pre-fault instance.</summary>
    [Fact]
    public void PersistentBarWindow_EnterFaultedMode_IsDistinctFromHealthy_AndRestores()
    {
        RunSta(() =>
        {
            var w = new PersistentBarWindow();
            w.ApplyTemplate();
            // NB: deliberately NOT calling SetTier — it reads the shared
            // UserPreferences file and races UserPreferencesTests under xUnit
            // parallelism. The XAML-default fill is a fine "healthy" baseline.
            Brush healthy = w.BarFill.Background;

            w.EnterFaultedMode();
            Assert.NotSame(healthy, w.BarFill.Background);                       // fault ≠ healthy
            Assert.Same(TierColorPalette.FaultedBarBrush, w.BarFill.Background); // the clay token
            Assert.Equal(System.Windows.Visibility.Visible, w.FaultOutline.Visibility);

            w.ExitFaultedMode();
            Assert.Same(healthy, w.BarFill.Background);
            Assert.Equal(System.Windows.Visibility.Collapsed, w.FaultOutline.Visibility);
        });
    }

    /// <summary>D3 liveness contract §5.2 — Paused ≠ Faulted. Calm-by-choice
    /// (dashed SleepOverlay) and calm-by-accident (solid FaultOutline + clay
    /// fill) must produce distinct, non-overlapping visual states so the two can
    /// never be confused.</summary>
    [Fact]
    public void PersistentBarWindow_PausedAndFaulted_AreDistinctTokens()
    {
        RunSta(() =>
        {
            // No SetTier (avoids the shared-UserPreferences race); EnterSleepMode
            // and EnterFaultedMode both apply their own fills regardless of tier.
            var paused = new PersistentBarWindow();
            paused.ApplyTemplate();
            paused.EnterSleepMode();

            var faulted = new PersistentBarWindow();
            faulted.ApplyTemplate();
            faulted.EnterFaultedMode();

            // Outline tokens are mutually exclusive: dashed for paused, solid for faulted.
            Assert.Equal(System.Windows.Visibility.Visible,   paused.SleepOverlay.Visibility);
            Assert.Equal(System.Windows.Visibility.Collapsed, paused.FaultOutline.Visibility);
            Assert.Equal(System.Windows.Visibility.Collapsed, faulted.SleepOverlay.Visibility);
            Assert.Equal(System.Windows.Visibility.Visible,   faulted.FaultOutline.Visibility);

            // And the fills differ: faint grey (paused) vs clay (faulted).
            var pausedFill  = (SolidColorBrush)paused.BarFill.Background;
            var faultedFill = (SolidColorBrush)faulted.BarFill.Background;
            Assert.NotEqual(pausedFill.Color, faultedFill.Color);
        });
    }

    private static void RunSta(Action body)
    {
        Exception? captured = null;
        var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { captured = ex; }
            finally { done.Set(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(15)), "STA test timed out");
        if (captured != null)
            throw new Xunit.Sdk.XunitException("STA test threw: " + captured);
    }
}
