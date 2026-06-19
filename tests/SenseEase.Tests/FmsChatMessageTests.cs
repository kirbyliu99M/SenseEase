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
}
