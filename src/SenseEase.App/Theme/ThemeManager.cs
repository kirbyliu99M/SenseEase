using System;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using SenseEase.App.Configuration;

namespace SenseEase.App.Theme;

/// <summary>
/// Drives the app's light/dark appearance by merging a dark-palette
/// <see cref="ResourceDictionary"/> view-locally into every <see cref="Window"/>
/// when "dark" is in effect, and removing it again when "light" is in effect.
///
/// <para>Appearance setting (2026-06-07): the effective theme is decided by the
/// user's saved <see cref="ThemeMode"/> choice (persisted in
/// <see cref="UserPreferences.ThemeMode"/>):</para>
/// <list type="bullet">
///   <item><see cref="ThemeMode.Auto"/> — follow the Windows light/dark system
///         setting (registry <c>AppsUseLightTheme</c>); live-updates when the OS
///         theme flips. This is the legacy behaviour and the default.</item>
///   <item><see cref="ThemeMode.Light"/> — force light; never dark, regardless
///         of the OS setting. OS flips are ignored.</item>
///   <item><see cref="ThemeMode.Dark"/> — force dark; always dark, regardless of
///         the OS setting. OS flips are ignored.</item>
/// </list>
///
/// <para>Design constraints (mirroring SenseEaseTheme.xaml's "never App-level"
/// rule): the dark palette is merged into each <c>Window.Resources</c>, NOT
/// <c>Application.Resources</c>. <see cref="Initialize"/> is invoked ONLY from
/// the real <see cref="App.OnStartup"/>; the bare <c>new Application()</c> that
/// MainWindowSmokeTests boots never calls it, so test windows keep the light
/// defaults.</para>
///
/// <para>Degrade-not-crash posture: every registry / IO / preferences touch is
/// wrapped in try/catch so a failure simply leaves the light default in place
/// and never throws into the WPF message loop.</para>
/// </summary>
public static class ThemeManager
{
    /// <summary>User-selectable theme mode. Re-exported from
    /// <see cref="AppThemeMode"/> (which lives next to the persistence layer in
    /// <see cref="UserPreferences"/>) so callers can write
    /// <c>ThemeManager.ThemeMode.Dark</c> without a second using. The two are
    /// the same underlying type.</summary>
    public enum ThemeMode
    {
        /// <summary>Follow the Windows system light/dark setting (default).</summary>
        Auto = AppThemeMode.Auto,
        /// <summary>Always light, never dark.</summary>
        Light = AppThemeMode.Light,
        /// <summary>Always dark.</summary>
        Dark = AppThemeMode.Dark,
    }

    /// <summary>Pack URI for the dark-palette resource dictionary. Same
    /// assembly + <c>;component/Theme/</c> convention as SenseEaseTheme.xaml so
    /// the merge resolves view-locally inside each window.</summary>
    public const string DarkPaletteUri =
        "pack://application:,,,/SenseEase.App;component/Theme/Palette.Dark.xaml";

    // Guard so Initialize is a no-op if accidentally called twice (the real App
    // only calls it once, but idempotence keeps the class-handler/SystemEvents
    // subscription from doubling up).
    private static bool _initialized;

    // The effective user choice. Cached in-process so the hot per-window
    // ApplyTo path (and the SystemEvents callback) doesn't re-read prefs on
    // every call. Seeded from UserPreferences at Initialize() and updated by
    // SetMode(). Defaults to Auto so the pre-Initialize / test path behaves
    // exactly like the legacy OS-following manager.
    private static ThemeMode _mode = ThemeMode.Auto;

    /// <summary>The currently-applied theme mode (in-process cache; seeded from
    /// <see cref="UserPreferences.ThemeMode"/> at <see cref="Initialize"/>).</summary>
    public static ThemeMode CurrentMode => _mode;

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
    /// Resolves whether DARK should be applied right now, honouring the user's
    /// <see cref="ThemeMode"/>:
    /// <list type="bullet">
    ///   <item><see cref="ThemeMode.Light"/> → always false.</item>
    ///   <item><see cref="ThemeMode.Dark"/> → always true.</item>
    ///   <item><see cref="ThemeMode.Auto"/> → defers to
    ///         <see cref="DetectDark"/> (the OS setting).</item>
    /// </list>
    /// This is the single decision point used by <see cref="ApplyTo"/>.
    /// </summary>
    public static bool ShouldUseDark() => _mode switch
    {
        ThemeMode.Light => false,
        ThemeMode.Dark => true,
        _ => DetectDark(), // Auto
    };

