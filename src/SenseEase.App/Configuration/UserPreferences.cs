using System;
using System.IO;
using System.Text.Json.Serialization;
using SenseEase.Infrastructure.Storage;

namespace SenseEase.App.Configuration;

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
    // defaults + log". Start at 1 since this is the first prefs file.
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Toggle A — auto-expand notification card on tier-up / spike.
    /// Default <c>false</c> per spec §4: passive signal first, active
    /// intervention requires user opt-in.</summary>
    [JsonPropertyName("auto_expand_notifications")]
    public bool AutoExpandNotifications { get; set; } = false;

    /// <summary>Toggle B — bar + tray icon reflect tier with 3-band colour.
    /// Default <c>true</c>. When false, both surfaces stay grey/neutral
    /// regardless of tier (silent visual mode).</summary>
    [JsonPropertyName("bar_tray_color_enabled")]
    public bool BarTrayColorEnabled { get; set; } = true;

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

    // ── Static singleton + persistence ──────────────────────────────────────

    private static UserPreferences? _current;
    private static readonly object _gate = new();

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
    /// a newer file falls back rather than misinterpreting).</summary>
    public static UserPreferences Load()
    {
        var loaded = AtomicJsonWriter.TryRead<UserPreferences>(FilePath);
        if (loaded is null) return new UserPreferences();
        if (loaded.SchemaVersion != 1)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[user-preferences] Unknown schema_version {loaded.SchemaVersion} at {FilePath}; using defaults.");
            return new UserPreferences();
        }
        return loaded;
    }

    /// <summary>Persists the current in-memory snapshot to disk. Best-effort
    /// (matches the AtomicJsonWriter contract); a write failure leaves the
    /// previous file untouched and is silently logged.</summary>
    public void Save()
    {
        AtomicJsonWriter.TryWrite(FilePath, this);
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
