using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using SenseEase.App.Configuration;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Pop-up sprint Task 5 — round-trip + reset + fallback tests for
/// the persisted user-preferences file. Touches the real
/// %LocalAppData%/SenseEase/user-preferences.json path because the loader
/// hardcodes it; tests back up + restore around each run so the user's
/// production prefs are never disturbed.</summary>
public class UserPreferencesTests : IDisposable
{
    private readonly string _backupPath;
    private readonly bool   _hadOriginal;

    public UserPreferencesTests()
    {
        // Back up the real prefs file if present so the test never destroys
        // user data on the test machine.
        _backupPath = UserPreferences.FilePath + ".test-backup";
        _hadOriginal = File.Exists(UserPreferences.FilePath);
        if (_hadOriginal)
        {
            File.Copy(UserPreferences.FilePath, _backupPath, overwrite: true);
        }
        // Start every test from a known clean slate.
        if (File.Exists(UserPreferences.FilePath)) File.Delete(UserPreferences.FilePath);
        UserPreferences.ResetCacheForTesting();
    }

    public void Dispose()
    {
        // Restore (or delete) so a test machine isn't polluted.
        if (File.Exists(UserPreferences.FilePath)) File.Delete(UserPreferences.FilePath);
        if (_hadOriginal && File.Exists(_backupPath))
        {
            File.Move(_backupPath, UserPreferences.FilePath, overwrite: true);
        }
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
        // fields added). New file writes always use the current schema.
        var p = UserPreferences.Load();
        Assert.False(p.AutoExpandNotifications);  // Toggle A default OFF (user decision)
        Assert.False(p.BarTrayColorEnabled);       // Toggle B default OFF (v0.8 hotfix)
        Assert.False(p.VestibularWarningShown);    // never shown yet
        Assert.Equal(2, p.SchemaVersion);
        // M8 new fields: opt-in privacy (head-tracking OFF), Balanced follow
        // mode, escape hatch on.
        Assert.False(p.HeadTrackingEnabled);
        Assert.Equal("Balanced", p.HeadTrackingFollowMode);
        Assert.True(p.HeadTrackingEscapeHatchEnabled);
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
        };
        written.Save();

        var read = UserPreferences.Load();
        Assert.True(read.AutoExpandNotifications);
        Assert.False(read.BarTrayColorEnabled);
        Assert.True(read.VestibularWarningShown);
        Assert.Equal(2, read.SchemaVersion);
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
        // Hand-write a v99 prefs file. Loader sees schema_version != 1
        // and returns a defaults-only instance instead of misinterpreting.
        Directory.CreateDirectory(Path.GetDirectoryName(UserPreferences.FilePath)!);
        File.WriteAllText(UserPreferences.FilePath,
            """{ "schema_version": 99, "auto_expand_notifications": true, "bar_tray_color_enabled": false }""");

        var p = UserPreferences.Load();

        Assert.Equal(2, p.SchemaVersion);
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

        // Rolled forward to current schema.
        Assert.Equal(2, p.SchemaVersion);
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
}
