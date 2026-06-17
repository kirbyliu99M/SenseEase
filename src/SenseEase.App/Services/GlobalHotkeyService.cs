using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace SenseEase.App.Services;

/// <summary>
/// TASK #7 (UI-RESHAPE): a system-wide hotkey that reveals / toggles the glass popup bar
/// (default <c>Win+Alt+S</c>, user-customisable). Registers via the Win32
/// <c>RegisterHotKey</c>/<c>UnregisterHotKey</c> pair against a message-only
/// <see cref="HwndSource"/> (a 0×0 hidden window) and raises <see cref="Pressed"/> when the
/// gesture fires (<c>WM_HOTKEY</c>).
///
/// <para><b>Fail-soft contract.</b> <see cref="Register"/> NEVER throws — a malformed gesture,
/// a key already claimed by another app, or any Win32 failure returns <c>false</c> and logs one
/// line through <see cref="SenseEase.Infrastructure.Real.WgcDiagLog"/>. The shell still works
/// (the edge sidebar click is the always-available reveal); the global hotkey is a convenience
/// on top, so a failed registration degrades silently rather than crashing launch.</para>
///
/// <para>App-lifetime: created from <c>App.OnStartup</c> after the <see cref="ShellController"/>
/// exists, stored in an App field, and <see cref="Dispose"/>d in <c>App.OnExit</c>. Re-register
/// (after a Settings change) by calling <see cref="Unregister"/> then <see cref="Register"/>.</para>
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    // ── Win32 ───────────────────────────────────────────────────────────────
    private const int WM_HOTKEY = 0x0312;

    /// <summary>Win32 hotkey modifier flag — ALT.</summary>
    public const uint MOD_ALT = 0x0001;
    /// <summary>Win32 hotkey modifier flag — CONTROL.</summary>
    public const uint MOD_CONTROL = 0x0002;
    /// <summary>Win32 hotkey modifier flag — SHIFT.</summary>
    public const uint MOD_SHIFT = 0x0004;
    /// <summary>Win32 hotkey modifier flag — WIN (the Windows logo key).</summary>
    public const uint MOD_WIN = 0x0008;

    // MOD_NOREPEAT (0x4000) coalesces the auto-repeat a held key would otherwise
    // generate into a single press — a reveal-toggle must fire once per chord, not
    // continuously while the key is held.
    private const uint MOD_NOREPEAT = 0x4000;

    // A fixed, app-local hotkey id. We only ever register ONE hotkey at a time
    // (the reveal toggle), so a constant id is sufficient and avoids id bookkeeping.
    private const int HotkeyId = 0xB00B;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private HwndSource? _source;
    private bool _registered;
    private bool _disposed;

    /// <summary>Raised on the UI thread when the registered gesture is pressed. Wire this to the
    /// shell's popup-toggle (marshal to the dispatcher at the consumer if needed).</summary>
    public event EventHandler? Pressed;

    /// <summary>The gesture string this service is currently registered for (e.g. "Win+Alt+S"),
    /// or <c>null</c> when no registration is active. Surfacing-only.</summary>
    public string? CurrentGesture { get; private set; }

    /// <summary>Register the system-wide hotkey for <paramref name="gesture"/> (e.g. "Win+Alt+S").
    /// Idempotent-ish: any prior registration is cleared first. Returns <c>true</c> on success;
    /// on ANY failure (unparseable gesture, key already taken, Win32 error) returns <c>false</c>
    /// after logging one diagnostic line — never throws.</summary>
    public bool Register(string gesture)
    {
        if (_disposed) return false;

        // Always start from a clean slate so a re-register (Settings change) doesn't
        // leak the previous binding or trip ERROR_HOTKEY_ALREADY_REGISTERED on itself.
        Unregister();

        if (!TryParse(gesture, out uint mods, out uint vk))
        {
            SenseEase.Infrastructure.Real.WgcDiagLog.Append(
                $"[{DateTime.UtcNow:O}] [GlobalHotkey] register skipped — unparseable gesture " +
                $"'{gesture}' (keeping no global hotkey; sidebar click still reveals)");
            return false;
        }

        try
        {
            // Message-only sink: a 0-size hidden HwndSource gives us an HWND + a WndProc
            // hook without ever showing a window. Created lazily and reused across
            // re-registers so we don't churn windows on every Settings change.
            if (_source is null)
            {
                _source = new HwndSource(new HwndSourceParameters("SenseEaseGlobalHotkeySink")
                {
                    Width = 0,
                    Height = 0,
                    WindowStyle = 0,
                    // Parent unset → a top-level message sink. We never Show() it.
                });
                _source.AddHook(WndProc);
            }

            if (!RegisterHotKey(_source.Handle, HotkeyId, mods | MOD_NOREPEAT, vk))
            {
                int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                SenseEase.Infrastructure.Real.WgcDiagLog.Append(
                    $"[{DateTime.UtcNow:O}] [GlobalHotkey] RegisterHotKey FAILED for '{gesture}' " +
                    $"(Win32 error {err}; likely already claimed by another app) — " +
                    "degraded: no global hotkey this session, sidebar click still reveals");
                return false;
            }

            _registered = true;
            CurrentGesture = gesture;
            SenseEase.Infrastructure.Real.WgcDiagLog.Append(
                $"[{DateTime.UtcNow:O}] [GlobalHotkey] registered '{gesture}'");
            return true;
        }
        catch (Exception ex)
        {
            // Fail-soft: any surprise (HwndSource creation, P/Invoke marshal) is logged
            // and swallowed — the global hotkey is a convenience, never a launch blocker.
            SenseEase.Infrastructure.Real.WgcDiagLog.Append(
                $"[{DateTime.UtcNow:O}] [GlobalHotkey] register threw for '{gesture}': " +
                $"{ex.GetType().Name}: {ex.Message} — continuing without a global hotkey");
            return false;
        }
    }

    /// <summary>Drop the current registration if any (the message sink is retained for reuse).
    /// Best-effort and never throws — safe to call when nothing is registered.</summary>
    public void Unregister()
    {
        if (!_registered || _source is null) { CurrentGesture = null; return; }
        try { UnregisterHotKey(_source.Handle, HotkeyId); }
        catch { /* best-effort */ }
        _registered = false;
        CurrentGesture = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            try { Pressed?.Invoke(this, EventArgs.Empty); }
            catch { /* a handler fault must not propagate into the message pump */ }
        }
        return IntPtr.Zero;
    }

    /// <summary>Parse a gesture string like "Win+Alt+S" into Win32 modifier flags +
    /// a virtual-key code. The LAST token is the key; every other token is a modifier
    /// (Ctrl/Control, Alt, Shift, Win/Windows/Meta — case-insensitive). Returns
    /// <c>false</c> (and leaves the outs zeroed) when the string is empty, carries no
    /// modifier, or the key token isn't a recognised <see cref="Key"/>. Pure + never throws,
    /// so it is safe to call from deserialisation / hydration paths.</summary>
    public static bool TryParse(string? gesture, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(gesture)) return false;

        string[] tokens = gesture.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return false; // require at least one modifier + a key

        uint parsedMods = 0;
        for (int i = 0; i < tokens.Length - 1; i++)
        {
            switch (tokens[i].Trim().ToLowerInvariant())
            {
                case "ctrl":
                case "control": parsedMods |= MOD_CONTROL; break;
                case "alt": parsedMods |= MOD_ALT; break;
                case "shift": parsedMods |= MOD_SHIFT; break;
                case "win":
                case "windows":
                case "meta": parsedMods |= MOD_WIN; break;
                default: return false; // unknown modifier token
            }
        }

        if (parsedMods == 0) return false; // a bare key with no modifier is not a valid global hotkey

        string keyToken = tokens[^1].Trim();
        if (!Enum.TryParse<Key>(keyToken, ignoreCase: true, out var key) || key == Key.None)
            return false;

        int vkCode = KeyInterop.VirtualKeyFromKey(key);
        if (vkCode <= 0) return false;

        mods = parsedMods;
        vk = (uint)vkCode;
        return true;
    }

    /// <summary>Format Win32 modifier flags + a <see cref="Key"/> back into the canonical
    /// gesture string ("Win+Alt+S"). Modifier order is fixed (Ctrl, Alt, Shift, Win) so a
    /// captured chord always renders the same way regardless of press order. The
    /// <see cref="MOD_NOREPEAT"/> bit (and any unknown bits) are ignored.</summary>
    public static string Format(uint mods, Key key)
    {
        var parts = new System.Collections.Generic.List<string>(4);
        if ((mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & MOD_WIN) != 0) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>Releases the hotkey registration and disposes the message-only sink window.
    /// Idempotent. Called from <c>App.OnExit</c>.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
        try
        {
            if (_source is not null)
            {
                _source.RemoveHook(WndProc);
                _source.Dispose();
            }
        }
        catch { /* best-effort teardown */ }
        _source = null;
    }
}
