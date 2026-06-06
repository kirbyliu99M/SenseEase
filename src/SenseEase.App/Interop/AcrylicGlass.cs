using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SenseEase.App.Interop;

/// <summary>
/// Win32 interop helper to apply a Win11 Fluent acrylic blur-behind to a WPF
/// window via the undocumented SetWindowCompositionAttribute API. Failures are a
/// silent no-op so the opaque card simply remains.
/// </summary>
public static class AcrylicGlass
{
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(
        IntPtr hwnd, ref WindowCompositionAttributeData data);

    /// <summary>
    /// Apply an acrylic frosted-glass blur behind the given window. The
    /// <paramref name="gradientAbgr"/> tint is in 0xAABBGGRR (ABGR) order. Call
    /// after the HWND exists (e.g. from Window.SourceInitialized). A failure is a
    /// silent no-op and the opaque card remains.
    /// </summary>
    public static void EnableAcrylic(Window window, uint gradientAbgr)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;

            var accent = new AccentPolicy
            {
                AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = 0,
                GradientColor = gradientAbgr,
                AnimationId = 0,
            };

            int accentSize = Marshal.SizeOf<AccentPolicy>();
            IntPtr accentPtr = Marshal.AllocHGlobal(accentSize);
            try
            {
                Marshal.StructureToPtr(accent, accentPtr, false);

                var data = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY,
                    Data = accentPtr,
                    SizeOfData = accentSize,
                };

                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(accentPtr);
            }
        }
        catch
        {
            // Silent no-op: the opaque card remains.
        }
    }
}
