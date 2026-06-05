using System;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Documents;
using SenseEase.App.Views;
using Xunit;

namespace SenseEase.Tests;

/// <summary>NE26: the home-grown Markdown renderer must not treat underscores
/// inside identifiers as italic emphasis. Chatbot replies routinely mention
/// personal_pressure_scale, severe_strike_limit, expires_at_iso, [OBSERVATION_ADD]
/// — italicising between the underscores swallowed them and made the identifiers
/// unreadable. `_` emphasis now requires word boundaries on BOTH delimiters;
/// `*italic*` (which identifiers never use) is unaffected.</summary>
public class MarkdownInlinesTests
{
    [Theory]
    [InlineData("set personal_pressure_scale now")]
    [InlineData("severe_strike_limit and expires_at_iso today")]
    [InlineData("emit the [OBSERVATION_ADD] token")]
    public void IntrawordUnderscores_AreNotItalicised(string text)
    {
        RunSta(() =>
        {
            var tb = new TextBlock();
            MarkdownInlines.SetSource(tb, text);
            Assert.DoesNotContain(tb.Inlines, i => i is Italic);
            Assert.Equal(text, Flatten(tb)); // underscores survive verbatim
        });
    }

    [Theory]
    [InlineData("this is _important_ ok")]   // underscore at word boundaries → italic
    [InlineData("this is *important* ok")]   // asterisk italic still works
    public void RealEmphasis_StillItalicised(string text)
    {
        RunSta(() =>
        {
            var tb = new TextBlock();
            MarkdownInlines.SetSource(tb, text);
            Assert.Contains(tb.Inlines, i => i is Italic);
        });
    }

    [Fact]
    public void Bold_StillWorks()
    {
        RunSta(() =>
        {
            var tb = new TextBlock();
            MarkdownInlines.SetSource(tb, "this is **strong** ok");
            Assert.Contains(tb.Inlines, i => i is Bold);
        });
    }

    private static string Flatten(TextBlock tb)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var inl in tb.Inlines) Append(inl, sb);
        return sb.ToString();
    }

    private static void Append(Inline inl, System.Text.StringBuilder sb)
    {
        switch (inl)
        {
            case Run r:      sb.Append(r.Text); break;
            case Span s:     foreach (var c in s.Inlines) Append(c, sb); break;
            case LineBreak:  sb.Append('\n'); break;
        }
    }

    private static void RunSta(Action body)
    {
        Exception? captured = null;
        var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try { body(); } catch (Exception ex) { captured = ex; } finally { done.Set(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(15)), "STA test timed out");
        if (captured != null) throw new Xunit.Sdk.XunitException("STA test threw: " + captured);
    }
}
