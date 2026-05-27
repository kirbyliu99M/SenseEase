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
    public void Defaults_WhenNoFileExists_AreToggleA_OFF_ToggleB_ON()
    {
        var p = UserPreferences.Load();
        Assert.False(p.AutoExpandNotifications);  // Toggle A default OFF (user decision)
        Assert.True(p.BarTrayColorEnabled);        // Toggle B default ON
        Assert.False(p.VestibularWarningShown);    // never shown yet
        Assert.Equal(1, p.SchemaVersion);
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
        Assert.Equal(1, read.SchemaVersion);
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
        Assert.True(reread.BarTrayColorEnabled);
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

        Assert.Equal(1, p.SchemaVersion);
        Assert.False(p.AutoExpandNotifications);  // defaults, NOT the file's true
        Assert.True(p.BarTrayColorEnabled);        // defaults, NOT the file's false
    }

    [Fact]
    public void Load_MalformedFile_FallsBackToDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UserPreferences.FilePath)!);
        File.WriteAllText(UserPreferences.FilePath, "{ not valid json");

        var p = UserPreferences.Load();

        Assert.False(p.AutoExpandNotifications);
        Assert.True(p.BarTrayColorEnabled);
    }

    [Fact]
    public void Load_EmptyFile_FallsBackToDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UserPreferences.FilePath)!);
        File.WriteAllText(UserPreferences.FilePath, "");

        var p = UserPreferences.Load();

        Assert.False(p.AutoExpandNotifications);
        Assert.True(p.BarTrayColorEnabled);
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
