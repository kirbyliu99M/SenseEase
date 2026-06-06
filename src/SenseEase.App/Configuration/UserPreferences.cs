using System;
using System.IO;
using System.Text.Json.Serialization;
using SenseEase.Infrastructure.Storage;

namespace SenseEase.App.Configuration;

/// <summary>
/// User-selectable theme mode (Appearance setting, 2026-06-07). Persisted in
/// <see cref="UserPreferences.ThemeMode"/> and consumed by
/// <see cref="Theme.ThemeManager"/>.
/// <list type="bullet">
///   <item><see cref="Auto"/> — follow the Windows light/dark system setting
///         (registry <c>AppsUseLightTheme</c>); live-updates on OS flips.</item>
///   <item><see cref="Light"/> — force light, never dark, regardless of OS.</item>
///   <item><see cref="Dark"/> — force dark, always, regardless of OS.</item>
/// </list>
/// Serialised as its string name (forward-compatible; unknown values fall back
/// to <see cref="Auto"/> on load).
/// </summary>
public enum AppThemeMode
{
    /// <summary>Follow the Windows system light/dark setting (default).</summary>
    Auto,
    /// <summary>Always light, never dark.</summary>
    Light,
    /// <summary>Always dark.</summary>
    Dark,
}

/// <summary>
/// Pop-up sprint Task 5: persisted user preferences. Lives at
/// %LocalAppData%/SenseEase/user-preferences.json. Atomic write via the
/// shared <see cref="AtomicJsonWriter"/> (tmp + File.Replace) so a
/// Process.Kill mid-write leaves the file intact at either the old or
/// the new version, never a half-written state.
///
/// <para>This is the SINGLE canonical preferences home for SenseEase.
/// Future settings (sensing-mode choice, accessibility tunables, etc.)
/// should consolidate here rather than spawning new JSON files. Per
/// user direction 2026-05-26, the previous "don't create new JSON
/// files" rule was relaxed once it was confirmed no preferences home
/// existed — preferences want their own file.</para>
///
/// <para>Chatbot-resettable invariant (user requirement 2026-05-26):
/// <see cref="ResetToDefaults"/> is intentionally public + static so
/// the chatbot can offer "reset my preferences" as an intent without
/// reaching past a service layer. When a future sprint wires the
/// chatbot's intent dispatch to this method, no architectural change
/// is needed here.</para>
///
/// <para>Defaults (per user decisions 2026-05-26 round 2):</para>
/// <list type="bullet">
///   <item><see cref="AutoExpandNotifications"/> = <c>false</c> — Toggle A.
///         Active intervention (auto-expand cards) requires explicit user
///         opt-in. Passive signal (bar/tray colour) is the default.</item>
///   <item><see cref="BarTrayColorEnabled"/> = <c>true</c> — Toggle B.
///         Bar + tray icon reflect tier via the 3-band colour map.</item>
///   <item><see cref="VestibularWarningShown"/> = <c>false</c> — set once
///         when the user accepts the vestibular safety dialog (single-warning
///         rule per spec §4 safety floor).</item>
/// </list>
/// </summary>
public sealed class UserPreferences
{
    // ── Schema versioning ───────────────────────────────────────────────────
    // Following the same pattern as habituation_profile.json — bump on
    // breaking changes; the loader treats unknown versions as "fall back to
    // defaults + log".
    //
    // Schema history:
    //   1 — initial (Pop-up sprint Task 5).
    //   2 — head-tracking sprint M8 adds head_tracking_enabled,
    //       head_tracking_follow_mode, head_tracking_escape_hatch_enabled.
    //       Old v=1 files load cleanly (missing fields default to the
    //       Roll forward strategy below).
    //   3 — Appearance setting (2026-06-07) adds theme_mode. Old v=1/v=2 files
    //       load cleanly; the missing field defaults to "Auto" (follow Windows),
    //       which is the pre-setting behaviour, so upgrading installs keep their
    //       OS-following theme until the user picks Light/Dark explicitly.
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Current canonical schema version this build writes.</summary>
    internal const int CurrentSchemaVersion = 3;

