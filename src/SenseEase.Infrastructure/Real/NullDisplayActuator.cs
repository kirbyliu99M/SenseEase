using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;

namespace SenseEase.Infrastructure.Real;

/// <summary>
/// No-op actuator for displays without DDC/CI support, or when the feature is off.
/// Lets the controller run unconditionally — "unsupported" is a real object surfaced
/// in the UI, never a silent failure or a null reference.
/// </summary>
public sealed class NullDisplayActuator : IDisplayComfortActuator
{
    public DisplayCapability Capability => DisplayCapability.None;
    public bool IsInitialized => false;
    public DisplayBaseline Baseline => new();
    public string MonitorSignature => string.Empty;
    public bool Initialize(DisplayBaseline? priorBaseline = null) => false;
    public void Apply(int contrastPercent, int brightnessPercent, int sharpnessPercent) { }
    public void RestoreBaseline() { }
    public void Dispose() { }
}
