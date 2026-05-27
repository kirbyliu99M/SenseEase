using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace SenseEase.App.Configuration;

/// <summary>
/// Path Z UX iter 5: Windows auto-launch registry helper.
///
/// <para>Writes / removes <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>
/// entry "SenseEase" pointing at the current process EXE plus the
/// <c>--minimized</c> command-line flag so the app starts hidden to the
/// tray on login rather than flashing the MainWindow.</para>
///
/// <para>HKCU (not HKLM) chosen deliberately — no admin rights required,
/// scope is per-user, and uninstalling for the current user is
/// straightforward via the same helper. Bypasses Task Scheduler entirely.</para>
///
/// <para>All methods are exception-safe at the boundary: registry failures
/// (key unavailable, permission denied, etc.) are caught and logged but
/// never propagate. The user toggle stays in sync with whatever the
/// registry actually holds via <see cref="IsEnabled"/>.</para>
/// </summary>
public static class AutoStartRegistry
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName  = "SenseEase";

    /// <summary>Command-line argument the app passes itself on autostart so
    /// it knows to skip showing the MainWindow. <see cref="App.OnStartup"/>
    /// reads this from <see cref="StartupEventArgs.Args"/>.</summary>
    public const string MinimizedArg = "--minimized";

    /// <summary>Reads the current registry state. Returns true iff a value
    /// is set for SenseEase under the Run key. Does NOT verify the path
    /// matches the current EXE — a stale value (e.g. after a build path
    /// change) still reports enabled.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is not null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoStartRegistry] IsEnabled read failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Writes or removes the registry value to match the requested
    /// state. When enabling, the value is set to the current process EXE
    /// path (quoted to survive spaces) followed by the <see cref="MinimizedArg"/>
    /// flag so autostart goes straight to tray. Returns true on success.</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return false;

            if (enabled)
            {
                string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath))
                {
                    Debug.WriteLine("[AutoStartRegistry] EXE path unavailable; cannot enable autostart.");
                    return false;
                }
                key.SetValue(ValueName, $"\"{exePath}\" {MinimizedArg}", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoStartRegistry] SetEnabled({enabled}) failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
