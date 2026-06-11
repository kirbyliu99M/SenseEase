using System;
using System.Threading;
using SenseEase.App.Views;
using SenseEase.Core.Models;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// ABCD test fix (2026-06-12): one of the four alpha-test machines lost the overlay behind a
/// topmost-claiming foreign app permanently — WPF asserts Topmost only at Show, and the
/// overlay (unlike PersistentBarWindow since Path Z iter 4) had no Win32 recovery. These pin
/// the new self-healing: <c>ReassertTopmost</c> is a safe no-op before the HWND exists, never
/// fires while hidden, and fires on the ~1/s engine-tick cadence while visible.
/// </summary>
public class OverlayTopmostReassertTests
{
    private static SenseState CalmState() => new()
    {
        VimsTier                 = 3,
        MaskActive               = true,
        PersistentBaselineSteady = true,   // quiet pipeline — no cross-fade churn
        HeadTrackingActive       = false,
        Shader                   = new ShaderParams(),
    };

    [Fact]
    public void ReassertTopmost_BeforeHandleExists_IsSafeNoOp()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();
            w.ReassertTopmost();                       // no HWND yet — must not throw
            Assert.Equal(0, w.TopmostReassertsForTests); // and must not count as issued
        });
    }

    [Fact]
    public void ApplyState_WhileHidden_NeverIssuesReasserts()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            w.ApplyTemplate();
            for (int i = 0; i < 40; i++)
                w.ApplyState(CalmState());
            Assert.Equal(0, w.TopmostReassertsForTests);
        });
    }

    [Fact]
    public void ApplyState_WhileVisible_IssuesPeriodicReasserts()
    {
        RunOnSta(() =>
        {
            var w = new OverlayWindow();
            try
            {
                // Show() creates the HWND synchronously; the window stays invisible on a
                // pumpless STA thread (Opacity starts at 0, the fade never advances).
                w.Show();
                long shown = w.TopmostReassertsForTests; // Show itself issues none (MainWindow does)

                // Cadence = every 16th engine tick: 33 ticks ⇒ ticks 0, 16, 32 fire.
                for (int i = 0; i < 33; i++)
                    w.ApplyState(CalmState());

                Assert.Equal(shown + 3, w.TopmostReassertsForTests);
            }
            finally
            {
                w.Close();
            }
        });
    }

    // ── STA harness (copied from OverlayPersistentBaselineSteadyTests) ────────
    private static void RunOnSta(Action body)
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
