using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace SenseEase.Tests;

/// <summary>§6 claims-audit CI gate (Alpha master plan M0 / §6.2). Scans the App's
/// USER-FACING copy for the §6 banned medical-claim vocabulary, EN + 繁中.
///
/// <para>The §6 red line: copy describes the <b>app's behaviour</b>, never the
/// <b>user's improvement</b> — no therapy / rehab / neuroplasticity /
/// desensitization / tolerance / recovery wording on any user-facing surface.</para>
///
/// <para>Scope:
/// <list type="bullet">
///   <item>XAML copy attributes (Text/Content/Header/ToolTip/AutomationProperties.*)
///         are checked against the FULL banned list — these are unambiguously
///         user-facing.</item>
///   <item>.cs string literals (converter copy, calibration summaries, fault
///         messages) are checked against the CJK terms + the multi-word EN claim
///         phrases only — so technical English in code (e.g. the word "recovery"
///         in a log or comment) cannot false-positive, while real claim copy is
///         still caught.</item>
/// </list>
/// Comments are excluded (only quoted literals / attribute values are scanned).
/// This is the automated half of §6.2; a manual claims review is the other half.</para></summary>
public class BannedPhraseLintTests
{
    // Single-word roots + phrases — applied to XAML copy (all user-facing).
    private static readonly string[] BannedEnglishFull =
    {
        "tolerance", "desensitiz", "neuroplastic", "rehab", "therap",
        "recovery", "recovered", "absorbed", "prevented", "cured",
        "reduced your", "less sick", "needing this less", "since you started",
        "motion you didn't",
    };

    // Unambiguous multi-word claim phrases — safe to also apply to .cs literals.
    private static readonly string[] BannedEnglishPhrases =
    {
        "reduced your", "less sick", "needing this less", "since you started",
        "motion you didn't",
    };

    // 繁中 banned terms — applied to BOTH XAML and .cs (CJK in code literals is
    // user-facing copy, not technical identifiers).
    private static readonly string[] BannedChinese =
    {
        "耐受", "容忍度", "去敏感", "神經可塑", "復健", "治療",
        "痊癒", "康復", "症狀", "減少了你", "不再需要",
    };

    private static readonly Regex XamlCopyAttr = new(
        @"(?:Text|Content|Header|ToolTip|AutomationProperties\.Name|AutomationProperties\.HelpText)\s*=\s*""([^""]*)""",
        RegexOptions.Compiled);

    // C# string literal (handles escaped quotes). Good enough to scan copy.
    private static readonly Regex CsStringLiteral = new(
        "\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

    [Fact]
    public void UserFacingCopy_HasNoBannedClaimPhrases()
    {
        string appDir = FindAppDir();
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(appDir, "*.xaml", SearchOption.AllDirectories))
        {
            if (Skip(file)) continue;
            foreach (Match m in XamlCopyAttr.Matches(File.ReadAllText(file)))
                Check(Path.GetFileName(file), m.Groups[1].Value, BannedEnglishFull, BannedChinese, violations);
        }

        foreach (var file in Directory.EnumerateFiles(appDir, "*.cs", SearchOption.AllDirectories))
        {
            if (Skip(file)) continue;
            foreach (Match m in CsStringLiteral.Matches(File.ReadAllText(file)))
                Check(Path.GetFileName(file), m.Groups[1].Value, BannedEnglishPhrases, BannedChinese, violations);
        }

        Assert.True(violations.Count == 0,
            "§6 medical-claim red line — user-facing copy must describe the APP's behaviour, " +
            "never the USER's improvement (no therapy / rehab / tolerance / recovery / " +
            "desensitization / neuroplasticity wording). Found:\n" + string.Join("\n", violations));
    }

    private static void Check(string file, string value, string[] en, string[] zh, List<string> outv)
    {
        string lower = value.ToLowerInvariant();
        foreach (var bad in en)
            if (lower.Contains(bad)) outv.Add($"{file}: \"{Trunc(value)}\" → banned EN '{bad}'");
        foreach (var bad in zh)
            if (value.Contains(bad)) outv.Add($"{file}: \"{Trunc(value)}\" → banned 繁中 '{bad}'");
    }

    private static string Trunc(string s) => s.Length <= 60 ? s : s.Substring(0, 60) + "…";

    private static bool Skip(string file)
    {
        string n = file.Replace('\\', '/');
        return n.Contains("/Theme/") || n.Contains("/obj/") || n.Contains("/bin/");
    }

    private static string FindAppDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "SenseEase.App");
            if (Directory.Exists(Path.Combine(candidate, "Theme")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate src/SenseEase.App from " + AppContext.BaseDirectory);
    }
}
