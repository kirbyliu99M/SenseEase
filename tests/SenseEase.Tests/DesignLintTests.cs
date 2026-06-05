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
    // banned royal-blue) to the Accent token. Lower this each time a surface
    // migrates to tokens. (Still present: #024AD8 in SettingsContent /
    // CalibrationCardView, which don't yet merge the theme — next migration.)
    private const int OrphanHexBaseline = 206;

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
