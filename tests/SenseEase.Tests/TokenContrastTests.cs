using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace SenseEase.Tests;

/// <summary>UX-11: the SenseEaseTheme header USED to claim "All text pairs clear WCAG
/// AA 4.5:1" with nothing enforcing it — and #8A93A3 TextTertiary actually sat at
/// 3.10:1 on white / 2.69:1 on SurfaceMuted. This test replaces that empty promise
/// with an enforced one: it reads the ACTUAL token hex values out of the theme XAML
/// (light = SenseEaseTheme.xaml, dark = Palette.Dark.xaml) and computes the real
/// WCAG 2.x contrast ratio for every declared text-on-surface pair, asserting each
/// clears the 4.5:1 normal-text AA floor. Caption text is the floor case (12px), so
/// 4.5 — not the 3:1 large-text bar — is the right gate.
///
/// Because it parses the live XAML, any future token re-tint that breaks a pair
/// fails HERE instead of silently shipping an unreadable surface.</summary>
public class TokenContrastTests
{
    // Token keys we contrast. Text tokens on each surface token, both palettes.
    private static readonly string[] TextKeys =
        { "TextPrimary", "TextSecondary", "TextTertiary" };
    private static readonly string[] SurfaceKeys =
        { "Surface", "SurfaceMuted" };

    private const double AaNormal = 4.5;

    [Fact]
    public void LightTextOnSurfacePairs_ClearWcagAa()
    {
        var tokens = ParseBrushes(Path.Combine(ThemeDir(), "SenseEaseTheme.xaml"));
        AssertAllPairs(tokens, tokens, "light");
    }

    [Fact]
    public void DarkTextOnSurfacePairs_ClearWcagAa()
    {
        // Dark palette redefines the SAME keys (text + surface) with dark values
        // and is merged ABOVE the light dictionary at runtime, so the dark file is
        // the source of truth for every key the dark pairs touch.
        var dark = ParseBrushes(Path.Combine(ThemeDir(), "Palette.Dark.xaml"));
        AssertAllPairs(dark, dark, "dark");
    }

    [Fact]
    public void WhiteTextOnAccentFill_ClearsWcagAa()
    {
        // Coverage gap closed (2026-06-18): the brand Accent (#4B6EF5 light / #7C8CF8 dark) is for
        // accent IDENTITY (dots, rings, accent text) and is intentionally NOT asserted here — white
        // on it is only 4.33:1 / 3.02:1. White-text primary-button FILLS must use AccentStrong; this
        // guards that token so a future re-tint can't reintroduce an unreadable white-on-fill button.
        foreach (var (file, palette) in new[] { ("SenseEaseTheme.xaml", "light"), ("Palette.Dark.xaml", "dark") })
        {
            var tokens = ParseBrushes(Path.Combine(ThemeDir(), file));
            Assert.True(tokens.ContainsKey("TextOnComfort"), $"[{palette}] missing TextOnComfort token.");
            Assert.True(tokens.ContainsKey("AccentStrong"),
                $"[{palette}] missing AccentStrong (the white-text-fill accent token).");
            double ratio = ContrastRatio(tokens["TextOnComfort"], tokens["AccentStrong"]);
            Assert.True(ratio >= AaNormal,
                $"[{palette}] TextOnComfort on AccentStrong contrast {ratio:0.00}:1 is below WCAG AA 4.5:1. " +
                "Deepen AccentStrong until white text on the fill clears 4.5:1.");
        }
    }

    private static void AssertAllPairs(
        IReadOnlyDictionary<string, (int r, int g, int b)> textSource,
        IReadOnlyDictionary<string, (int r, int g, int b)> surfaceSource,
        string palette)
    {
        foreach (var tk in TextKeys)
        {
            Assert.True(textSource.ContainsKey(tk),
                $"[{palette}] missing text token '{tk}' in theme XAML — pair coverage would silently drop.");
            foreach (var sk in SurfaceKeys)
            {
                Assert.True(surfaceSource.ContainsKey(sk),
                    $"[{palette}] missing surface token '{sk}' in theme XAML.");
                double ratio = ContrastRatio(textSource[tk], surfaceSource[sk]);
                Assert.True(ratio >= AaNormal,
                    $"[{palette}] {tk} on {sk} contrast {ratio:0.00}:1 is below WCAG AA 4.5:1. " +
                    "Re-tint the token (darker on light surfaces / lighter on dark) until it clears.");
            }
        }
    }

    // ── WCAG 2.x relative luminance + contrast ratio ──────────────────────────
    private static double Channel(int c)
    {
        double s = c / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    private static double Luminance((int r, int g, int b) c) =>
        0.2126 * Channel(c.r) + 0.7152 * Channel(c.g) + 0.0722 * Channel(c.b);

    private static double ContrastRatio((int r, int g, int b) a, (int r, int g, int b) b)
    {
        double la = Luminance(a), lb = Luminance(b);
        double hi = Math.Max(la, lb), lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    // ── XAML brush parsing (only #RRGGBB / #AARRGGBB SolidColorBrush keys) ─────
    private static readonly Regex BrushRe = new(
        "<SolidColorBrush\\s+x:Key=\"(?<key>[^\"]+)\"\\s+Color=\"#(?<hex>[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})\"",
        RegexOptions.Compiled);

    private static Dictionary<string, (int r, int g, int b)> ParseBrushes(string path)
    {
        Assert.True(File.Exists(path), "Expected theme file not found: " + path);
        var map = new Dictionary<string, (int, int, int)>(StringComparer.Ordinal);
        foreach (Match m in BrushRe.Matches(File.ReadAllText(path)))
        {
            string hex = m.Groups["hex"].Value;
            if (hex.Length == 8) hex = hex.Substring(2); // drop alpha (AARRGGBB → RRGGBB)
            int r = int.Parse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int g = int.Parse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int b = int.Parse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            map[m.Groups["key"].Value] = (r, g, b);
        }
        return map;
    }

    private static string ThemeDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "SenseEase.App", "Theme");
            if (File.Exists(Path.Combine(candidate, "SenseEaseTheme.xaml")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate src/SenseEase.App/Theme from " + AppContext.BaseDirectory);
    }
}
