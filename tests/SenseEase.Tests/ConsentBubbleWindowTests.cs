using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using SenseEase.App.Views;
using Xunit;

namespace SenseEase.Tests;

/// <summary>One-tap consent (Model-2, Kirby 2026-06-25): when <see cref="ConsentBubbleWindow.OneTapMode"/>
/// is ON, a tap on the collapsed avatar GRANTS directly (the bubble is the sole one-tap confirm); when OFF
/// the avatar EXPANDS the card as before. WPF window construction needs an STA thread + applied template,
/// so each test spins up its own STA thread (pattern mirrors <c>NotificationCardWindowTests</c>).</summary>
[Collection("TestDataRoot")]
public class ConsentBubbleWindowTests
{
    [Fact]
    public void OneTapMode_AvatarClick_GrantsDirectly_WithoutExpandingTheCard()
    {
        RunOnStaBubble(w =>
        {
            w.OneTapMode = true;
            int granted = 0, declined = 0;
            w.ConsentAccepted += (_, _) => granted++;
            w.ConsentDeclined += (_, _) => declined++;

            w.AvatarBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal(1, granted);                                         // one tap = grant
            Assert.Equal(0, declined);
            Assert.NotEqual(Visibility.Visible, w.MessageBubble.Visibility);  // the card never expanded
        });
    }

    [Fact]
    public void DefaultMode_AvatarClick_ExpandsTheCard_DoesNotGrant()
    {
        RunOnStaBubble(w =>
        {
            w.OneTapMode = false;
            int granted = 0;
            w.ConsentAccepted += (_, _) => granted++;

            w.AvatarBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal(0, granted);                                         // a tap expands, does NOT grant
            Assert.Equal(Visibility.Visible, w.MessageBubble.Visibility);     // the card is now showing
        });
    }

    private static void RunOnStaBubble(Action<ConsentBubbleWindow> body)
    {
        Exception? captured = null;
        var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                var w = new ConsentBubbleWindow();
                // Force template instantiation so the named parts (AvatarBtn, MessageBubble) are non-null.
                w.ApplyTemplate();
                try { body(w); }
                finally { try { w.Close(); } catch { /* best-effort cleanup */ } }
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
