using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SenseEase.App.Interop;

/// <summary>
/// Fixes the blank / default taskbar button on Windows 11 for an app with a heavy
/// startup. Per <see href="https://github.com/dotnet/wpf/issues/11308">dotnet/wpf#11308</see>,
/// the Win11 taskbar object initialises EARLIER than on Win10, so when a window is
/// created after a lengthy startup (SenseEase loads the ML model / WGC pipeline) the
/// taskbar has already captured a default icon before WPF applies <see cref="Window.Icon"/>.
/// The title-bar / Alt-Tab icon (driven by <c>Window.Icon</c>) looks right while the
/// taskbar button stays blank — exactly the observed symptom.
///
/// <para>The maintainer-recommended workaround: during <c>OnSourceInitialized</c>, push
/// the app's OWN embedded icon onto the window via the Win32 class icons
/// (<c>SetClassLongPtr GCLP_HICON / GCLP_HICONSM</c> — what the taskbar reads) plus
/// <c>WM_SETICON</c>, so it overwrites the default the early taskbar grabbed. The icon
/// is extracted from the apphost EXE (<c>SenseEase.App.exe</c>, which embeds
/// <c>ApplicationIcon</c>), resolved NEXT TO the running assembly so it is correct even
/// under <c>dotnet run</c> (where the process module may be the SDK host). Entirely
/// best-effort: any failure is a silent no-op that leaves the already-correct
/// <c>Window.Icon</c> untouched and never throws on the startup path.</para>
/// </summary>
internal static class ShellTaskbarIcon
{
    public static void RegisterForWindow(Window window)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            // The apphost EXE embeds ApplicationIcon. Resolve it next to the running
            // assembly (…\SenseEase.App.exe) so the icon source is correct even when
            // the process module is the SDK host under `dotnet run`. Fall back to the
            // current process module if the sibling EXE is somehow absent.
            string exe = Path.ChangeExtension(typeof(ShellTaskbarIcon).Assembly.Location, ".exe");
            if (!File.Exists(exe))
                exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;

            // Pull the large (32px) + small (16px) variants of icon group 0 — the
            // multi-size indigo ApplicationIcon baked into the EXE.
            ExtractIconEx(exe, 0, out IntPtr hiconBig, out IntPtr hiconSmall, 1);
            if (hiconBig == IntPtr.Zero && hiconSmall == IntPtr.Zero) return;

            // Class icons: what the Win11 taskbar reads. Overwrites the default the
            // early taskbar captured. WPF gives each top-level window its own class,
            // so this affects only this window.
            if (hiconBig != IntPtr.Zero)   SetClassLongPtr(hwnd, GCLP_HICON,   hiconBig);
            if (hiconSmall != IntPtr.Zero) SetClassLongPtr(hwnd, GCLP_HICONSM, hiconSmall);

            // WM_SETICON: the window's own icons (title bar + Alt-Tab + taskbar
            // fallback), belt-and-suspenders alongside the class icons.
            if (hiconSmall != IntPtr.Zero) SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, hiconSmall);
            if (hiconBig != IntPtr.Zero)   SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG,   hiconBig);

            // The HICONs are now referenced by the window/class; the shell owns their
            // lifetime for the window's duration, so we intentionally do not destroy
            // them here (destroying them would blank the icon again).
        }
        catch
        {
            // Best-effort: Window.Icon already paints the title-bar / Alt-Tab icon, so
            // a failure here simply leaves the taskbar button as Windows drew it rather
            // than crashing the startup path.
        }
    }

    private const int GCLP_HICON   = -14;   // class big icon
    private const int GCLP_HICONSM = -34;   // class small icon
    private const int WM_SETICON   = 0x0080;
    private const int ICON_SMALL   = 0;
    private const int ICON_BIG     = 1;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(
        string lpszFile, int nIconIndex,
        out IntPtr phiconLarge, out IntPtr phiconSmall, int nIcons);

    // SetClassLongPtrW is the 64-bit export (the app + apphost are x64).
    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW", SetLastError = true)]
    private static extern IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
