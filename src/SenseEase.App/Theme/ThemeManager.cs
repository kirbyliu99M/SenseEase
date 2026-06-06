using System;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SenseEase.App.Theme;

/// <summary>
/// Makes the app follow the Windows light/dark system setting by merging a
/// dark-palette <see cref="ResourceDictionary"/> view-locally into every
/// <see cref="Window"/> when the OS is in dark mode, and removing it again when
/// the OS flips back to light.
///
/// <para>Design constraints (mirroring SenseEaseTheme.xaml's "never App-level"
/// rule): the dark palette is merged into each <c>Window.Resources</c>, NOT
/// <c>Application.Resources</c>. <see cref="Initialize"/> is invoked ONLY from
/// the real <see cref="App.OnStartup"/>; the bare <c>new Application()</c> that
/// MainWindowSmokeTests boots never calls it, so test windows keep the light
/// defaults.</para>
///
/// <para>Degrade-not-crash posture: every registry / IO touch is wrapped in
/// try/catch so a failure simply leaves the light default in place and never
/// throws into the WPF message loop.</para>
/// </summary>
public static class ThemeManager
{
    /// <summary>Pack URI for the dark-palette resource dictionary. Same
    /// assembly + <c>;component/Theme/</c> convention as SenseEaseTheme.xaml so
    /// the merge resolves view-locally inside each window.</summary>
    public const string DarkPaletteUri =
        "pack://application:,,,/SenseEase.App;component/Theme/Palette.Dark.xaml";

    // Guard so Initialize is a no-op if accidentally called twice (the real App
    // only calls it once, but idempotence keeps the class-handler/SystemEvents
    // subscription from doubling up).
    private static bool _initialized;

    /// <summary>
    /// True when Windows is configured for the dark apps theme. Reads
    /// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize</c>
    /// value <c>AppsUseLightTheme</c> (DWORD): 0 = dark; 1 or missing = light.
    /// Any failure (key absent, access denied, unexpected value type) is
    /// swallowed and treated as light so the app degrades to its default.
    /// </summary>
    public static bool DetectDark()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key is null) return false; // key missing → light default

            object? raw = key.GetValue("AppsUseLightTheme");
            if (raw is int dword) return dword == 0; // 0 = dark
            return false; // missing / unexpected type → light default
        }
        catch
        {
            // Registry read failed (security policy, IO fault, …): leave the
            // light default. Never throw out of theme detection.
            return false;
        }
    }

    /// <summary>
    /// Call ONCE from <see cref="App.OnStartup"/>. Wires the app to follow the
    /// OS theme by (a) registering a class handler so every window applies the
    /// theme on Loaded, (b) applying immediately to any already-open windows,
    /// and (c) re-evaluating + re-applying when the OS theme flips.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            // (a) Every Window applies the current theme as it loads. A class
            // handler fires for ALL Window instances without each window having
            // to opt in, so future windows are covered automatically.
            EventManager.RegisterClassHandler(
                typeof(Window),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnWindowLoaded));

            // (b) Apply to any windows that already exist at init time (e.g. a
            // window constructed before Initialize ran).
            if (Application.Current is { } app)
            {
                foreach (Window w in app.Windows)
                    ApplyTo(w);
            }

            // (c) Re-apply to all open windows when the OS theme flips. The
            // General category covers the light/dark personalization change.
            // SystemEvents fires on a background thread, so marshal the
            // re-apply onto the UI dispatcher (guarding against a null
            // dispatcher in degenerate teardown ordering).
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        catch
        {
            // Any wiring failure leaves the app on its light default — never
            // throw out of startup.
        }
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window w) ApplyTo(w);
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;

        try
        {
            Dispatcher? dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return;

            dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (Application.Current is { } app)
                    {
                        foreach (Window w in app.Windows)
                            ApplyTo(w);
                    }
                }
                catch
                {
                    // Re-apply is best-effort; leave windows as-is on failure.
                }
            }));
        }
        catch
        {
            // Dispatcher marshaling failed (e.g. shutdown in progress): ignore.
        }
    }

    /// <summary>
    /// Idempotently brings window <paramref name="w"/> into sync with the OS
    /// theme: when dark, ensures the dark-palette dictionary is present in
    /// <c>w.Resources.MergedDictionaries</c> (adds it if absent); when light,
    /// removes it if present. Wrapped in try/catch so a registry/IO/URI failure
    /// just leaves the light default and never throws.
    /// </summary>
    public static void ApplyTo(Window w)
    {
        if (w is null) return;

        try
        {
            var merged = w.Resources.MergedDictionaries;
            ResourceDictionary? existing = FindDarkPalette(merged);

            if (DetectDark())
            {
                // Ensure present (add only if missing — idempotent).
                if (existing is null)
                {
                    merged.Add(new ResourceDictionary
                    {
                        Source = new Uri(DarkPaletteUri, UriKind.Absolute)
                    });
                }
            }
            else
            {
                // Ensure absent (remove only if present — idempotent).
                if (existing is not null)
                    merged.Remove(existing);
            }
        }
        catch
        {
            // Loading the palette / mutating the dictionary failed: leave the
            // window on its light default. Never throw.
        }
    }

    /// <summary>Locate an already-merged dark-palette dictionary by its Source
    /// URI so adds/removes stay idempotent.</summary>
    private static ResourceDictionary? FindDarkPalette(
        System.Collections.ObjectModel.Collection<ResourceDictionary> merged)
    {
        foreach (ResourceDictionary rd in merged)
        {
            if (rd.Source is { } src &&
                string.Equals(src.ToString(), DarkPaletteUri, StringComparison.OrdinalIgnoreCase))
            {
                return rd;
            }
        }
        return null;
    }
}
