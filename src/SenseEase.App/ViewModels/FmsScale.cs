using System;
using System.Windows.Media;

namespace SenseEase.App.ViewModels;

/// <summary>
/// The calm "comfort spectrum" colour ramp for the in-chat FMS rating box (chatbot-tuning sprint
/// 2026-06-19). Encodes an FMS value 0–20 as a MUTED calm→warm tone — sage-teal (ease) → ochre →
/// clay (strain) — deliberately NOT a saturated red, per SenseEase's calm cardinal rule. The squares
/// carry a low-alpha tint of this colour so the row reads as a gentle gradient without ever alarming.
/// Brushes are frozen (software-render friendly; no effects / no BitmapCache anywhere in this surface).
/// </summary>
public static class FmsScale
{
    public const int Min = 0;
    public const int Max = 20;

    // 3-stop ramp. Calm and muted on purpose; the high end is a warm clay, not red.
    private static readonly Color Ease   = Color.FromRgb(0x6F, 0xA8, 0xA0); // sage-teal
    private static readonly Color Rising = Color.FromRgb(0xD6, 0xB2, 0x5E); // ochre
    private static readonly Color Strain = Color.FromRgb(0xC7, 0x7B, 0x5B); // clay

    /// <summary>Sample the ramp at <paramref name="t"/> ∈ [0,1] (0 = ease, 1 = strain).</summary>
    public static Color Sample(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t < 0.5 ? Lerp(Ease, Rising, t / 0.5) : Lerp(Rising, Strain, (t - 0.5) / 0.5);
    }

    /// <summary>The ramp colour for an FMS value 0–20.</summary>
    public static Color ColorFor(int fms) => Sample((Math.Clamp(fms, Min, Max) - Min) / (double)(Max - Min));

    /// <summary>A low-alpha tinted fill for an idle square (subtle calm gradient across the row).</summary>
    public static Brush IdleTint(int fms) => Frozen(WithAlpha(ColorFor(fms), 0x26));   // ~15%

    /// <summary>A continuous calm→warm track brush spanning the window's first→last value — the
    /// "comfort spectrum" signature line beneath the row.</summary>
    public static Brush Track(int firstValue, int lastValue)
    {
        var b = new LinearGradientBrush(ColorFor(firstValue), ColorFor(lastValue), 0.0);
        b.Freeze();
        return b;
    }

    private static Color Lerp(Color a, Color b, double u) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * u),
        (byte)Math.Round(a.G + (b.G - a.G) * u),
        (byte)Math.Round(a.B + (b.B - a.B) * u));

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>One square in the FMS rating row: the FMS value it represents + its idle tint. The tap is
/// driven by the parent <c>ChatMessageViewModel.SelectFmsCommand</c> (one selection per check-in).</summary>
public sealed class FmsButtonVm
{
    public int Value { get; }
    public Brush IdleTint { get; }
    public FmsButtonVm(int value)
    {
        Value = value;
        IdleTint = FmsScale.IdleTint(value);
    }
}
