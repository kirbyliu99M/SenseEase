using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using SenseEase.App.Configuration;
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
    public void Defaults_WhenNoFileExists_AreToggleA_OFF_ToggleB_OFF()
    {
        // v0.8 hotfix: Toggle B default flipped from ON → OFF. User
        // feedback was that the colour-changing bar reads as disruptive
        // ambient noise. Default behaviour now is grey/neutral bar; user
        // opts in via Settings if they want the tier-colour signal.
        // Head-tracking sprint M8: schema bumped to 2 (new head-tracking
        // fields added). Appearance setting (2026-06-07): schema bumped to 3
        // (new theme_mode field). New file writes always use the current schema.
        var p = UserPreferences.Load();
        Assert.False(p.AutoExpandNotifications);  // Toggle A default OFF (user decision)
        Assert.False(p.BarTrayColorEnabled);       // Toggle B default OFF (v0.8 hotfix)
        Assert.False(p.VestibularWarningShown);    // never shown yet
        Assert.Equal(3, p.SchemaVersion);
        // M8 new fields: opt-in privacy (head-tracking OFF), Balanced follow
        // mode, escape hatch on.
        Assert.False(p.HeadTrackingEnabled);
        Assert.Equal("Balanced", p.HeadTrackingFollowMode);
        Assert.True(p.HeadTrackingEscapeHatchEnabled);
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
            BarTrayColorEnabled     = false,
            VestibularWarningShown  = true,
            ThemeMode               = AppThemeMode.Dark,
        };
        written.Save();

        var read = UserPreferences.Load();
        Assert.True(read.AutoExpandNotifications);
        Assert.False(read.BarTrayColorEnabled);
        Assert.True(read.VestibularWarningShown);
        Assert.Equal(AppThemeMode.Dark, read.ThemeMode);
        Assert.Equal(3, read.SchemaVersion);
    }

    [Fact]
    public void Save_WritesSnakeCaseJson_OnDisk()
    {
        var p = new UserPreferences
        {
            AutoExpandNotifications = true,
            BarTrayColorEnabled     = false,
        };
        p.Save();

        Assert.True(File.Exists(UserPreferences.FilePath));
        string json = File.ReadAllText(UserPreferences.FilePath);
        Assert.Contains("auto_expand_notifications", json);
        Assert.Contains("bar_tray_color_enabled", json);
        Assert.Contains("schema_version", json);
    }

    // ── ResetToDefaults (chatbot-resettable invariant) ──────────────────────

    [Fact]
    public void ResetToDefaults_RestoresDefaults_AndPersists()
    {
        var p = new UserPreferences
        {
            AutoExpandNotifications = true,
            BarTrayColorEnabled     = false,
            VestibularWarningShown  = true,
        };
        p.Save();

        UserPreferences.ResetToDefaults();

        var reread = UserPreferences.Load();
        Assert.False(reread.AutoExpandNotifications);
        Assert.False(reread.BarTrayColorEnabled);   // v0.8 hotfix: default OFF
        Assert.False(reread.VestibularWarningShown);
    }

    [Fact]
    public void ResetToDefaults_AlsoClearsVestibularWarningShown()
    {
        // Reset MUST clear the vestibular-warning-shown flag — a fresh
        // reset puts the user in the same state as a clean install, so
        // the next Toggle B → OFF attempt SHOULD re-prompt the warning.
        var p = new UserPreferences { VestibularWarningShown = true };
        p.Save();

        UserPreferences.ResetToDefaults();

        Assert.False(UserPreferences.Load().VestibularWarningShown);
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
        Assert.False(p.BarTrayColorEnabled);       // defaults (v0.8 hotfix: OFF), NOT the file's value
    }

    [Fact]
    public void Load_MalformedFile_FallsBackToDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UserPreferences.FilePath)!);
        File.WriteAllText(UserPreferences.FilePath, "{ not valid json");

        var p = UserPreferences.Load();

        Assert.False(p.AutoExpandNotifications);
        Assert.False(p.BarTrayColorEnabled);  // v0.8 hotfix: default OFF
    }

    [Fact]
    public void Load_EmptyFile_FallsBackToDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UserPreferences.FilePath)!);
        File.WriteAllText(UserPreferences.FilePath, "");

        var p = UserPreferences.Load();

        Assert.False(p.AutoExpandNotifications);
        Assert.False(p.BarTrayColorEnabled);  // v0.8 hotfix: default OFF
    }

    // ── Head-tracking sprint M8: schema v1 → v2 roll-forward ────────────────

    [Fact]
    public void Load_SchemaV1File_PreservesExistingFields_AndDefaultsNewHeadTrackingFields()
    {
        // A user upgrading from a pre-M8 build has a v=1 prefs file with the
        // original 6 fields (no head-tracking). Loader must:
        //   • Preserve all v=1 fields verbatim (don't reset the user's prior
        //     Toggle A / Toggle B / minimize-to-tray choices).
        //   • Default the new head-tracking fields to the safe opt-in posture.
        //   • Stamp the loaded instance with the current schema version so
        //     the next Save persists as v=2.
        Directory.CreateDirectory(Path.GetDirectoryName(UserPreferences.FilePath)!);
        File.WriteAllText(UserPreferences.FilePath,
            """{ "schema_version": 1, "auto_expand_notifications": true, "bar_tray_color_enabled": true, "vestibular_warning_shown": true, "minimize_to_tray_on_close": false, "run_at_windows_startup": true, "use_new_bar_overlay": true }""");

        var p = UserPreferences.Load();

        // Pre-existing fields preserved.
        Assert.True(p.AutoExpandNotifications);
        Assert.True(p.BarTrayColorEnabled);
        Assert.True(p.VestibularWarningShown);
        Assert.False(p.MinimizeToTrayOnClose);
        Assert.True(p.RunAtWindowsStartup);
        Assert.True(p.UseNewBarOverlay);

        // New M8 fields default to safe-opt-in.
        Assert.False(p.HeadTrackingEnabled);
        Assert.Equal("Balanced", p.HeadTrackingFollowMode);
        Assert.True(p.HeadTrackingEscapeHatchEnabled);

        // Appearance setting field (absent in v=1) defaults to Auto.
        Assert.Equal(AppThemeMode.Auto, p.ThemeMode);

        // Rolled forward to current schema.
        Assert.Equal(3, p.SchemaVersion);
    }

    [Fact]
    public void Save_HeadTrackingFields_WritesSnakeCaseJson_OnDisk()
    {
        var p = new UserPreferences
        {
            HeadTrackingEnabled            = true,
            HeadTrackingFollowMode         = "FullFollow",
            HeadTrackingEscapeHatchEnabled = false,
        };
        p.Save();

        string json = File.ReadAllText(UserPreferences.FilePath);
        Assert.Contains("head_tracking_enabled", json);
        Assert.Contains("head_tracking_follow_mode", json);
        Assert.Contains("head_tracking_escape_hatch_enabled", json);
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