    /// <summary>Toggle A — auto-expand notification card on tier-up / spike.
    /// Default <c>false</c> per spec §4: passive signal first, active
    /// intervention requires user opt-in.</summary>
    [JsonPropertyName("auto_expand_notifications")]
    public bool AutoExpandNotifications { get; set; } = false;

    /// <summary>Toggle B — bar + tray icon reflect tier with 3-band colour.
    /// Default <c>false</c> as of v0.8 hotfix: user feedback was that the
    /// colour-changing bar reads as disruptive ambient noise. Stays grey
    /// out-of-the-box; user can opt in via Settings → Toggle B if they
    /// want the tier signal. Existing UserPreferences.json files keep
    /// their stored value (no migration); only first-launch / wiped
    /// installs see the new default.</summary>
    [JsonPropertyName("bar_tray_color_enabled")]
    public bool BarTrayColorEnabled { get; set; } = false;

    /// <summary>Single-warning flag for the vestibular safety dialog. Set
    /// to <c>true</c> after the user has been shown the "you have a
    /// vestibular diagnosis, are you sure you want to silence colour
    /// signal?" dialog at least once. Subsequent Toggle B → OFF transitions
    /// do NOT re-prompt (no-nagging principle per spec §4).</summary>
    [JsonPropertyName("vestibular_warning_shown")]
    public bool VestibularWarningShown { get; set; } = false;

    /// <summary>Pop-up sprint Task 6 feature-flag dispatcher (spec §6).
    /// Default <c>true</c> = new bar/tray/card path. Setting to
    /// <c>false</c> routes spike events back through the legacy
    /// <see cref="Views.SpikePopupWindow"/> path (tab-switch + chat bubble
    /// + floating popup) and suppresses the bar/tray spawn entirely.
    /// <para>INTENTIONALLY not exposed in the settings UI — emergency
    /// rollback only. Edit the JSON directly if you need to fall back.
    /// Will be retired in a future sprint once the new mode has been
    /// stable for two release cycles.</para></summary>
    [JsonPropertyName("use_new_bar_overlay")]
    public bool UseNewBarOverlay { get; set; } = true;

    /// <summary>Path Z UX iter 5: when true, closing the MainWindow hides
    /// it to the system tray rather than exiting the process. The real
    /// quit path is the tray menu's "退出 SenseEase" item. Default true
    /// because the entire bar + tray + card architecture exists to support
    /// background tray-resident operation.</summary>
    [JsonPropertyName("minimize_to_tray_on_close")]
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>Path Z UX iter 5: when true, a HKCU\Software\Microsoft\
    /// Windows\CurrentVersion\Run registry value is written so SenseEase
    /// auto-launches at Windows login with the <c>--minimized</c> arg.
    /// Read on startup to surface the current registry state in the
    /// Settings UI; <see cref="Save"/> updates the registry value to
    /// match. Default false — opt-in only.</summary>
    [JsonPropertyName("run_at_windows_startup")]
    public bool RunAtWindowsStartup { get; set; } = false;

    // ── Head-tracking sprint M8 (2026-05-30) ────────────────────────────────
    //
    // Three fields surface the head-tracking feature in Settings UI. All read
    // at app startup by App.OnStartup which mirrors them onto the static
    // AppConfig.HeadTracking* props (AppConfig is the per-process source of
    // truth read by MainViewModel / OverlayWindow at runtime). The
    // HeadTrackingEnabled flag is "startup-bound" because the sidecar reader
    // factory + LightSensorProvider bind once at MainViewModel construction;
    // the FollowMode + EscapeHatchEnabled flags are runtime-changeable
    // because OverlayWindow reads them per frame in ApplyHeadPoseToBrush /
    // TriggerEscapeHatch.

