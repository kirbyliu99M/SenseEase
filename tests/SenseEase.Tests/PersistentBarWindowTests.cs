using System;
using System.Threading;
using System.Windows.Media;
using SenseEase.App.Views;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Pop-up sprint Task 2 — pins the 3-tier colour mapping for the
/// persistent ambient bar. Verifies the user-decided ranges (Tier 1–5 grey,
/// 6–7 amber, 8–10 red) and clamping at edges. The bar's positioning + show
/// path is not unit-testable without WPF dispatcher harness (covered by
/// manual acceptance in Task 7).</summary>
public class PersistentBarWindowTests
{
    // RGBA target values from the spec (User decisions 2026-05-26).
    // Stored as (A, R, G, B) for direct comparison against Color.FromArgb.
    private const byte GreyA  = 0x26;  // 0.15 alpha × 255
    private const byte GreyR  = 0x66, GreyG  = 0x66, GreyB  = 0x66;
    private const byte AmberA = 0x4D;  // 0.30 alpha × 255
    private const byte AmberR = 0xE6, AmberG = 0x8C, AmberB = 0x28;
    private const byte RedA   = 0x73;  // 0.45 alpha × 255
    private const byte RedR   = 0xDC, RedG   = 0x32, RedB   = 0x28;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Tier1To5_ReturnsGrey(int tier)
    {
        AssertColor(PersistentBarWindow.BrushForTier(tier), GreyA, GreyR, GreyG, GreyB);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    public void Tier6And7_ReturnsAmber(int tier)
    {
        AssertColor(PersistentBarWindow.BrushForTier(tier), AmberA, AmberR, AmberG, AmberB);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void Tier8To10_ReturnsRed(int tier)
    {
        AssertColor(PersistentBarWindow.BrushForTier(tier), RedA, RedR, RedG, RedB);
    }

    [Theory]
    [InlineData(0)]   // engine should never emit this, but defensive
    [InlineData(-5)]
    public void TierAtOrBelowZero_ClampsToGrey(int tier)
    {
        AssertColor(PersistentBarWindow.BrushForTier(tier), GreyA, GreyR, GreyG, GreyB);
    }

    [Theory]
    [InlineData(11)]  // defensive — clamps to nearest band (red)
    [InlineData(99)]
    public void TierAboveTen_ClampsToRed(int tier)
    {
        AssertColor(PersistentBarWindow.BrushForTier(tier), RedA, RedR, RedG, RedB);
    }

    [Fact]
    public void BrushesAreFrozen_ShareableAcrossThreads()
    {
        // Brushes are returned from the static cache; they must be Frozen so
        // they can be assigned on any thread without WPF freezing complaints.
        Assert.True(PersistentBarWindow.BrushForTier(1).IsFrozen);
        Assert.True(PersistentBarWindow.BrushForTier(6).IsFrozen);
        Assert.True(PersistentBarWindow.BrushForTier(10).IsFrozen);
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
}
