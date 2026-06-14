using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SenseEase.Infrastructure.Real;

/// <summary>
/// Beta-1 D-16 / theme-2: resolve a foreground window handle to a (category, appName) pair —
/// the on-device per-app attribution key. Best-effort: every Win32 step is guarded and a failure
/// (dead window, ACCESS_DENIED on a protected/elevated process, etc.) degrades to null rather
/// than throwing (Raymond Chen: the foreground window may be gone by the time we resolve it).
///
/// <para>Privacy (D-16): returns a COARSE category + the raw exe base-name (no path, no window
/// title). Stored on-device only; export de-identifies to category-only (D-17). UWP apps are
/// hosted by ApplicationFrameHost.exe — for Beta-1 that host is reported as its own name (so it
/// bins to "other"); resolving the real packaged-app AUMID is a documented follow-on.</para>
/// </summary>
public static class ForegroundAppResolver
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, [Out] StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>Resolve the owning process of <paramref name="hwnd"/> to (category, appName), or
    /// null on any failure / null hwnd. The category comes from <see cref="AppCategoryMapper"/>.</summary>
    public static (string category, string appName)? Resolve(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        try
        {
            if (GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == 0) return null;

            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (!QueryFullProcessImageNameW(h, 0, sb, ref size)) return null;
                string name = Path.GetFileNameWithoutExtension(sb.ToString());
                if (string.IsNullOrWhiteSpace(name)) return null;
                return (AppCategoryMapper.Categorize(name), name.ToLowerInvariant());
            }
            finally { CloseHandle(h); }
        }
        catch { return null; } // best-effort: attribution must never destabilise sensing
    }

    /// <summary>The on-device key format stored in SymptomEvent.ForegroundKey: "category/app"
    /// (raw name kept locally per D-16; export strips to the category half per D-17).</summary>
    public static string FormatKey(string category, string appName) => category + "/" + appName;
}