    /// <summary>M8: master opt-in for head-tracking. Default <c>false</c>
    /// (privacy + stability — see docs/HEAD-TRACKING-PRIVACY.md §1). When
    /// false the Python sidecar is never spawned and the FOV mask centre
    /// stays at the v0.9 hardcoded (0.5, 0.5). Changes take effect at the
    /// next app launch — the Settings UI annotates the checkbox with
    /// "需重新啟動 / Requires restart" because the IHeadPoseReader and
    /// LightSensorProvider factories run once at MainViewModel construction
    /// and don't watch the flag at runtime.</summary>
    [JsonPropertyName("head_tracking_enabled")]
    public bool HeadTrackingEnabled { get; set; } = false;

    /// <summary>M8: head-tracking follow-mode persistence. String-serialised
    /// for forward-compat ("Balanced" / "FullFollow"). Default "Balanced"
    /// matches AppConfig default (clinical VIMS-mitigation purpose — periphery
    /// always partially dimmed). Unknown values fall back to "Balanced" on
    /// load. Runtime-changeable: OverlayWindow.ApplyHeadPoseToBrush reads the
    /// mode per frame.</summary>
    [JsonPropertyName("head_tracking_follow_mode")]
    public string HeadTrackingFollowMode { get; set; } = "Balanced";

    /// <summary>M8: double-tap-Ctrl temporary-transparency escape hatch.
    /// Default <c>true</c> matches AppConfig default. Runtime-changeable.
    /// NOTE: even with this flag <c>true</c>, the escape hatch no-ops for
    /// users whose habituation profile records a vestibular diagnosis (the
    /// FOV mask is their VIMS protection; opening a 3-second window of 30 %
    /// opacity could surface VIMS in high-motion content). See
    /// OverlayWindow.TriggerEscapeHatch + docs/HEAD-TRACKING-PRIVACY.md §3.4.</summary>
    [JsonPropertyName("head_tracking_escape_hatch_enabled")]
    public bool HeadTrackingEscapeHatchEnabled { get; set; } = true;

    // ── Appearance setting (2026-06-07) ─────────────────────────────────────

    /// <summary>User-selected theme mode, string-serialised for forward-compat
    /// exactly like <see cref="HeadTrackingFollowMode"/> ("Auto" / "Light" /
    /// "Dark"). Default "Auto" (follow the Windows light/dark system setting) —
    /// this matches the pre-setting behaviour, so existing installs and v=1/v=2
    /// files (where the field is absent) keep following the OS until the user
    /// explicitly picks Light or Dark in Settings → 外觀 / Appearance.
    /// <para>A plain string (not an enum-typed property) is used deliberately:
    /// an unrecognised stored value must NOT throw inside deserialisation and
    /// wipe the whole prefs file — it should degrade to Auto. The typed
    /// accessor <see cref="ThemeMode"/> performs that tolerant parse, and
    /// <see cref="Theme.ThemeManager"/> consumes the typed value.</para></summary>
    [JsonPropertyName("theme_mode")]
    public string ThemeModeRaw { get; set; } = nameof(AppThemeMode.Auto);

    /// <summary>Tolerant typed view over <see cref="ThemeModeRaw"/>. Unknown /
    /// malformed strings fall back to <see cref="AppThemeMode.Auto"/> on get;
    /// the setter writes the canonical enum name back to the raw string so the
    /// persisted file stays "Auto"/"Light"/"Dark". Case-insensitive parse.</summary>
    [JsonIgnore]
    public AppThemeMode ThemeMode
    {
        get => Enum.TryParse<AppThemeMode>(ThemeModeRaw, ignoreCase: true, out var m)
            ? m
            : AppThemeMode.Auto;
        set => ThemeModeRaw = value.ToString();
    }

    // ── Static singleton + persistence ──────────────────────────────────────

    private static UserPreferences? _current;
    private static readonly object _gate = new();