    /// <summary>
    /// Persist <paramref name="mode"/> as the user's choice and re-apply it to
    /// every open window immediately. Called from the Settings → Appearance
    /// selector. Best-effort: a preferences-write failure still updates the
    /// in-process mode + repaints windows (so the live UI is correct even if
    /// the choice didn't persist to disk), and never throws into the caller.
    /// </summary>
    public static void SetMode(ThemeMode mode)
    {
        _mode = mode;

        // (1) Persist. UserPreferences.Save() fires UserPreferences.Changed,
        // which the shared SettingsContent surfaces also listen to — but theme
        // mode is applied to WINDOWS here directly (the toggle re-hydration in
        // SettingsContent is about its own controls, not window chrome).
        try
        {
            UserPreferences.Current.ThemeMode = (AppThemeMode)mode;
            UserPreferences.Current.Save();
        }
        catch
        {
            // Persistence is best-effort (AtomicJsonWriter contract). Fall
            // through and still repaint so the live session reflects the pick.
        }

        // (2) Re-apply to all open windows on the UI thread.
        ApplyToAllWindows();
    }

    /// <summary>
    /// Call ONCE from <see cref="App.OnStartup"/>. Reads the saved
    /// <see cref="ThemeMode"/>, then wires the app to honour it by (a)
    /// registering a class handler so every window applies the theme on Loaded,
    /// (b) applying immediately to any already-open windows, and (c)
    /// re-evaluating + re-applying when the OS theme flips (which only changes
    /// anything in <see cref="ThemeMode.Auto"/>).
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        // Read the saved mode FIRST so the very first window paint already
        // reflects the user's choice. Best-effort: a prefs failure leaves the
        // Auto default (legacy OS-following behaviour).
        try
        {
            _mode = (ThemeMode)UserPreferences.Current.ThemeMode;
        }
        catch
        {
            _mode = ThemeMode.Auto;
        }

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
            ApplyToAllWindows();

            // (c) Re-apply to all open windows when the OS theme flips. The
            // General category covers the light/dark personalization change.
            // In Light/Dark forced modes ApplyTo ignores the OS value, so the
            // flip is effectively a no-op there; we still re-run it (cheap +
            // idempotent) rather than conditionally unsubscribing, so toggling
            // back to Auto picks up flips without re-wiring.
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

        // Only Auto follows the OS; in forced Light/Dark an OS flip changes
        // nothing, so skip the marshaled repaint entirely.
        if (_mode != ThemeMode.Auto) return;

