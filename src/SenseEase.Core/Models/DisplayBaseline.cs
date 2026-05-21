namespace SenseEase.Core.Models;

/// <summary>
/// The monitor's ORIGINAL VCP values, captured at startup, so the app can restore
/// them on exit. Per the product requirement, the app's adjustments are temporary —
/// the display must return to this baseline whenever the app closes.
/// </summary>
public sealed class DisplayBaseline
{
    public uint OriginalContrast   { get; init; }
    public uint OriginalBrightness { get; init; }
    public uint OriginalSharpness  { get; init; }

    public bool HasContrast   { get; init; }
    public bool HasBrightness { get; init; }
    public bool HasSharpness  { get; init; }
}