    /// <summary>UI Consolidation Sprint M-Phase-1 (2026-05-30): raised after a
    /// successful <see cref="Save"/> (or <see cref="ResetToDefaults"/>, which
    /// calls Save). Lets multiple UI surfaces hosting the shared
    /// <c>SettingsContent</c> UserControl re-hydrate from
    /// <see cref="Current"/> when a peer surface saves a change, so the
    /// Settings tab and SettingsWindow stay visually in sync without
    /// per-instance plumbing.
    /// <para>Fires on whatever thread called Save. All current callers are
    /// UI event handlers (toggle Checked/Unchecked), so subscribers can
    /// safely touch WPF state directly. A future background-thread caller
    /// would need to marshal back to the dispatcher itself.</para></summary>
    public static event EventHandler? Changed;

    /// <summary>The active preferences instance. First access loads from
    /// disk (falling back to defaults if missing/malformed). Thread-safe.</summary>
    public static UserPreferences Current
    {
        get
        {
            if (_current is not null) return _current;
            lock (_gate)
            {
                _current ??= Load();
                return _current;
            }
        }
    }

    /// <summary>Path to the on-disk preferences file. Public so docs +
    /// support tools can reference it; not intended for direct read/write.</summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SenseEase", "user-preferences.json");

    /// <summary>Loads preferences from <see cref="FilePath"/>. Returns a
    /// defaults-only instance if the file is missing OR if the loaded
    /// schema_version is unknown (forward-compat: an older app reading
    /// a newer file falls back rather than misinterpreting).
    ///
    /// <para>Roll-forward strategy (NE17): ANY stored version in the inclusive
    /// range [1, <see cref="CurrentSchemaVersion"/>] loads cleanly and is
    /// migrated forward — new fields from later schema revisions default via
    /// their property initialisers, so an old install upgrading does NOT lose
    /// existing toggle state. On next Save the file is rewritten at the current
    /// schema. Defaults are reserved ONLY for a version newer than this build
    /// can understand (> Current), a version below the first schema (&lt; 1),
    /// or a malformed/absent version (deserialises to 0, i.e. &lt; 1) — this
    /// preserves the safe-on-mismatch posture for genuinely unknown files
    /// without rejecting valid intermediate versions.</para></summary>
    public static UserPreferences Load()
    {
        var loaded = AtomicJsonWriter.TryRead<UserPreferences>(FilePath);
        if (loaded is null) return new UserPreferences();
        if (loaded.SchemaVersion < 1 || loaded.SchemaVersion > CurrentSchemaVersion)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[user-preferences] Unknown schema_version {loaded.SchemaVersion} at {FilePath}; using defaults.");
            return new UserPreferences();
        }
        // Roll any in-range older version → current. Fields introduced by later
        // schema revisions already have safe defaults from property
        // initialisers; just stamp the version so the next Save persists as the
        // current schema.
        if (loaded.SchemaVersion < CurrentSchemaVersion)
            loaded.SchemaVersion = CurrentSchemaVersion;
        return loaded;
    }

    /// <summary>Persists the current in-memory snapshot to disk. Best-effort
    /// (matches the AtomicJsonWriter contract); a write failure leaves the
    /// previous file untouched and is silently logged.</summary>
    public void Save()
    {
        AtomicJsonWriter.TryWrite(FilePath, this);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Resets all preferences back to defaults and persists. The
    /// <see cref="VestibularWarningShown"/> flag is INTENTIONALLY also
    /// cleared so a fresh "reset" leaves the user in the same state as a
    /// new install (subsequent Toggle B → OFF will re-prompt the warning,
    /// which is the right behaviour: reset means "start over").
    ///
    /// <para>Public + static so the chatbot can call this from intent
    /// handlers without reaching past a service layer (user requirement
    /// 2026-05-26). When the chatbot's intent dispatch wires up "reset my
    /// preferences" to this, no architectural change here is needed.</para></summary>
    public static void ResetToDefaults()
    {
        lock (_gate)
        {
            _current = new UserPreferences();
            _current.Save();
        }
    }

    /// <summary>Test-only escape hatch — drops the cached singleton so a
    /// subsequent <see cref="Current"/> access re-loads from disk. Used by
    /// the round-trip test so reads from FilePath produce a fresh instance
    /// instead of the lock-cached one.</summary>
    internal static void ResetCacheForTesting()
    {
        lock (_gate) { _current = null; }
    }
}