        try
        {
            Dispatcher? dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return;

            dispatcher.BeginInvoke(new Action(ApplyToAllWindows));
        }
        catch
        {
            // Dispatcher marshaling failed (e.g. shutdown in progress): ignore.
        }
    }

    // View-local surfaces (NOT Windows) that merge SenseEaseTheme.xaml into
    // their OWN resources and therefore need the dark override merged into the
    // same collection — a Window-level merge can't reach them. Held WEAKLY so a
    // surface that's gone out of scope without unregistering never keeps the
    // visual tree alive; dead refs are pruned on each repaint pass.
    private static readonly System.Collections.Generic.List<WeakReference<FrameworkElement>>
        _trackedElements = new();
    private static readonly object _trackedLock = new();

    /// <summary>
    /// Register a view-local surface so the theme is (re)applied to its OWN
    /// resources on every theme change — including a live OS light/dark flip in
    /// Auto mode. Use for controls that merge <c>SenseEaseTheme.xaml</c> into
    /// their own <c>Resources</c> (e.g. <c>SettingsContent</c>). Applies the
    /// current theme immediately. Idempotent (a surface registers at most once),
    /// and pairs with <see cref="UntrackElement"/> on unload to avoid leaks.
    /// </summary>
    public static void TrackElement(FrameworkElement element)
    {
        if (element is null) return;

        lock (_trackedLock)
        {
            // Prune dead refs and skip if already tracked.
            bool already = false;
            for (int i = _trackedElements.Count - 1; i >= 0; i--)
            {
                if (!_trackedElements[i].TryGetTarget(out var t))
                    _trackedElements.RemoveAt(i);
                else if (ReferenceEquals(t, element))
                    already = true;
            }
            if (!already)
                _trackedElements.Add(new WeakReference<FrameworkElement>(element));
        }

        ApplyTo(element);
    }

    /// <summary>Stop tracking a previously-registered surface (call on Unloaded).</summary>
    public static void UntrackElement(FrameworkElement element)
    {
        if (element is null) return;

        lock (_trackedLock)
        {
            for (int i = _trackedElements.Count - 1; i >= 0; i--)
            {
                if (!_trackedElements[i].TryGetTarget(out var t) || ReferenceEquals(t, element))
                    _trackedElements.RemoveAt(i);
            }
        }
    }

    /// <summary>Re-apply the effective theme to every open window AND every
    /// tracked view-local surface. Safe to call repeatedly (ApplyTo is
    /// idempotent). Wrapped so an enumeration/teardown race never throws.</summary>
    private static void ApplyToAllWindows()
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
            // Best-effort; leave windows as-is on failure.
        }

        // Tracked non-window surfaces (e.g. SettingsContent). Snapshot under the
        // lock, prune dead refs, then apply outside the lock.
        try
        {
            System.Collections.Generic.List<FrameworkElement> live = new();
            lock (_trackedLock)
            {
                for (int i = _trackedElements.Count - 1; i >= 0; i--)
                {
                    if (_trackedElements[i].TryGetTarget(out var el))
                        live.Add(el);
                    else
                        _trackedElements.RemoveAt(i);
                }
            }
            foreach (var el in live)
                ApplyTo(el);
        }
        catch
        {
            // Best-effort; leave surfaces as-is on failure.
        }
    }

    /// <summary>
    /// Idempotently brings window <paramref name="w"/> into sync with the
    /// effective theme (see <see cref="ShouldUseDark"/>): when dark, ensures the
    /// dark-palette dictionary is present in
    /// <c>w.Resources.MergedDictionaries</c> (adds it if absent); when light,
    /// removes it if present. Wrapped in try/catch so a registry/IO/URI failure
    /// just leaves the light default and never throws.
    /// </summary>
    public static void ApplyTo(Window w)
    {
        if (w is null) return;
        ApplyTo(w.Resources);
    }

    /// <summary>
    /// Idempotently brings an arbitrary <paramref name="element"/>'s own
    /// resources into sync with the effective theme. Use this for surfaces that
    /// merge <c>SenseEaseTheme.xaml</c> into their OWN
    /// <c>FrameworkElement.Resources</c> (e.g. <c>SettingsContent</c>): a
    /// view-local light dictionary shadows the dark palette merged at the Window
    /// level, so those elements must merge the dark override into their own
    /// collection too. Delegates to the same merge/unmerge core as
    /// <see cref="ApplyTo(Window)"/>; the dark palette redefines brushes only,
    /// so the light dictionary's non-colour tokens (radius, elevation,
    /// typography) keep resolving from underneath.
    /// </summary>
    public static void ApplyTo(FrameworkElement element)
    {
        if (element is null) return;
        ApplyTo(element.Resources);
    }

    /// <summary>Shared merge/unmerge core: ensures the dark palette is present
    /// in <paramref name="resources"/>' merged dictionaries when dark is in
    /// effect and absent otherwise. Idempotent and self-contained (never throws
    /// into the WPF message loop).</summary>
    private static void ApplyTo(ResourceDictionary resources)
    {
        if (resources is null) return;

        try
        {
            var merged = resources.MergedDictionaries;
            ResourceDictionary? existing = FindDarkPalette(merged);

            if (ShouldUseDark())
            {
                // Ensure present (add only if missing — idempotent). Added LAST
                // so it sits ABOVE any light SenseEaseTheme.xaml merged earlier
                // in the same collection and wins the brush-key lookup.
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
            // surface on its light default. Never throw.
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
