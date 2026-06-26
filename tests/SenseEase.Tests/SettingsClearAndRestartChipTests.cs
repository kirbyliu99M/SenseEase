using System.IO;
using SenseEase.App.Configuration;
using SenseEase.App.Views;
using Xunit;

namespace SenseEase.Tests;

/// <summary>UX-08 + UX-09 + FEAT-13 (Wave B1b, settings honesty). Two pinned contracts:
///
/// <list type="bullet">
///   <item><b>UX-09 + FEAT-13 — the "clear all my data" path is a COMPLETE clear.</b>
///         <see cref="SettingsContent.ClearAllUserData"/> deletes every content-bearing
///         file the label promises (behaviour log, model trace, chat transcript, legacy
///         calibration history) from a temp DataRoot, and — the Q4.5 invariant — never
///         touches the habituation offset (<c>habituation.json</c>) or the habituation
///         profile FILE, so graded-exposure progress survives a full clear.</item>
///   <item><b>UX-08 — the Restart-needed chip only shows on a genuine pending change.</b>
///         <see cref="SettingsContent.ShouldShowRestartChip"/> is true iff the live
///         head-tracking toggle differs from the app-start baseline, and false when it
///         matches — so the chip no longer lies on every Settings open.</item>
/// </list>
///
/// <para>CW-9 isolation: <see cref="AppConfig"/> paths route through
/// <c>AppConfig.DataRootOverride</c>; this class points it at its own temp subdir and
/// serialises against the other override-retargeting classes via the <c>TestDataRoot</c>
/// collection, so the single static override never races and we never touch the
/// developer's real %LOCALAPPDATA%\SenseEase data.</para></summary>
[Collection("TestDataRoot")]
public class SettingsClearAndRestartChipTests
{
    private static string SeedFile(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    // ── UX-09 + FEAT-13: clear-all deletes the enumerated content-bearing files ──

    [Fact]
    public void ClearAllUserData_DeletesEnumeratedContentBearingFiles()
    {
        TestDataRootFixture.UseClassSubdir(nameof(SettingsClearAndRestartChipTests) + "_clear");

        // The four content-bearing files the label promises to clear.
        string behaviorLog = SeedFile(AppConfig.BehaviorLogPath, "{\"d\":\"2026-06-11\"}\n");
        string modelTrace  = SeedFile(AppConfig.ModelTracePath, "{\"trace\":1}\n");
        string chatHistory = SeedFile(AppConfig.ChatHistoryPath, "{\"role\":\"user\",\"text\":\"hi\"}\n");
        string calibHist   = SeedFile(AppConfig.CalibrationHistoryPath, "{\"q\":1,\"a\":\"yes\"}\n");
        string symptomLog  = SeedFile(AppConfig.SymptomLogPath, "{\"kind\":\"fms_report\",\"value\":10}\n");
        string onsetLog    = SeedFile(AppConfig.SessionOnsetLogPath, "{\"channel\":\"fms\",\"offset_sec\":90}\n");

        SettingsContent.ClearAllUserData();

        Assert.False(File.Exists(behaviorLog), "behaviour log must be deleted");
        Assert.False(File.Exists(modelTrace),  "model diagnostic trace must be deleted");
        Assert.False(File.Exists(chatHistory), "chat transcript must be deleted (UX-09)");
        Assert.False(File.Exists(calibHist),   "legacy calibration history must be deleted (FEAT-13)");
        Assert.False(File.Exists(symptomLog),  "FMS symptom log must be deleted (Beta-1 Phase-1 / H24)");
        Assert.False(File.Exists(onsetLog),    "session-onset log must be deleted (learning-loop Part D / Idea #2)");
    }

    // ── Kirby 2026-06-26: clear-all returns the app to its first-run / welcome stage ──

    [Fact]
    public void ClearAllUserData_ResetsFirstRunFlag_SoWelcomeReturns()
    {
        TestDataRootFixture.UseClassSubdir(nameof(SettingsClearAndRestartChipTests) + "_firstrun");

        // Simulate a user who has already completed onboarding (flag present ⇒ no welcome).
        AppConfig.MarkFirstRunComplete();
        Assert.False(AppConfig.IsFirstRun, "precondition: onboarding completed before the clear");

        SettingsContent.ClearAllUserData();

        // A full clear is a factory reset: the welcome introduction must show again next launch.
        Assert.True(AppConfig.IsFirstRun, "clear-all must drop the first-run flag so the welcome returns");
    }

    // ── UX-09 Q4.5: clear-all leaves the habituation offset / progress intact ──

    [Fact]
    public void ClearAllUserData_LeavesHabituationFilesIntact()
    {
        TestDataRootFixture.UseClassSubdir(nameof(SettingsClearAndRestartChipTests) + "_q45");

        // The graded-exposure progress that MUST survive a full clear:
        //   • habituation.json — the offset file (Q4.5: never cleared).
        //   • habituation_profile.json — the profile FILE must still exist afterwards.
        const string offsetJson  = "{\"offset\":0.42}";
        const string profileJson = "{\"Profile\":\"TYPICAL\",\"VimsTier\":5}";
        string offset  = SeedFile(AppConfig.HabituationStatePath, offsetJson);
        string profile = SeedFile(AppConfig.HabituationProfilePath, profileJson);

        // Also seed the things that SHOULD be cleared, to prove the clear ran.
        SeedFile(AppConfig.BehaviorLogPath, "{\"d\":\"2026-06-11\"}\n");

        SettingsContent.ClearAllUserData();

        // The offset file is untouched, byte-for-byte (the Q4.5 hard invariant).
        Assert.True(File.Exists(offset), "habituation.json (offset) must survive the clear");
        Assert.Equal(offsetJson, File.ReadAllText(offset));

        // The profile file still exists (ClearUserModel truncates observation memory
        // inside it but never deletes the file or the earned offset).
        Assert.True(File.Exists(profile), "habituation_profile.json must survive the clear");

        // And the user-model file the clear DOES target is gone.
        Assert.False(File.Exists(AppConfig.BehaviorLogPath), "behaviour log should have been cleared");
    }

    // ── UX-08: Restart-needed chip pending-change logic ──

    [Fact]
    public void RestartChip_ShownOnlyWhenToggleDiffersFromAppStartValue()
    {
        // App started with head-tracking OFF.
        SettingsContent.SetAppStartHeadTrackingForTesting(false);

        // Toggle still matches the app-start value → no pending restart → chip hidden.
        Assert.False(SettingsContent.ShouldShowRestartChip(currentHeadTracking: false),
            "chip must be HIDDEN when the toggle matches the value the app started with");

        // Toggle flipped away from the app-start value → pending restart → chip shown.
        Assert.True(SettingsContent.ShouldShowRestartChip(currentHeadTracking: true),
            "chip must be SHOWN when the toggle differs from the app-start value");
    }

    [Fact]
    public void RestartChip_FlippingBackToAppStartValueHidesChipAgain()
    {
        // App started with head-tracking ON.
        SettingsContent.SetAppStartHeadTrackingForTesting(true);

        // Flipping it off is a pending change → chip shown.
        Assert.True(SettingsContent.ShouldShowRestartChip(currentHeadTracking: false));

        // Flipping it back ON (the launched value) clears the pending state → chip hidden.
        Assert.False(SettingsContent.ShouldShowRestartChip(currentHeadTracking: true),
            "flipping the toggle back to the app-start value must hide the chip (no fake restart)");
    }
}
