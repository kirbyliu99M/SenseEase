using System;
using System.IO;
using System.Runtime.CompilerServices;
using SenseEase.App.Configuration;

namespace SenseEase.Tests;

/// <summary>
/// CW-9 (TEST-01/02/03/14) — assembly-wide test-data isolation seam.
///
/// <para>PROBLEM this replaces: the chatbot VM tests used to steer the
/// <c>LOCALAPPDATA</c> env var as a "sandbox", but <c>Environment.GetFolderPath</c>
/// ignores env overrides — so constructing a <c>ChatbotViewModel</c> appended
/// crisis / injection payloads to the developer's REAL
/// <c>%LOCALAPPDATA%\SenseEase\chat_history.jsonl</c>, and <c>UserPreferencesTests</c>
/// deleted / rewrote the REAL <c>user-preferences.json</c> while other classes ran
/// in parallel (the documented "1 failing in full run" flake).</para>
///
/// <para>The fix is <see cref="AppConfig.DataRootOverride"/>: every
/// %LOCALAPPDATA%\SenseEase path (and <c>UserPreferences.FilePath</c>) is computed
/// at access time through one root resolver, so pointing the override at a temp
/// directory redirects ALL test reads/writes off the real user data.</para>
///
/// <para>This <see cref="[ModuleInitializer]"/> runs once, when the test assembly
/// loads, BEFORE any test (or any AppConfig path access) — guaranteed by the CLR.
/// It seeds an assembly-wide unique temp root so even tests that never opt into
/// per-class isolation are safe. Tests that need isolation FROM EACH OTHER call
/// <see cref="UseClassSubdir"/> to retarget the override at a per-class subdir.</para>
/// </summary>
internal static class TestDataRootFixture
{
    /// <summary>The assembly-wide temp root seeded by the module initializer.
    /// Per-class subdirs are created beneath it so every class shares one parent
    /// (easy to reason about, and the OS reclaims %TEMP% on its own schedule).</summary>
    internal static string AssemblyRoot { get; private set; } = string.Empty;

    [ModuleInitializer]
    internal static void Init()
    {
        try
        {
            AssemblyRoot = Path.Combine(
                Path.GetTempPath(),
                "SenseEase_Tests_DataRoot_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(AssemblyRoot);
            // Set BEFORE any AppConfig path property or UserPreferences.FilePath is
            // first read, so no test can touch the real per-user SenseEase folder.
            AppConfig.DataRootOverride = AssemblyRoot;
        }
        catch
        {
            // Never let isolation-setup failure crash the whole assembly's tests.
            // Worst case the override stays null and paths fall back to the OS
            // folder — the same (unisolated) behaviour as before this seam.
        }
    }

    /// <summary>Retargets <see cref="AppConfig.DataRootOverride"/> at a unique
    /// subdir under <see cref="AssemblyRoot"/> for the calling test class, so a
    /// class that deletes/rewrites a shared file (e.g. user-preferences.json)
    /// can't collide with a sibling class. Returns the created directory so the
    /// caller can read back persisted files. Pass a stable <paramref name="label"/>
    /// (typically the test class name) for a readable temp path.</summary>
    internal static string UseClassSubdir(string label)
    {
        string dir = Path.Combine(
            AssemblyRoot.Length > 0 ? AssemblyRoot : Path.GetTempPath(),
            label + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        AppConfig.DataRootOverride = dir;
        return dir;
    }
}
