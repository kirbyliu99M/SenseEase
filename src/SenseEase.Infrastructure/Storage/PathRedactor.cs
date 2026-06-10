using System.Text.RegularExpressions;

namespace SenseEase.Infrastructure.Storage;

/// <summary>
/// SEC-01: Runtime PII guard that replaces Windows user-profile name segments
/// in log strings with a safe placeholder before the strings are persisted.
///
/// <para>Lifted verbatim from the inline H19 regex in <c>App.xaml.cs</c>
/// (<see cref="LogCrash"/> / <see cref="LogStartupDiagnostic"/>). Centralising
/// the logic here lets every crash / diagnostic writer call a single shared
/// entry point rather than each reproducing the regex. <c>App.xaml.cs</c> will
/// swap its own <c>UserPathRedaction</c> field to delegate here later this wave
/// without any observable behaviour change.</para>
///
/// <para><b>Contract.</b></para>
/// <list type="bullet">
///   <item>Both backslash and forward-slash separators are matched.</item>
///   <item>Drive-prefixed paths (<c>C:\Users\Alice\...</c>) and UNC-style paths
///         without a drive letter are both handled.</item>
///   <item>The matched account name is replaced with the literal token
///         <c>&lt;redacted&gt;</c>; the trailing separator is preserved so the
///         remainder of the path remains syntactically valid.</item>
///   <item><see cref="Redact"/> is case-insensitive and <b>never throws</b>.
///         On any unexpected failure it falls back to returning the original
///         string unchanged so callers in crash-handler paths are never
///         disrupted.</item>
/// </list>
///
/// <para>Dependency-free: no Infrastructure / Core types are referenced.</para>
/// </summary>
public static class PathRedactor
{
    // Matches \Users\<name>\ or /Users/<name>/ (and mixed) so both Windows
    // path separators are covered.
    //
    // Pattern breakdown:
    //   ([/\\]) — captured group 1: leading separator before "Users"
    //   Users   — literal segment name (case-insensitive via flag)
    //   [/\\]   — separator after "Users" (consumed, not captured)
    //   [^\\/]+ — one or more chars that are NOT a separator = the account name
    //   ([/\\]) — captured group 2: trailing separator, preserved in output
    //
    // Both groups are re-emitted in the replacement so the path structure
    // around the redacted segment is not distorted (e.g. the leading slash on
    // a forward-slash path stays intact, and the trailing separator that
    // connects to the next path component is preserved).
    private static readonly Regex _userPathPattern = new(
        @"([/\\])Users[/\\][^\\/]+([/\\])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // $1 = leading separator (preserved as-is), $2 = trailing separator.
    // The "Users\<redacted>" segment uses a canonical backslash; the
    // surrounding separators are whatever the original string used.
    private const string Replacement = @"$1Users\<redacted>$2";

    /// <summary>
    /// Returns <paramref name="line"/> with any <c>\Users\&lt;name&gt;\</c> (or
    /// forward-slash equivalent) segment replaced by
    /// <c>\Users\&lt;redacted&gt;\</c>.
    ///
    /// <para>Null or empty input is returned as-is. Any internal exception is
    /// swallowed and the original value is returned so callers inside crash
    /// handlers are never interrupted.</para>
    /// </summary>
    public static string Redact(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return line ?? string.Empty;

        try
        {
            return _userPathPattern.Replace(line, Replacement);
        }
        catch
        {
            // Never throw inside a diagnostic / crash-log path.
            return line;
        }
    }
}
