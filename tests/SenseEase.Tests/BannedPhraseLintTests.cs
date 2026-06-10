using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace SenseEase.Tests;

/// <summary>§6 claims-audit CI gate (Alpha master plan M0 / §6.2). Scans the
/// USER-FACING copy for the §6 banned medical-claim vocabulary, EN + 繁中.
///
/// <para>The §6 red line: copy describes the <b>app's behaviour</b>, never the
/// <b>user's improvement</b> — no therapy / rehab / neuroplasticity /
/// desensitization / tolerance / recovery / symptom wording on any user-facing
/// surface. Also banned: duration-of-use claims like "N minutes of (SenseEase)"
/// that imply improvement over time.</para>
///
/// <para>Scope (B1b extension):
/// <list type="bullet">
///   <item>XAML copy attributes (Text/Content/Header/ToolTip/AutomationProperties.*)
///         are checked against the FULL banned list — these are unambiguously
///         user-facing.</item>
///   <item>XAML element inner text (<c>&lt;TextBlock&gt;…&lt;/TextBlock&gt;</c> etc.)
///         is checked against the FULL banned list — element content is equally
///         user-facing. Covers <c>TextBlock</c>, <c>Label</c>, <c>Run</c>,
///         <c>Button</c>, and <c>TextBox</c> inner text.</item>
///   <item>.cs string literals (converter copy, calibration summaries, fault
///         messages, chatbot reply strings) in <c>src/SenseEase.App</c>,
///         <c>src/SenseEase.Core</c>, and <c>src/SenseEase.Infrastructure</c>
///         are checked against the CJK terms + the multi-word EN claim
///         phrases + the EN regex patterns — so technical English in code
///         (e.g. the word "recovery" in a log or comment) cannot false-positive,
///         while real claim copy is still caught.</item>
/// </list>
/// Comments are excluded (only quoted literals / attribute values are scanned).</para>
///
/// <para><b>Core/Infrastructure zh exclusion list.</b>  Input-matcher lexicons
/// in Core/Infrastructure legitimately CONTAIN banned-looking zh terms as
/// <em>keywords to match in user input</em>, not as output copy shown to the
/// user. For example, <c>HabituationProposalLexicon.cs</c> maps the user
/// input word "耐受" to the RESILIENT profile, and <c>FeedbackLexicon.cs</c>
/// contains "沒症狀" as a comfort indicator a user might type. Scanning these
/// files for banned zh would false-positive on vocabulary that is deliberately
/// there for input detection. Excluded files are listed explicitly with
/// rationales in <c>SkipForZhInCoreInfra</c>.</para>
///
/// <para>This is the automated half of §6.2; a manual claims review is the
/// other half.</para></summary>
public class BannedPhraseLintTests
{
    // Single-word roots + phrases — applied to XAML copy (all user-facing).
    // Also applied to XAML element inner text (B1b addition).
    // 'symptom' added per locked lint spec (B1b).
    private static readonly string[] BannedEnglishFull =
    {
        "tolerance", "desensitiz", "neuroplastic", "rehab", "therap",
        "recovery", "recovered", "absorbed", "prevented", "cured",
        "symptom",
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

    // EN regex patterns — applied alongside BannedEnglishPhrases in .cs literal
    // scans and alongside BannedEnglishFull in XAML attribute + element scans.
    // '\d+\s*minutes? of' added per locked lint spec (B1b): catches claims like
    // "30 minutes of SenseEase" / "5 minute of protection".
    private static readonly Regex[] BannedEnglishRegexes =
    {
        new Regex(@"\d+\s*minutes?\s+of\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };

    // ── B1b: XAML element inner-text patterns ─────────────────────────────────
    // Match the text content between opening and closing tags for the named
    // user-visible elements. Does NOT handle nested tags — a simple capture of
    // the text between the pair is sufficient for the copy-only check (nested
    // markup with binding expressions is unlikely to contain banned literals).
    private static readonly Regex XamlElementContent = new(
        @"<(?:TextBlock|Label|Run|Button|TextBox)\b[^>]*>\s*([^<]+?)\s*</(?:TextBlock|Label|Run|Button|TextBox)>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // ── XAML attribute pattern (unchanged from original) ──────────────────────
    private static readonly Regex XamlCopyAttr = new(
        @"(?:Text|Content|Header|ToolTip|AutomationProperties\.Name|AutomationProperties\.HelpText)\s*=\s*""([^""]*)""",
        RegexOptions.Compiled);

    // C# string literal (handles escaped quotes). Good enough to scan copy.
    private static readonly Regex CsStringLiteral = new(
        "\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

    // ── B1b: files to SKIP for zh scanning in Core + Infrastructure ───────────
    // These files are input-matcher lexicons or LLM system-prompt builders.
    // They legitimately CONTAIN banned-looking zh terms as vocabulary-to-match
    // in user input or as LLM instruction examples, NOT as output copy shown to
    // the user. Scanning them for banned zh would false-positive on:
    //   HabituationProposalLexicon.cs — maps user input "耐受" → RESILIENT profile;
    //       "容忍度" appears in MentionsStrikeLimit (user can say "容忍度" to
    //       adjust the strike limit). These are INPUT keywords, not output copy.
    //   FeedbackLexicon.cs — "沒症狀" is a comfort keyword the user might TYPE
    //       (NLU input detection). Output copy is never produced from this list.
    //   MistralChatbotService.cs — builds the LLM system prompt; "症狀" appears
    //       inside an LLM instruction example ("我在週末症狀更嚴重" is a quoted
    //       example of user input, not text shown directly to the user).
    private static readonly HashSet<string> SkipForZhInCoreInfra = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "HabituationProposalLexicon.cs",  // input-matcher: "耐受" / "容忍度" are user-input keywords
        "FeedbackLexicon.cs",             // input-matcher: "沒症狀" is an NLU comfort keyword
        "MistralChatbotService.cs",       // LLM system-prompt builder: "症狀" is inside an LLM example
    };

    [Fact]
    public void UserFacingCopy_HasNoBannedClaimPhrases()
    {
        string appDir  = FindSrcDir("SenseEase.App");
        string coreDir = FindSrcDir("SenseEase.Core");
        string infraDir = FindSrcDir("SenseEase.Infrastructure");

        var violations = new List<string>();

        // ── XAML scan (SenseEase.App only — Core/Infra have no XAML) ────────
        foreach (var file in Directory.EnumerateFiles(appDir, "*.xaml", SearchOption.AllDirectories))
        {
            if (SkipFile(file)) continue;
            string text = File.ReadAllText(file);
            string fileName = Path.GetFileName(file);

            // Attribute values (existing gate).
            foreach (Match m in XamlCopyAttr.Matches(text))
                CheckFull(fileName, m.Groups[1].Value, violations);

            // Element inner text (B1b new gate).
            foreach (Match m in XamlElementContent.Matches(text))
            {
                string inner = m.Groups[1].Value.Trim();
                if (inner.Length == 0) continue;
                // Skip pure binding expressions (no literal text to evaluate).
                if (inner.StartsWith("{", StringComparison.Ordinal)) continue;
                CheckFull(fileName, inner, violations);
            }
        }

        // ── .cs scan: SenseEase.App (original scope) ─────────────────────────
        ScanCsDir(appDir, applyZh: true, skipZhByFilename: false, violations);

        // ── .cs scan: SenseEase.Core (B1b extended scope) ────────────────────
        ScanCsDir(coreDir, applyZh: true, skipZhByFilename: true, violations);

        // ── .cs scan: SenseEase.Infrastructure (B1b extended scope) ──────────
        ScanCsDir(infraDir, applyZh: true, skipZhByFilename: true, violations);

        Assert.True(violations.Count == 0,
            "§6 medical-claim red line — user-facing copy must describe the APP's behaviour, " +
            "never the USER's improvement (no therapy / rehab / tolerance / recovery / " +
            "desensitization / neuroplasticity / symptom wording, no N-minutes-of claims). Found:\n" +
            string.Join("\n", violations));
    }

    /// <summary>Scan all .cs files in <paramref name="dir"/>, applying phrase + regex
    /// bans to every string literal. When <paramref name="skipZhByFilename"/> is true,
    /// files whose filename appears in <see cref="SkipForZhInCoreInfra"/> are exempt
    /// from the zh check (they are input-matcher lexicons, not output copy).</summary>
    private static void ScanCsDir(string dir, bool applyZh, bool skipZhByFilename,
        List<string> violations)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (SkipFile(file)) continue;

            string fileName = Path.GetFileName(file);
            bool skipZhForFile = skipZhByFilename && SkipForZhInCoreInfra.Contains(fileName);

            string[] zhTerms = (applyZh && !skipZhForFile) ? BannedChinese : Array.Empty<string>();

            foreach (Match m in CsStringLiteral.Matches(File.ReadAllText(file)))
            {
                string value = m.Groups[1].Value;
                // Phrase check (multi-word EN + zh).
                Check(fileName, value, BannedEnglishPhrases, zhTerms, violations);
                // Regex check.
                CheckRegex(fileName, value, violations);
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Full check (single-word EN + multi-word + zh + regex) — used for
    /// XAML attribute values and element inner text.</summary>
    private static void CheckFull(string file, string value, List<string> violations)
    {
        Check(file, value, BannedEnglishFull, BannedChinese, violations);
        CheckRegex(file, value, violations);
    }

    private static void Check(string file, string value, string[] en, string[] zh,
        List<string> outv)
    {
        string lower = value.ToLowerInvariant();
        foreach (var bad in en)
            if (lower.Contains(bad)) outv.Add($"{file}: \"{Trunc(value)}\" → banned EN '{bad}'");
        foreach (var bad in zh)
            if (value.Contains(bad)) outv.Add($"{file}: \"{Trunc(value)}\" → banned 繁中 '{bad}'");
    }

    private static void CheckRegex(string file, string value, List<string> outv)
    {
        foreach (var rx in BannedEnglishRegexes)
        {
            var m = rx.Match(value);
            if (m.Success)
                outv.Add($"{file}: \"{Trunc(value)}\" → banned EN pattern '{rx}'");
        }
    }

    private static string Trunc(string s) => s.Length <= 60 ? s : s.Substring(0, 60) + "…";

    private static bool SkipFile(string file)
    {
        string n = file.Replace('\\', '/');
        return n.Contains("/Theme/") || n.Contains("/obj/") || n.Contains("/bin/");
    }

    private static string FindSrcDir(string projectName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "src", projectName);
            if (Directory.Exists(candidate))
            {
                // Verify it's the right project by checking for at least one .cs or .xaml
                if (Directory.GetFiles(candidate, "*.cs", SearchOption.AllDirectories).Length > 0 ||
                    Directory.GetFiles(candidate, "*.xaml", SearchOption.AllDirectories).Length > 0)
                    return candidate;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not locate src/{projectName} from {AppContext.BaseDirectory}");
    }
}
