using System;
using Microsoft.Win32;
using SenseEase.App.Configuration;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// PKG-01 defense-in-depth: AutoStartRegistry empty/whitespace ghost-value handling.
///
/// <para>The 0608/0609 installer wrote SenseEase="" to HKCU\...\Run because the
/// Inno Setup [Registry] entry used ValueType:string + dontcreatekey, which still
/// creates the value when the key already exists. These tests verify the 0610 fix:
/// an empty or whitespace Run-key value is treated as DISABLED and immediately
/// deleted so it never ghost-launches a missing EXE.</para>
///
/// <para>All tests are HKCU-scoped (no elevation), fully reversible, and guarded:
/// the original Run-key value (if any) is backed up in the constructor and
/// restored in Dispose. The tests skip gracefully on machines where the registry
/// key is unavailable.</para>
/// </summary>
public sealed class AutoStartRegistryTests : IDisposable
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName  = "SenseEase";

    // Backup of whatever was in the registry before this test class ran.
    private readonly string? _backup;
    private readonly bool    _hadValue;

    public AutoStartRegistryTests()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        _backup    = key?.GetValue(ValueName) as string;
        _hadValue  = _backup is not null;

        // Start each test from a clean slate — no SenseEase Run value.
        DeleteTestValue();
    }

    public void Dispose()
    {
        // Restore the original state so the test machine is never permanently
        // altered. If the value wasn't present before, ensure it's gone now.
        if (_hadValue && _backup is not null)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.SetValue(ValueName, _backup, RegistryValueKind.String);
            }
            catch { /* best-effort restore */ }
        }
        else
        {
            DeleteTestValue();
        }
    }

    // ── Helper ──────────────────────────────────────────────────────────────

    private static void DeleteTestValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { /* best-effort */ }
    }

    private static void WriteTestValue(string value)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key?.SetValue(ValueName, value, RegistryValueKind.String);
    }

    private static string? ReadRawValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    // ── No-value baseline ────────────────────────────────────────────────────

    [Fact]
    public void IsEnabled_WhenNoValuePresent_ReturnsFalse()
    {
        // Nothing in the registry → disabled.
        Assert.False(AutoStartRegistry.IsEnabled());
    }

    // ── Empty/whitespace ghost-value (0608/0609 installer bug) ───────────────

    [Fact]
    public void IsEnabled_WhenValueIsEmpty_ReturnsFalse()
    {
        // Simulate the 0608/0609 installer writing SenseEase="".
        WriteTestValue(string.Empty);

        Assert.False(AutoStartRegistry.IsEnabled(),
            "Empty Run-key value must be treated as DISABLED (not enabled).");
    }

    [Fact]
    public void IsEnabled_WhenValueIsEmpty_DeletesGhostValue()
    {
        // After IsEnabled() observes the empty value it must purge it so
        // no ghost entry lingers pointing at nothing.
        WriteTestValue(string.Empty);

        AutoStartRegistry.IsEnabled();

        // IsEnabled() must delete the empty Run-key ghost value (PKG-01 defense-in-depth).
        Assert.Null(ReadRawValue());
    }

    [Fact]
    public void IsEnabled_WhenValueIsWhitespaceOnly_ReturnsFalse()
    {
        WriteTestValue("   ");

        Assert.False(AutoStartRegistry.IsEnabled(),
            "Whitespace-only Run-key value must be treated as DISABLED.");
    }

    [Fact]
    public void IsEnabled_WhenValueIsWhitespaceOnly_DeletesGhostValue()
    {
        WriteTestValue("   ");

        AutoStartRegistry.IsEnabled();

        // IsEnabled() must delete a whitespace-only Run-key ghost value.
        Assert.Null(ReadRawValue());
    }

    // ── SetEnabled round-trip ─────────────────────────────────────────────────

    [Fact]
    public void SetEnabled_False_WhenNoValuePresent_ReturnsTrue()
    {
        // Disabling when already disabled must succeed cleanly (idempotent).
        Assert.True(AutoStartRegistry.SetEnabled(false));
    }

    [Fact]
    public void SetEnabled_False_RemovesValue()
    {
        // Manually write a dummy value then disable; the value must be gone.
        WriteTestValue("dummy");

        AutoStartRegistry.SetEnabled(false);

        Assert.Null(ReadRawValue());
    }
}
