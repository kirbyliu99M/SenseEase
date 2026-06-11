using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using SenseEase.App.Configuration;
using SenseEase.App.Localization;
using SenseEase.App.Theme;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Pop-up sprint Task 5 — round-trip + reset + fallback tests for
/// the persisted user-preferences file.
///
/// <para>CW-9 (TEST-02): these tests used to delete/rewrite the developer's REAL
/// %LocalAppData%/SenseEase/user-preferences.json (backing it up + restoring per
/// test) — but because each test mutates that single shared file while other test
/// classes run in PARALLEL, that was the documented "1 failing in full run" flake.
/// Now <c>UserPreferences.FilePath</c> routes through <c>AppConfig.DataRootOverride</c>,
/// so we point it at a unique per-class temp subdir and never touch the real file.
/// The [Collection] serialises this against the other override-retargeting classes
/// so the single static override never races mid-flight.</para></summary>
[Collection("TestDataRoot")]
public class UserPreferencesTests : IDisposable
{
    public UserPreferencesTests()
    {
        // Isolate this class's prefs file into its own temp subdir, then start
        // every test from a known clean slate (no file on disk + no cached
        // singleton). FilePath now resolves under the override, so this never
        // touches the developer's real user-preferences.json.
        TestDataRootFixture.UseClassSubdir(nameof(UserPreferencesTests));
        if (File.Exists(UserPreferences.FilePath)) File.Delete(UserPreferences.FilePath);
        UserPreferences.ResetCacheForTesting();
    }

    public void Dispose()
    {
        // Drop the cached singleton so a later class doesn't observe this class's
        // in-memory prefs. The temp file itself is left for the OS to reclaim with
        // the rest of %TEMP% — it is never the real user data.
        UserPreferences.ResetCacheForTesting();
    }

    // ── Defaults ─────────────────────────────────────────────────────────────

    [Fact]
    public void Defaults_WhenNoFileExists_AreToggleA_OFF()
    {
        // Toggle A (auto-expand) defaults OFF — passive signal first, active
        // intervention requires explicit opt-in. Head-tracking sprint M8: schema
        // bumped to 2 (new head-tracking fields added). Appearance setting
        // (2026-06-07): schema bumped to 3 (new theme_mode field). New file
        // writes always use the current schema.
        // D-10 (2026-06-11): Toggle B (BarTrayColorEnabled) retired — no assertion.
        var p = UserPreferences.Load();
        Assert.False(p.AutoExpandNotifications);  // Toggle A default OFF (user decision)
        // D-14 (2026-06-11): VestibularWarningShown deleted (dead field) — no assertion.
        Assert.Equal(3, p.SchemaVersion);
        // M8 new fields: opt-in privacy (head-tracking OFF), Balanced follow mode.
        Assert.False(p.HeadTrackingEnabled);
        Assert.Equal("Balanced", p.HeadTrackingFollowMode);
        // Appearance default: Auto (follow the Windows system theme).
        Assert.Equal(AppThemeMode.Auto, p.ThemeMode);
    }

    // ── Round-trip ───────────────────────────────────────────────────────────

    [Fact]
    public void Save_ThenLoad_RoundTripsAllFields()
    {
        var written = new UserPreferences
        {
            AutoExpandNotifications = true,
            ThemeMode               = AppThemeMode.Dark,
        };
        written.Save();

        var read = UserPreferences.Load();
        Assert.True(read.AutoExpandNotifications);
        Assert.Equal(AppThemeMode.Dark, read.ThemeMode);
        Assert.Equal(3, read.SchemaVersion);
    }

    [Fact]
    public void Save_WritesSnakeCaseJson_OnDisk()
    {
        var p = new UserPreferences
        {
            AutoExpandNotifications = true,
        };
        p.Save();

        Assert.True(File.Exists(UserPreferences.FilePath));
        string json = File.ReadAllText(UserPreferences.FilePath);
        Assert.Contains("auto_expand_notifications", json);
        Assert.Contains("schema_version", json);
    }

