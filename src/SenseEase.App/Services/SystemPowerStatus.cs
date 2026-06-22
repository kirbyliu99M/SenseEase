using System;
using System.Runtime.InteropServices;

namespace SenseEase.App.Services;

/// <summary>Thin wrapper over the Win32 <c>GetSystemPowerStatus</c> AC-line query. Used to gate
/// battery-sensitive optimizations (the warm-on-load skip; a future auto-power-save). Fails OPEN: any error,
/// a machine with no battery, or an "unknown" line status all report NOT on battery — so an optimization is
/// never wrongly suppressed on a desktop or when the OS can't tell.</summary>
internal static class SystemPowerStatus
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;        // 0 = offline (on battery), 1 = online (plugged in), 255 = unknown
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int  BatteryLifeTime;
        public int  BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    /// <summary>True only when the machine is confirmed running on battery (AC line reported offline). A
    /// desktop, or any query failure / unknown status, returns false so the caller treats it as plugged in.</summary>
    public static bool OnBattery()
    {
        try { return GetSystemPowerStatus(out var s) && s.ACLineStatus == 0; }
        catch { return false; }
    }
}
