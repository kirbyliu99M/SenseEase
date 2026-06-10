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

    /// <summary>Reads the current registry state. Returns true iff a non-empty
    /// value is set for SenseEase under the Run key. Self-healing:
    /// <list type="bullet">
    ///   <item>Empty/whitespace value — left by the 0608/0609 installer bug —
    ///   is treated as DISABLED and is immediately deleted to avoid a ghost Run
    ///   entry that launches nothing.</item>
    ///   <item>Non-empty value that no longer matches the current EXE (e.g. after
    ///   an in-place update to a versioned folder, a reinstall, or a portable move)
    ///   is rewritten to the correct path so autostart keeps launching the live
    ///   EXE. A rewrite failure does not change the reported state — a stale-but-
    ///   present value still reports enabled.</item>
    /// </list>
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key?.GetValue(ValueName) is not string current) return false;

            // Defense-in-depth: empty/whitespace was written by the 0608/0609
            // installer bug (ValueType: string with dontcreatekey still wrote
            // SenseEase="" because HKCU\...\Run always exists). Treat as disabled
            // and purge so it never ghost-launches anything.
            if (string.IsNullOrWhiteSpace(current))
            {
                DeleteValue();
                return false;
            }

            string? expected = BuildRunValue();
            if (expected is not null &&
                !string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
            {
                Heal(expected);
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoStartRegistry] IsEnabled read failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Builds the canonical Run-key value (quoted current EXE path
    /// plus the <see cref="MinimizedArg"/> flag), or null when the EXE path is
    /// unavailable.</summary>
    private static string? BuildRunValue()
    {
        string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
        return string.IsNullOrEmpty(exePath) ? null : $"\"{exePath}\" {MinimizedArg}";
    }

    /// <summary>Deletes the Run-key value. Used by <see cref="IsEnabled"/> to
    /// purge an empty/whitespace ghost entry left by the 0608/0609 installer
    /// bug. Failures are swallowed (logged only).</summary>
    private static void DeleteValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            Debug.WriteLine("[AutoStartRegistry] purged empty/whitespace Run-key ghost value.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoStartRegistry] DeleteValue failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Rewrites the Run-key value to <paramref name="value"/>. Used by
    /// <see cref="IsEnabled"/> to correct a stale path. Failures are swallowed
    /// (logged only) so a read can never throw from a heal attempt.</summary>
    private static void Heal(string value)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;
            key.SetValue(ValueName, value, RegistryValueKind.String);
            Debug.WriteLine("[AutoStartRegistry] healed stale Run-key path to current EXE.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoStartRegistry] heal failed: {ex.GetType().Name}: {ex.Message}");
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
                string? value = BuildRunValue();
                if (value is null)
                {
                    Debug.WriteLine("[AutoStartRegistry] EXE path unavailable; cannot enable autostart.");
                    return false;
                }
                key.SetValue(ValueName, value, RegistryValueKind.String);
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
