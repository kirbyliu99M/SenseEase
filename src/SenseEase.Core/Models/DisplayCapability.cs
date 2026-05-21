namespace SenseEase.Core.Models;

/// <summary>
/// Which DDC/CI VCP features the active monitor actually supports, and each
/// feature's VCP maximum (the device's native scale — often 100, but sharpness
/// is sometimes a small range like 0–4).
/// </summary>
public sealed class DisplayCapability
{
    public bool ContrastSupported   { get; init; }
    public bool BrightnessSupported { get; init; }
    public bool SharpnessSupported  { get; init; }

    public uint ContrastMax   { get; init; }
    public uint BrightnessMax { get; init; }
    public uint SharpnessMax  { get; init; }

    public bool AnySupported => ContrastSupported || BrightnessSupported || SharpnessSupported;

    /// <summary>An all-false capability — the graceful "unsupported display" state.</summary>
    public static DisplayCapability None => new();
}
