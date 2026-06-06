using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SenseEase.App.Interop;

/// <summary>
/// Fixes the blank taskbar button for a process that sets an explicit
/// AppUserModelID (see App.OnStartup) but is NOT installed with a Start-menu
/// shortcut registering that AUMID. Windows groups the taskbar button under the
/// AUMID and, finding no icon registered for it, paints a blank button — even
/// though <see cref="Window.Icon"/> correctly draws the title-bar / Alt-Tab icon
/// (which is why the title bar looks right while the taskbar stays dead).
///
/// <para>Setting <c>PKEY_AppUserModel_RelaunchIconResource</c> on the window's
/// shell property store tells the taskbar which icon to use for the AUMID group:
/// our own EXE, icon index 0 (the embedded <c>ApplicationIcon</c>). Entirely
/// best-effort — any failure is a silent no-op that leaves the (already correct)
/// title-bar icon untouched; it never throws on the startup path.</para>
/// </summary>
internal static class ShellTaskbarIcon
{
    public static void RegisterForWindow(Window window)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            string? exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return;

            Guid iid = IID_IPropertyStore;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out IPropertyStore? store) != 0 || store is null)
                return;

            try
            {
                if (InitPropVariantFromString($"{exe},0", out PROPVARIANT pv) != 0) return;
                try
                {
                    PROPERTYKEY key = PKEY_AppUserModel_RelaunchIconResource;
                    store.SetValue(ref key, ref pv);
                    store.Commit();
                }
                finally { PropVariantClear(ref pv); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch
        {
            // Best-effort: the title-bar / Alt-Tab icon is already correct via
            // Window.Icon; a property-store failure simply leaves the taskbar
            // button as-is rather than crashing startup.
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr hwnd, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? propertyStore);

    [DllImport("propsys.dll")]
    private static extern int InitPropVariantFromString(
        [MarshalAs(UnmanagedType.LPWStr)] string psz, out PROPVARIANT ppropvar);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    private static Guid IID_IPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

    // PKEY_AppUserModel_RelaunchIconResource — {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, PID 3.
    private static readonly PROPERTYKEY PKEY_AppUserModel_RelaunchIconResource = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 3,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    // PROPVARIANT laid out for x64 (24 bytes): VARTYPE + 3 reserved WORDs + an
    // 8-byte union (the LPWSTR pointer) + 8-byte tail padding. InitPropVariantFromString
    // populates it; PropVariantClear frees it.
    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr unionValue;
        public IntPtr unionTail;
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint cProps);
        int GetAt(uint iProp, out PROPERTYKEY pkey);
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        int Commit();
    }
}