    // ── D-10: tolerant load of an old file carrying the deleted Toggle-B /
    //         escape-hatch keys, and their drop on re-save ────────────────────

    [Fact]
    public void Load_OldFileWithDeletedBarTrayColorAndEscapeHatchKeys_LoadsTolerantly_AndKeysDropOnResave()
    {
        // D-10 (2026-06-11): BarTrayColorEnabled (Toggle B) and
        // HeadTrackingEscapeHatchEnabled were deleted. A prefs file written by an
        // OLDER build still carries "bar_tray_color_enabled" / "head_tracking_escape_hatch_enabled".
        // The current build must:
        //   • load WITHOUT throwing (System.Text.Json ignores unmapped members),
        //   • preserve every still-defined field verbatim (the unknown keys must NOT
        //     reset the file to defaults), and
        //   • drop the dead keys on the next Save (the re-serialised file no longer
        //     contains them, because the properties no longer exist).
        Directory.CreateDirectory(Path.GetDirectoryName(UserPreferences.FilePath)!);
        File.WriteAllText(UserPreferences.FilePath,
            """{ "schema_version": 3, "auto_expand_notifications": true, "bar_tray_color_enabled": true, "head_tracking_escape_hatch_enabled": false, "head_tracking_enabled": true }""");

        var p = UserPreferences.Load();

        // Loaded tolerantly — surviving fields intact, NOT reset to defaults.
        Assert.True(p.AutoExpandNotifications);
        Assert.True(p.HeadTrackingEnabled);
        Assert.Equal(3, p.SchemaVersion);

        // Re-save and confirm the deleted keys are gone from the on-disk JSON.
        p.Save();
        string resaved = File.ReadAllText(UserPreferences.FilePath);
        Assert.DoesNotContain("bar_tray_color_enabled", resaved);
        Assert.DoesNotContain("head_tracking_escape_hatch_enabled", resaved);
        // Sanity: the file is still a real prefs file (a key that DID survive).
        Assert.Contains("auto_expand_notifications", resaved);
    }

    // ── ResetToDefaults (chatbot-resettable invariant) ──────────────────────

    [Fact]
    public void ResetToDefaults_RestoresDefaults_AndPersists()
    {
        var p = new UserPreferences
        {
            AutoExpandNotifications = true,
        };
        p.Save();

        UserPreferences.ResetToDefaults();

        var reread = UserPreferences.Load();
        Assert.False(reread.AutoExpandNotifications);
    }

    [Fact]
    public void ResetToDefaults_RestoresEveryNonDefaultField_AndPersistsToDisk()
    {
        // D-15 (2026-06-11): the Settings "Reset preferences" action wires straight
        // to ResetToDefaults(). Set a spread of non-default preferences across the
        // surfaces the confirm copy enumerates (theme, language, notification +
        // background toggles, HUD position), reset, then assert (a) every field is
        // back at its property-initialiser default AND (b) the reset is persisted to
        // disk (a fresh Load() — not just the cached singleton — reads defaults).
        var p = new UserPreferences
        {
            AutoExpandNotifications = true,
            MinimizeToTrayOnClose   = false,
            RunAtWindowsStartup     = true,
            ThemeMode               = AppThemeMode.Dark,
            LanguageModeRaw         = "English",
            HudLeft                 = 123.0,
            HudTop                  = 456.0,
        };
        p.Save();

        UserPreferences.ResetToDefaults();

        // Drop the cached singleton so the assertions observe the ON-DISK file the
        // reset wrote, proving persistence (not just an in-memory swap).
        UserPreferences.ResetCacheForTesting();
        var reread = UserPreferences.Load();

        Assert.False(reread.AutoExpandNotifications);          // default OFF
        Assert.True(reread.MinimizeToTrayOnClose);             // default ON
        Assert.False(reread.RunAtWindowsStartup);              // default OFF
        Assert.Equal(AppThemeMode.Auto, reread.ThemeMode);     // default Auto
        Assert.Equal("ChineseTraditional", reread.LanguageModeRaw); // default zh-Hant
        Assert.Null(reread.HudLeft);                           // default unset
        Assert.Null(reread.HudTop);                            // default unset
        Assert.Equal(3, reread.SchemaVersion);
    }

