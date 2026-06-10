using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Unit tests for <see cref="PathRedactor"/>. Guards the SEC-01 requirement
/// that Windows user-profile name segments are replaced with a safe placeholder
/// before any log string is persisted, without ever throwing.
/// </summary>
public class PathRedactorTests
{
    // -------------------------------------------------------------------------
    // Backslash paths (canonical Windows form)
    // -------------------------------------------------------------------------

    [Fact]
    public void Redact_BackslashPath_ReplacesUserName()
    {
        // Typical crash-log entry: absolute Windows path through the user profile.
        const string input    = @"at System.IO.File.ReadAllText(C:\Users\Kirby Liu\AppData\Local\SenseEase\prefs.json)";
        const string expected = @"at System.IO.File.ReadAllText(C:\Users\<redacted>\AppData\Local\SenseEase\prefs.json)";

        Assert.Equal(expected, PathRedactor.Redact(input));
    }

    [Fact]
    public void Redact_BackslashPath_PreservesTrailingSeparator()
    {
        // Verify that the remainder of the path after the account name is intact.
        const string input    = @"C:\Users\Alice\Desktop\file.txt";
        const string expected = @"C:\Users\<redacted>\Desktop\file.txt";

        Assert.Equal(expected, PathRedactor.Redact(input));
    }

    [Fact]
    public void Redact_BackslashPath_CaseInsensitiveUsersSegment()
    {
        // "users" all-lowercase must still be redacted; the segment name in the
        // output is the replacement literal "Users" (canonical casing), which is
        // acceptable — the PII concern is the account name, not the "Users" label.
        const string input    = @"C:\users\bob\AppData\Local\crash.log";
        const string expected = @"C:\Users\<redacted>\AppData\Local\crash.log";

        Assert.Equal(expected, PathRedactor.Redact(input));
    }

    // -------------------------------------------------------------------------
    // Forward-slash paths
    // -------------------------------------------------------------------------

    [Fact]
    public void Redact_ForwardSlashPath_ReplacesUserName()
    {
        // Forward-slash form (e.g. from a third-party library or cross-platform stack trace).
        // The "Users\<redacted>" segment uses canonical backslashes in the replaced part;
        // the leading and trailing separators from the original string are preserved.
        const string input    = "Error reading /Users/kirby/Documents/config.json";
        const string expected = @"Error reading /Users\<redacted>/Documents/config.json";

        Assert.Equal(expected, PathRedactor.Redact(input));
    }

    [Fact]
    public void Redact_MixedSlashPath_Handled()
    {
        // Mixed separators in exception messages are not unheard-of.
        // Leading separator before "Users" and trailing separator after the account name
        // are each preserved from the original; the "Users\<redacted>" middle is canonical.
        const string input    = @"C:/Users/Kirby Liu\AppData\Local\SenseEase";
        const string expected = @"C:/Users\<redacted>\AppData\Local\SenseEase";

        Assert.Equal(expected, PathRedactor.Redact(input));
    }

    // -------------------------------------------------------------------------
    // Lines without a user path (must be returned unchanged)
    // -------------------------------------------------------------------------

    [Fact]
    public void Redact_LineWithNoPath_ReturnsUnchanged()
    {
        const string input = "Application started normally at 2026-06-11T08:00:00Z";

        Assert.Equal(input, PathRedactor.Redact(input));
    }

    [Fact]
    public void Redact_DiagnosticLineWithNoUserSegment_ReturnsUnchanged()
    {
        // A typical WgcDiagLog entry that has no user path.
        const string input = "[2026-06-11T08:00:00Z] [App.OnStartup] entry, AUMID='SenseEase.App.Desktop' not set";

        Assert.Equal(input, PathRedactor.Redact(input));
    }

    // -------------------------------------------------------------------------
    // Null / empty safety
    // -------------------------------------------------------------------------

    [Fact]
    public void Redact_Null_ReturnsEmptyString()
    {
        // null → empty (never throws, consistent with "never throw" contract).
        Assert.Equal(string.Empty, PathRedactor.Redact(null));
    }

    [Fact]
    public void Redact_EmptyString_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, PathRedactor.Redact(string.Empty));
    }

    // -------------------------------------------------------------------------
    // Multiple occurrences in one line
    // -------------------------------------------------------------------------

    [Fact]
    public void Redact_MultiplePaths_AllRedacted()
    {
        // A stack trace can embed more than one user path in the same string.
        const string input =
            @"Inner: C:\Users\Alice\crash.log Outer: C:\Users\Bob\crash.log";
        const string expected =
            @"Inner: C:\Users\<redacted>\crash.log Outer: C:\Users\<redacted>\crash.log";

        Assert.Equal(expected, PathRedactor.Redact(input));
    }
}
