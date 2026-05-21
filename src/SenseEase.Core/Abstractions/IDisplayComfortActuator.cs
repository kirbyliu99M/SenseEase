using SenseEase.Core.Models;

namespace SenseEase.Core.Abstractions;

/// <summary>
/// Hardware actuator for monitor Contrast/Brightness/Sharpness via DDC/CI.
/// All Win32 is isolated in the Infrastructure implementation. Disposing the
/// actuator restores the captured baseline — the display must never be left altered.
/// </summary>
public interface IDisplayComfortActuator : IDisposable
{
    /// <summary>Which features the active monitor supports. None until Initialize() runs.</summary>
    DisplayCapability Capability { get; }

    /// <summary>True once Initialize() has successfully probed and captured a baseline.</summary>
    bool IsInitialized { get; }

    /// <summary>The captured original VCP values (valid after a successful Initialize()).</summary>
    DisplayBaseline Baseline { get; }

    /// <summary>Signature of the probed monitor; used for baseline-recovery matching.</summary>
    string MonitorSignature { get; }

    /// <summary>
    /// Probe the active monitor and capture its original VCP values. If
    /// <paramref name="priorBaseline"/> is supplied (recovery from a prior force-kill),
    /// those values are written to the monitor BEFORE probing, so the captured
    /// originals are the true user originals rather than the comfort-adjusted state.
    /// Returns true if at least one feature is controllable.
    /// </summary>
    bool Initialize(DisplayBaseline? priorBaseline = null);

    /// <summary>
    /// Apply target values, each a normalized 0–100 percent. The implementation
    /// quantises each onto that feature's native VCP range and skips unsupported
    /// features and no-op writes.
    /// </summary>
    void Apply(int contrastPercent, int brightnessPercent, int sharpnessPercent);

    /// <summary>Restore the captured baseline. Idempotent and safe to call repeatedly.</summary>
    void RestoreBaseline();
}
