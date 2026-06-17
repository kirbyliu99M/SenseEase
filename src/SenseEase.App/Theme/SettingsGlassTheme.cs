using System;
using System.Collections.Generic;
using System.Windows;
using Microsoft.Win32;
using SenseEase.App.Configuration;

namespace SenseEase.App.Theme;

/// <summary>
/// Applies the GLASS bridge (<c>SettingsGlassPalette[.Dark].xaml</c>) to the Settings surfaces so
/// they match the popup panel. The element merges the SLATE glass (SettingsGlassPalette.xaml) in its
/// XAML; this helper merges the Game-Bar <c>SettingsGlassPalette.Dark.xaml</c> ABOVE it when the
/// effective theme is dark (and removes it when light), re-applying on a live theme flip — exactly
/// how the popup's <c>ApplyPopupTheme</c> flips PopupGlassTokens.Dark. It is the drop-in replacement
/// for <see cref="ThemeManager.TrackElement"/> on these surfaces (TrackElement would merge the GREY
/// Palette.Dark and fight the glass). Degrade-not-crash: every touch is guarded.
/// </summary>
public static class SettingsGlassTheme
{
    public const string DarkUri =
        "pack://application:,,,/SenseEase.App;component/Theme/SettingsGlassPalette.Dark.xaml";

    // Tracked surfaces held WEAKLY (so a closed window never pins the tree); pruned on each pass.
    private static readonly List<WeakReference<FrameworkElement>> _tracked = new();
    private static readonly object _lock = new();
    private static bool _wired;

    /// <summary>Apply the glass dark variant now + re-apply on every theme flip. Pair with
    /// <see cref="Untrack"/> on Unloaded/Closed. Idempotent.</summary>
    public static void Track(FrameworkElement element)
    {
        if (element is null) return;
        lock (_lock)
        {
            bool already = false;
            for (int i = _tracked.Count - 1; i >= 0; i--)
            {
                if (!_tracked[i].TryGetTarget(out var t)) _tracked.RemoveAt(i);
                else if (ReferenceEquals(t, element)) already = true;
            }
            if (!already) _tracked.Add(new WeakReference<FrameworkElement>(element));
            WireGlobalOnce();
        }
        Apply(element);
    }

    /// <summary>Stop tracking a previously-registered surface (call on Unloaded/Closed).</summary>
    public static void Untrack(FrameworkElement element)
    {
        if (element is null) return;
        lock (_lock)
            for (int i = _tracked.Count - 1; i >= 0; i--)
                if (!_tracked[i].TryGetTarget(out var t) || ReferenceEquals(t, element))
                    _tracked.RemoveAt(i);
    }

    private static void WireGlobalOnce()
    {
        if (_wired) return;
        _wired = true;
        // UserPreferences.Changed = the Settings → Appearance picker / forced Light↔Dark.
        try { UserPreferences.Changed += (_, _) => ApplyAll(); } catch { }
        // The OS light/dark flip (Auto mode follows it; forced modes ignore — cheap idempotent re-run).
        try { SystemEvents.UserPreferenceChanged += (_, e) => { if (e.Category == UserPreferenceCategory.General) ApplyAll(); }; } catch { }
    }

    private static void ApplyAll()
    {
        List<FrameworkElement> live = new();
        lock (_lock)
            for (int i = _tracked.Count - 1; i >= 0; i--)
            {
                if (_tracked[i].TryGetTarget(out var el)) live.Add(el);
                else _tracked.RemoveAt(i);
            }
        foreach (var el in live)
        {
            try
            {
                if (el.Dispatcher.CheckAccess()) Apply(el);
                else el.Dispatcher.BeginInvoke(new Action(() => Apply(el)));
            }
            catch { }
        }
    }

    /// <summary>Idempotently merge the Game-Bar dark glass when dark, remove it when light. Never throws.</summary>
    public static void Apply(FrameworkElement element)
    {
        if (element is null) return;
        bool dark;
        try { dark = ThemeManager.ShouldUseDark(); }
        catch { dark = false; }

        try
        {
            var merged = element.Resources.MergedDictionaries;
            ResourceDictionary? existing = null;
            foreach (ResourceDictionary rd in merged)
            {
                if (rd.Source is { } src &&
                    string.Equals(src.ToString(), DarkUri, StringComparison.OrdinalIgnoreCase))
                { existing = rd; break; }
            }

            if (dark)
            {
                if (existing is null)
                    merged.Add(new ResourceDictionary { Source = new Uri(DarkUri, UriKind.Absolute) });
            }
            else if (existing is not null)
            {
                merged.Remove(existing);
            }
        }
        catch { /* leave the slate glass on failure */ }
    }
}