    // ── D-15 wiring: the Settings "Reset preferences" post-reset sequence ───

    [Fact]
    public void ResetPreferences_WiringSequence_RestoresThemeAndLanguageManagersToDefault()
    {
        // D-15 (2026-06-11): SettingsContent.OnResetPreferencesClicked, after the
        // confirm, runs exactly this sequence — ResetToDefaults() then re-applies the
        // reset theme + language to their live managers so the session reflects the
        // reset without a restart. This test pins that wiring contract at the
        // manager/data layer (no WPF dialog): start from non-default theme + language,
        // run the sequence, and assert BOTH the persisted prefs AND the live managers
        // land back at defaults. ThemeManager.SetMode is headless-safe (no
        // Application.Current → its window-repaint pass is a no-op) and still persists.
        var startLang = LocalizationManager.Current.Language;
        var startMode = ThemeManager.CurrentMode;
        try
        {
            // Arrange a non-default starting state, mirroring a user who changed both.
            ThemeManager.SetMode(ThemeManager.ThemeMode.Dark);          // persists Dark
            LocalizationManager.Current.Language = UILanguage.English;
            UserPreferences.Current.LanguageModeRaw = "English";
            UserPreferences.Current.Save();
            Assert.Equal(AppThemeMode.Dark, UserPreferences.Current.ThemeMode);

            // Act — the handler's exact post-confirm sequence.
            UserPreferences.ResetToDefaults();
            ThemeManager.SetMode((ThemeManager.ThemeMode)UserPreferences.Current.ThemeMode);
            LocalizationManager.Current.Language =
                LocalizationManager.Parse(UserPreferences.Current.LanguageModeRaw);

            // Assert — persisted prefs back at defaults …
            UserPreferences.ResetCacheForTesting();
            var reread = UserPreferences.Load();
            Assert.Equal(AppThemeMode.Auto, reread.ThemeMode);
            Assert.Equal("ChineseTraditional", reread.LanguageModeRaw);

            // … and the LIVE managers the handler nudged are back at defaults too.
            Assert.Equal(ThemeManager.ThemeMode.Auto, ThemeManager.CurrentMode);
            Assert.Equal(UILanguage.ChineseTraditional, LocalizationManager.Current.Language);
        }
        finally
        {
            // Restore the process-wide singletons so this test never leaks language /
            // theme state into a peer test (both are static process-wide singletons).
            LocalizationManager.Current.Language = startLang;
            ThemeManager.SetMode(startMode);
        }
    }

    // ── Schema-mismatch fallback ─────────────────────────────────────────────

    [Fact]
    public void Load_UnknownSchemaVersion_FallsBackToDefaults()
    {
        // Hand-write a v99 prefs file. Loader sees schema_version > Current
        // and returns a defaults-only instance instead of misinterpreting.
        Directory.CreateDirectory(Path.GetDirectoryName(UserPreferences.FilePath)!);
        File.WriteAllText(UserPreferences.FilePath,
            """{ "schema_version": 99, "auto_expand_notifications": true, "bar_tray_color_enabled": false }""");

        var p = UserPreferences.Load();

        Assert.Equal(3, p.SchemaVersion);
        Assert.False(p.AutoExpandNotifications);  // defaults, NOT the file's true
    }

