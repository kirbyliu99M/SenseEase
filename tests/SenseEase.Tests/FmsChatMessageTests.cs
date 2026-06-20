using System.Linq;
using System.Windows;
using System.Windows.Media;
using SenseEase.App.ViewModels;
using SenseEase.Core.Models;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// FMS owner-migration (2026-06-19): the in-chat FMS rating message + its DETERMINISTIC capture (the pick
/// fires the host callback directly — it never routes through the LLM) and the calm colour ramp.
/// </summary>
public class FmsChatMessageTests
{
    [Fact]
    public void FmsMessage_BuildsSevenButtons_WithValuesAndTints_AndStartsVisible()
    {
        int picked = -1;
        var m = new ChatMessageViewModel("How are you?", "tap one", new[] { 2, 3, 4, 5, 6, 7, 8 },
            v => picked = v, "noted");

        Assert.NotNull(m.FmsButtons);
        Assert.Equal(7, m.FmsButtons!.Count);
        Assert.Equal(new[] { 2, 3, 4, 5, 6, 7, 8 }, m.FmsButtons.Select(b => b.Value).ToArray());
        Assert.All(m.FmsButtons, b => Assert.NotNull(b.IdleTint));
        Assert.NotNull(m.FmsTrackBrush);
        Assert.Equal(Visibility.Visible, m.FmsVisibility);
        Assert.Equal(Visibility.Collapsed, m.FmsConfirmVisibility);
        Assert.Equal("noted", m.FmsConfirmText);
    }

    [Fact]
    public void SelectingAValue_CapturesItOnce_AndCollapsesToConfirmation()
    {
        int calls = 0, picked = -1;
        var m = new ChatMessageViewModel("q", "h", new[] { 5, 6, 7, 8, 9, 10, 11 },
            v => { calls++; picked = v; }, "ok");

        m.SelectFmsCommand.Execute(8);

        Assert.Equal(1, calls);                                    // deterministic single capture
        Assert.Equal(8, picked);
        Assert.Equal(Visibility.Collapsed, m.FmsVisibility);       // row hidden after answering
        Assert.Equal(Visibility.Visible, m.FmsConfirmVisibility);

        m.SelectFmsCommand.Execute(11);                            // one rating per check-in
        Assert.Equal(1, calls);
    }

    [Fact]
    public void SelectFms_AcceptsBoxedIntOrStringParameter()
    {
        int picked = -1;
        var m = new ChatMessageViewModel("q", "h", new[] { 0, 1, 2, 3, 4, 5, 6 }, v => picked = v, "ok");
        m.SelectFmsCommand.Execute("3");                           // XAML may box the bound value as text
        Assert.Equal(3, picked);
    }

    [Fact]
    public void FmsScale_RampIsCalmAtLow_WarmButNeverStarkRedAtHigh()
    {
        Color low = FmsScale.ColorFor(0);
        Color high = FmsScale.ColorFor(20);
        Assert.NotEqual(low, high);
        Assert.True(high.R > high.B, "high end should read warm");
        Assert.True(high.G > 80, "high end stays a muted clay, not a stark alarming red");
        Assert.True(low.G >= low.R, "low end is calm/cool (green channel not dominated by red)");
    }

    [Fact]
    public void FollowupCard_HidesAfterSelection_AndCapturesOnce()
    {
        // UVHS fix (2026-06-20): the symptom-picker cards must disappear once the user taps one.
        int calls = 0; string? picked = null;
        var m = new ChatMessageViewModel("Which symptom?", ChatRole.Assistant,
            followupOptions: new[] { "Dizziness", "Nausea", "Headache", "Eye strain" },
            onFollowupSelected: s => { calls++; picked = s; });

        Assert.Equal(Visibility.Visible, m.FollowupVisibility);
        m.SelectFollowupCommand.Execute("Headache");
        Assert.Equal(1, calls);
        Assert.Equal("Headache", picked);
        Assert.Equal(Visibility.Collapsed, m.FollowupVisibility);   // cards gone after the tap
        m.SelectFollowupCommand.Execute("Nausea");                  // a second tap is a no-op
        Assert.Equal(1, calls);
    }

