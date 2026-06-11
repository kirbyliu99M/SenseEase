using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace SenseEase.Tests;

/// <summary>D0 design-system lint gates (Step 7). Objective guards that protect the
/// token migration: new colour must use Theme tokens, not inline hex. This is a
/// RATCHET — it pins the current orphan-hex count and fails only if it INCREASES,
/// so surfaces can migrate to tokens one at a time while staying green. Lower the
/// baseline as surfaces migrate.</summary>
public class DesignLintTests
{
    // Inline hex colours in App XAML OUTSIDE Theme/ (the token source is exempt).
    // Ratchet: 247 (post D0/D1/D2) → 238 after audit H1 alarm-red (#E54747)
    // retirement → 229 after audit C3 retired all 9 #7B8DB0 (banned token) to
    // TextTertiary → 206 after audit H1 retired all 22 MainWindow #024AD8 (the
    // banned royal-blue) to the Accent token → 204 after the HP-grade "Crisp
    // Cool" refresh → 151 after the palette-consistency sweep migrated the card
    // (#1E5BB0), SpikePopup (dark theme), first-run/calibration banners, Settings
    // and CalibrationCardView (#024AD8) to theme tokens. Lower this each time a
    // surface migrates to tokens → 150 after the final UI-polish wave → 104 after
    // the A-D batch (M3/M14 regroup) retired #0A1F5C→TextPrimary and the banned
    // #7B8DB0→TextTertiary across SettingsContent + CalibrationCardView → 91 after
    // the pop-up two-face compact-bar redesign (fewer, token-driven elements).
    private const int OrphanHexBaseline = 91;

    private static readonly Regex HexColor =
        new(@"#([0-9A-Fa-f]{8}|[0-9A-Fa-f]{6})", RegexOptions.Compiled);

    [Fact]
    public void OrphanHex_DoesNotExceedBaseline()
    {
        string appDir = FindAppDir();
        int count = 0;
        foreach (var file in Directory.EnumerateFiles(appDir, "*.xaml", SearchOption.AllDirectories))
        {
            string norm = file.Replace('\\', '/');
            if (norm.Contains("/Theme/")) continue; // token source — exempt
            if (norm.Contains("/obj/") || norm.Contains("/bin/")) continue; // generated
            foreach (var line in File.ReadLines(file))
                count += HexColor.Matches(line).Count;
        }

        Assert.True(count <= OrphanHexBaseline,
            $"Orphan inline-hex count {count} exceeds the ratchet baseline {OrphanHexBaseline}. " +
            "New colours must use Theme/SenseEaseTheme.xaml tokens, not inline hex. " +
            "If you intentionally migrated a surface and the count DROPPED, set OrphanHexBaseline to the new count.");
    }

    // TEST-12: Zero-tolerance scan for the three explicitly-banned hex values as
    // LIVE assignments in production *.xaml and *.cs files. These colours
    // (#E54747 alarm-red, #024AD8 royal-blue, #7B8DB0 banned-slate) must NEVER
    // appear as colour values outside the Theme/ token declarations.
    //
    // Exemptions:
    //   • Theme/ files — the banned-list comment in SenseEaseTheme.xaml's header
    //     documents these values by name; the declaration is not a usage.
    //   • All comment regions: XAML <!-- ... --> (including multi-line blocks) and
    //     C# // lines. Historical notes referencing retired values are documentation,
    //     not live assignments. XAML stripping removes the entire <!-- ... --> span
    //     before scanning so interior lines of multi-line comments are excluded.
    //   • obj/ and bin/ build artefacts.
    //
    // "Zero live hits" is the invariant: once a banned hex is truly retired from
    // all production surfaces, this gate ensures it stays retired.
    [Fact]
    public void BannedHex_ZeroLiveOccurrencesInProductionCode()
    {
        var bannedPattern = new Regex(
            @"#(?:E54747|024AD8|7B8DB0)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Strip XAML block comments (<!-- ... -->) including multi-line spans.
        var xmlCommentPattern = new Regex(
            @"<!--.*?-->",
            RegexOptions.Compiled | RegexOptions.Singleline);

        // Strip C# line comments (// to end-of-line). Applied line-by-line so
        // we don't accidentally eat string literals that contain "//".
        var csLineCommentPattern = new Regex(
            @"//.*$",
            RegexOptions.Compiled);

        string repoRoot = FindRepoRoot();
        var violations = new System.Collections.Generic.List<string>();

        foreach (string file in Directory.EnumerateFiles(
            Path.Combine(repoRoot, "src"), "*", SearchOption.AllDirectories))
        {
            string norm = file.Replace('\\', '/');
            if (norm.Contains("/obj/") || norm.Contains("/bin/")) continue;

            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext != ".xaml" && ext != ".cs") continue;

            // Theme/ declarations document the banned list — exempt.
            if (norm.Contains("/Theme/")) continue;

            string content = File.ReadAllText(file);

            // For XAML: strip all XML comment blocks before checking.
            if (ext == ".xaml")
            {
                string stripped = xmlCommentPattern.Replace(content, string.Empty);
                if (bannedPattern.IsMatch(stripped))
                    violations.Add($"{file}: contains live banned-hex in non-comment XAML");
                continue;
            }

            // For C#: strip line comments line-by-line, then test each line.
            int lineNum = 0;
            foreach (string line in content.Split('\n'))
            {
                lineNum++;
                string stripped = csLineCommentPattern.Replace(line, string.Empty);
                if (bannedPattern.IsMatch(stripped))
                    violations.Add($"{file}:{lineNum}: {line.TrimStart()}");
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} banned-hex live assignment(s) in production code " +
            "(#E54747, #024AD8, #7B8DB0 must not appear as colour values outside Theme/ declarations):\n" +
            string.Join("\n", violations));
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

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src")) &&
                Directory.Exists(Path.Combine(dir.FullName, "tests")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate repo root (src/ + tests/ siblings) from " + AppContext.BaseDirectory);
    }
}