    [Fact]
    public void Load_MalformedFile_FallsBackToDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UserPreferences.FilePath)!);
        File.WriteAllText(UserPreferences.FilePath, "{ not valid json");

        var p = UserPreferences.Load();

        Assert.False(p.AutoExpandNotifications);
    }

    [Fact]
    public void Load_EmptyFile_FallsBackToDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UserPreferences.FilePath)!);
        File.WriteAllText(UserPreferences.FilePath, "");

        var p = UserPreferences.Load();

        Assert.False(p.AutoExpandNotifications);
    }

    // ── Head-tracking sprint M8: schema v1 → v2 roll-forward ────────────────

    [Fact]
    public void Load_SchemaV1File_PreservesExistingFields_AndDefaultsNewHeadTrackingFields()
    {
        // A user upgrading from a pre-M8 build has a v=1 prefs file with the
        // original 6 fields (no head-tracking). Loader must:
        //   • Preserve all v=1 fields verbatim (don't reset the user's prior
        //     Toggle A / minimize-to-tray choices).
        //   • Default the new head-tracking fields to the safe opt-in posture.
        //   • Stamp the loaded instance with the current schema version so
        //     the next Save persists as v=2.
        // D-14/D-10 note: the JSON intentionally still carries the now-DELETED
        // "vestibular_warning_shown" and "bar_tray_color_enabled" keys — an old file
        // in the wild has them. The load must stay tolerant (System.Text.Json ignores
        // unmapped members), so the unknown keys neither throw nor reset the surviving
        // fields. See the dedicated tolerant-load tests below.
        Directory.CreateDirectory(Path.GetDirectoryName(UserPreferences.FilePath)!);
        File.WriteAllText(UserPreferences.FilePath,
            """{ "schema_version": 1, "auto_expand_notifications": true, "bar_tray_color_enabled": true, "vestibular_warning_shown": true, "minimize_to_tray_on_close": false, "run_at_windows_startup": true, "use_new_bar_overlay": true }""");

        var p = UserPreferences.Load();

        // Pre-existing (still-defined) fields preserved; the deleted
        // vestibular_warning_shown / bar_tray_color_enabled keys are silently ignored.
        Assert.True(p.AutoExpandNotifications);
        Assert.False(p.MinimizeToTrayOnClose);
        Assert.True(p.RunAtWindowsStartup);
        Assert.True(p.UseNewBarOverlay);

        // New M8 fields default to safe-opt-in.
        Assert.False(p.HeadTrackingEnabled);
        Assert.Equal("Balanced", p.HeadTrackingFollowMode);

        // Appearance setting field (absent in v=1) defaults to Auto.
        Assert.Equal(AppThemeMode.Auto, p.ThemeMode);

        // Rolled forward to current schema.
        Assert.Equal(3, p.SchemaVersion);
    }

    // ── D-14: tolerant load of an old file carrying deleted keys ────────────

    [Fact]
    public void Load_OldFileWithDeletedVestibularWarningShownKey_LoadsTolerantly_AndKeyDropsOnResave()
    {
        // D-14 (2026-06-11): VestibularWarningShown was deleted. A prefs file written
        // by an OLDER build still carries "vestibular_warning_shown" (and we throw in
        // an extra unknown key for good measure). The current build must:
        //   • load WITHOUT throwing (System.Text.Json ignores unmapped members),
        //   • preserve every still-defined field verbatim (the unknown key must NOT
        //     reset the file to defaults), and
        //   • drop the dead key on the next Save (the re-serialised file no longer
        //     contains it, because the property no longer exists).
        Directory.CreateDirectory(Path.GetDirectoryName(UserPreferences.FilePath)!);
        File.WriteAllText(UserPreferences.FilePath,
            """{ "schema_version": 3, "auto_expand_notifications": true, "minimize_to_tray_on_close": false, "theme_mode": "Dark", "vestibular_warning_shown": true, "some_future_unknown_key": 42 }""");

        var p = UserPreferences.Load();

        // Loaded tolerantly — surviving fields intact, NOT reset to defaults.
        Assert.True(p.AutoExpandNotifications);
        Assert.False(p.MinimizeToTrayOnClose);
        Assert.Equal(AppThemeMode.Dark, p.ThemeMode);
        Assert.Equal(3, p.SchemaVersion);

        // Re-save and confirm the deleted key is gone from the on-disk JSON.
        p.Save();
        string resaved = File.ReadAllText(UserPreferences.FilePath);
        Assert.DoesNotContain("vestibular_warning_shown", resaved);
        Assert.DoesNotContain("some_future_unknown_key", resaved);
        // Sanity: the file is still a real prefs file (a key that DID survive).
        Assert.Contains("auto_expand_notifications", resaved);
    }

    [Fact]
    public void Save_HeadTrackingFields_WritesSnakeCaseJson_OnDisk()
    {
        var p = new UserPreferences
        {
            HeadTrackingEnabled    = true,
            HeadTrackingFollowMode = "FullFollow",
        };
        p.Save();

        string json = File.ReadAllText(UserPreferences.FilePath);
        Assert.Contains("head_tracking_enabled", json);
        Assert.Contains("head_tracking_follow_mode", json);
        Assert.Contains("FullFollow", json);
    }

    // ── Appearance setting (2026-06-07): theme_mode ─────────────────────────

    [Fact]
    public void Save_ThemeMode_WritesSnakeCaseKey_AndEnumName_OnDisk()
    {
        var p = new UserPreferences { ThemeMode = AppThemeMode.Dark };
        p.Save();

        string json = File.ReadAllText(UserPreferences.FilePath);
        Assert.Contains("theme_mode", json);   // snake_case JSON key
        Assert.Contains("Dark", json);          // canonical enum name as value
    }

    [Fact]
    public void Load_UnknownThemeModeString_FallsBackToAuto_WithoutWipingOtherFields()
    {
        // A hand-edited / future-build file with an unrecognised theme_mode
        // must degrade that ONE field to Auto — not throw inside deserialisation
        // and reset every other preference. The plain-string ThemeModeRaw +
        // tolerant typed ThemeMode getter guarantees this.
        Directory.CreateDirectory(Path.GetDirectoryName(UserPreferences.FilePath)!);
        File.WriteAllText(UserPreferences.FilePath,
            """{ "schema_version": 3, "auto_expand_notifications": true, "theme_mode": "Sepia" }""");

        var p = UserPreferences.Load();

        Assert.Equal(AppThemeMode.Auto, p.ThemeMode);  // unknown → Auto
        Assert.True(p.AutoExpandNotifications);          // other fields intact
    }

    [Fact]
    public void Load_ThemeModeString_IsCaseInsensitive()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UserPreferences.FilePath)!);
        File.WriteAllText(UserPreferences.FilePath,
            """{ "schema_version": 3, "theme_mode": "light" }""");

        Assert.Equal(AppThemeMode.Light, UserPreferences.Load().ThemeMode);
    }

    // ── Current singleton ────────────────────────────────────────────────────

    [Fact]
    public void Current_LazyLoadsOnFirstAccess_ThenCaches()
    {
        // No file → Current returns defaults. Second access returns the
        // same instance (lazy cache).
        UserPreferences.ResetCacheForTesting();

        var first  = UserPreferences.Current;
        var second = UserPreferences.Current;
        Assert.Same(first, second);
    }

    // ── UI Consolidation Sprint M-Phase-1: Changed event ────────────────────
    // Cross-instance UI sync mechanism — when one SettingsContent surface
    // saves a preference change, the static Changed event re-hydrates peer
    // surfaces (Settings tab ↔ SettingsWindow). These tests validate the
    // event mechanism at the data layer; UI propagation is verified by
    // user UVHS at G_final.

    [Fact]
    public void Save_RaisesChangedEvent_ForCrossSurfaceSync()
    {
        bool fired = false;
        EventHandler handler = (_, _) => fired = true;
        UserPreferences.Changed += handler;
        try
        {
            new UserPreferences { AutoExpandNotifications = true }.Save();
        }
        finally { UserPreferences.Changed -= handler; }

        Assert.True(fired, "Save() must raise Changed for Settings tab / SettingsWindow cross-instance sync.");
    }

    [Fact]
    public void ResetToDefaults_RaisesChangedEvent_TransitivelyViaSave()
    {
        bool fired = false;
        EventHandler handler = (_, _) => fired = true;
        UserPreferences.Changed += handler;
        try
        {
            UserPreferences.ResetToDefaults();
        }
        finally { UserPreferences.Changed -= handler; }

        Assert.True(fired, "ResetToDefaults() must raise Changed (via Save()) so cross-surface UI re-hydrates after a chatbot-driven reset.");
    }
}