    // ── W-F2 (2026-06-21): ‹ › window arrows replace the old "Worse →" chip; ‹ undoes an accidental › ──

    private static ChatMessageViewModel FmsRow(int[] window)
        => new("How are you?", "tap one", window, _ => { }, "noted",
               worseWindow:  w => SenseEase.App.Services.FmsCheckInService.WindowAbove(w[0]),
               betterWindow: w => SenseEase.App.Services.FmsCheckInService.WindowBelow(w[0]));

    [Fact]
    public void FmsArrows_StepUpThenBack_RestoreExactPriorWindows_AndGateAvailability()
    {
        var m = FmsRow(new[] { 0, 1, 2, 3, 4, 5, 6 });

        Assert.True(m.FmsCanGoWorse);   // a higher window (7-13) exists
        Assert.False(m.FmsCanGoBack);   // nothing to undo yet

        m.FmsWorseCommand.Execute(null);                                            // › → 7-13
        Assert.Equal(new[] { 7, 8, 9, 10, 11, 12, 13 }, m.FmsButtons!.Select(b => b.Value).ToArray());
        Assert.True(m.FmsCanGoWorse);   // 14-20 still above
        Assert.True(m.FmsCanGoBack);    // can undo

        m.FmsWorseCommand.Execute(null);                                            // › → 14-20 (top)
        Assert.Equal(new[] { 14, 15, 16, 17, 18, 19, 20 }, m.FmsButtons!.Select(b => b.Value).ToArray());
        Assert.False(m.FmsCanGoWorse);  // greyed at the top of the scale
        Assert.True(m.FmsCanGoBack);

        m.FmsBackCommand.Execute(null);                                             // ‹ → back to 7-13 exactly
        Assert.Equal(new[] { 7, 8, 9, 10, 11, 12, 13 }, m.FmsButtons!.Select(b => b.Value).ToArray());
        Assert.True(m.FmsCanGoWorse);

        m.FmsBackCommand.Execute(null);                                             // ‹ → back to 0-6 exactly
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, m.FmsButtons!.Select(b => b.Value).ToArray());
        Assert.False(m.FmsCanGoBack);   // no more history
    }

    [Fact]
    public void FmsBack_AtBottomWindow_IsNoOp()
    {
        var m = FmsRow(new[] { 0, 1, 2, 3, 4, 5, 6 });
        m.FmsBackCommand.Execute(null);                                             // already at the lowest numbers
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, m.FmsButtons!.Select(b => b.Value).ToArray());
        Assert.False(m.FmsCanGoBack);
    }

    [Fact]
    public void FmsArrows_FromMiddleWindow_BothLive_AndBackPagesDownImmediately()
    {
        var m = FmsRow(new[] { 7, 8, 9, 10, 11, 12, 13 });   // e.g. a prior mid/high rating opened here
        Assert.True(m.FmsCanGoWorse);   // 14-20 above
        Assert.True(m.FmsCanGoBack);    // 0-6 below — live WITHOUT needing › first
        m.FmsBackCommand.Execute(null);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, m.FmsButtons!.Select(b => b.Value).ToArray());
    }

    [Fact]
    public void FmsArrows_AtTopWindow_BackStillLive()
    {
        // Regression for the reported "can't press either arrow": a check-in that opens at the top window
        // used to leave BOTH arrows disabled (nothing higher + no undo history). ‹ must page down.
        var m = FmsRow(new[] { 14, 15, 16, 17, 18, 19, 20 });
        Assert.False(m.FmsCanGoWorse);  // nothing higher
        Assert.True(m.FmsCanGoBack);    // ‹ pages down to 7-13 — never stuck
    }

    [Fact]
    public void FmsArrows_DisabledAfterAnswering()
    {
        var m = FmsRow(new[] { 0, 1, 2, 3, 4, 5, 6 });
        m.SelectFmsCommand.Execute(3);                                              // answered
        Assert.False(m.FmsCanGoWorse);
        Assert.False(m.FmsCanGoBack);
        m.FmsWorseCommand.Execute(null);                                            // a stray tap is inert
        Assert.Equal(Visibility.Collapsed, m.FmsVisibility);
    }
}
