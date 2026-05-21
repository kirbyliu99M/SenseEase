using System.Runtime.InteropServices;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;

namespace SenseEase.Infrastructure.Real;

/// <summary>
/// DDC/CI display actuator: controls monitor Contrast (VCP 0x12), Brightness (0x10)
/// and Sharpness (0x87) on the primary physical monitor via the dxva2 Monitor
/// Configuration API. Captures original values on Initialize() and restores them on
/// RestoreBaseline()/Dispose() — the display is never left altered after the app exits.
/// Hardware-dependent: many laptop internal panels do not implement DDC/CI; then
/// Initialize() returns false and the caller falls back to NullDisplayActuator.
/// Hand-written P/Invoke (mirrors CursorVelocityProvider's direct interop style).
/// </summary>
public sealed class Dxva2DisplayActuator : IDisplayComfortActuator
{
    private const byte VcpContrast   = 0x12;
    private const byte VcpBrightness = 0x10;
    private const byte VcpSharpness  = 0x87;
    private const uint MonitorDefaultToPrimary = 1;

    private readonly IntPtr _hwnd;
    private PHYSICAL_MONITOR[]? _monitors;
    private IntPtr _hMonitor;
    private DisplayBaseline _baseline = new();
    private bool _restored;
    private int _lastContrast = -1, _lastBrightness = -1, _lastSharpness = -1;

    public Dxva2DisplayActuator(IntPtr mainWindowHandle) => _hwnd = mainWindowHandle;

    public DisplayCapability Capability { get; private set; } = DisplayCapability.None;
    public bool IsInitialized { get; private set; }

    /// <summary>Signature of the probed monitor, for baseline-recovery matching.</summary>
    public string MonitorSignature { get; private set; } = string.Empty;

    /// <summary>The captured original VCP values (valid after a successful Initialize()).</summary>
    public DisplayBaseline Baseline => _baseline;

    public bool Initialize(DisplayBaseline? priorBaseline = null)
    {
        try
        {
            IntPtr hmon = MonitorFromWindow(_hwnd, MonitorDefaultToPrimary);
            if (hmon == IntPtr.Zero) return false;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out uint count) || count == 0)
                return false;

            var arr = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hmon, count, arr)) return false;
            _monitors = arr;
            _hMonitor = arr[0].hPhysicalMonitor;

            if (priorBaseline is not null)
            {
                try
                {
                    if (priorBaseline.HasContrast)   SetVCPFeature(_hMonitor, VcpContrast,   priorBaseline.OriginalContrast);
                    if (priorBaseline.HasBrightness) SetVCPFeature(_hMonitor, VcpBrightness, priorBaseline.OriginalBrightness);
                    if (priorBaseline.HasSharpness)  SetVCPFeature(_hMonitor, VcpSharpness,  priorBaseline.OriginalSharpness);
                }
                catch { /* best-effort recovery */ }
            }

            bool cOk = TryProbe(VcpContrast,   out uint cCur, out uint cMax);
            bool bOk = TryProbe(VcpBrightness, out uint bCur, out uint bMax);
            bool sOk = TryProbe(VcpSharpness,  out uint sCur, out uint sMax);

            Capability = new DisplayCapability
            {
                ContrastSupported   = cOk, ContrastMax   = cMax,
                BrightnessSupported = bOk, BrightnessMax = bMax,
                SharpnessSupported  = sOk, SharpnessMax  = sMax,
            };
            _baseline = new DisplayBaseline
            {
                HasContrast   = cOk, OriginalContrast   = cCur,
                HasBrightness = bOk, OriginalBrightness = bCur,
                HasSharpness  = sOk, OriginalSharpness  = sCur,
            };
            MonitorSignature = $"{arr[0].szPhysicalMonitorDescription}|{cMax}|{bMax}|{sMax}";

            IsInitialized = Capability.AnySupported;
            return IsInitialized;
        }
        catch { return false; }
    }

    private bool TryProbe(byte vcp, out uint current, out uint max)
    {
        current = 0; max = 0;
        try
        {
            if (!GetVCPFeatureAndVCPFeatureReply(_hMonitor, vcp, out _, out current, out max))
                return false;
            return max > 0;
        }
        catch { return false; }
    }

    public void Apply(int contrastPercent, int brightnessPercent, int sharpnessPercent)
    {
        if (!IsInitialized) return;
        _restored = false;
        if (Capability.ContrastSupported)
            WriteIfChanged(VcpContrast, contrastPercent, Capability.ContrastMax, ref _lastContrast);
        if (Capability.BrightnessSupported)
            WriteIfChanged(VcpBrightness, brightnessPercent, Capability.BrightnessMax, ref _lastBrightness);
        if (Capability.SharpnessSupported)
            WriteIfChanged(VcpSharpness, sharpnessPercent, Capability.SharpnessMax, ref _lastSharpness);
    }

    private void WriteIfChanged(byte vcp, int percent, uint max, ref int last)
    {
        int pct = Math.Clamp(percent, 0, 100);
        uint value = (uint)Math.Round(pct / 100.0 * max);
        if ((int)value == last) return;
        try { if (SetVCPFeature(_hMonitor, vcp, value)) last = (int)value; }
        catch { /* DDC/CI write failed — retry next tick */ }
    }

    public void RestoreBaseline()
    {
        if (_restored || !IsInitialized) return;
        _restored = true;
        try
        {
            if (_baseline.HasContrast)   SetVCPFeature(_hMonitor, VcpContrast,   _baseline.OriginalContrast);
            if (_baseline.HasBrightness) SetVCPFeature(_hMonitor, VcpBrightness, _baseline.OriginalBrightness);
            if (_baseline.HasSharpness)  SetVCPFeature(_hMonitor, VcpSharpness,  _baseline.OriginalSharpness);
        }
        catch { /* best-effort restore */ }
        _lastContrast = _lastBrightness = _lastSharpness = -1;
    }

    public void Dispose()
    {
        RestoreBaseline();
        try
        {
            if (_monitors is not null)
                DestroyPhysicalMonitors((uint)_monitors.Length, _monitors);
        }
        catch { /* best-effort */ }
        _monitors = null;
    }

    // ── dxva2 / user32 interop ────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(
        IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(
        IntPtr hMonitor, uint dwPhysicalMonitorArraySize,
        [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitors(
        uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(
        IntPtr hMonitor, byte bVCPCode, out uint pvct,
        out uint pdwCurrentValue, out uint pdwMaximumValue);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetVCPFeature(IntPtr hMonitor, byte bVCPCode, uint dwNewValue);
}
