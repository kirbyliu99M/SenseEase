using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SenseEase.App.Views;

/// <summary>
/// Attached property that renders a small subset of Markdown into a TextBlock's
/// Inlines without taking a dependency on a third-party Markdown library.
///
/// Supported subset (deliberately narrow — large enough for chatbot replies that
/// emit bold/italic/code/lists, small enough to stay obviously correct):
///   **bold**         → Bold
///   *italic*  _italic_ → Italic
///   `inline code`    → Run with monospace face + faint background
///   - item / * item  → "•" + item, line-wrapped
///   \n               → hard LineBreak
///
/// Anything that doesn't match falls through as a plain Run, so unrecognised
/// markdown shows verbatim instead of being silently dropped.
/// </summary>
public static class MarkdownInlines
{
    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.RegisterAttached(
            "Source",
            typeof(string),
            typeof(MarkdownInlines),
            new PropertyMetadata(null, OnSourceChanged));

    public static void SetSource(DependencyObject element, string value)
        => element.SetValue(SourceProperty, value);

    public static string? GetSource(DependencyObject element)
        => (string?)element.GetValue(SourceProperty);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        string? text = e.NewValue as string;

        // Selectable chat bubbles (2026-06-08): a WPF TextBlock can't be selected/
        // copied, so the chat surface uses read-only TextBoxes. On a TextBox we render
        // a marker-stripped PLAIN version (the light markdown — **bold**, `code`,
        // - bullets — reduces to clean selectable text; single * / _ are left verbatim
        // so technical terms like behavior_log aren't mangled).
        if (d is TextBox tbx)
        {
            tbx.Text = ToPlainText(text);
            return;
        }

        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        if (string.IsNullOrEmpty(text)) return;

        foreach (var inline in Parse(text!))
            tb.Inlines.Add(inline);
    }

    /// <summary>Strip the supported markdown markers to clean plain text for a
    /// selectable <see cref="TextBox"/>: drops ** (bold) and ` (code) markers and
    /// turns leading "- " / "* " list markers into "• ". Single * / _ are left as-is.</summary>
    private static string ToPlainText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        string[] lines = text!.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder(text!.Length);
        for (int li = 0; li < lines.Length; li++)
        {
            string line = lines[li];
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                string indent = line.Substring(0, line.Length - trimmed.Length);
                line = indent + "• " + trimmed.Substring(2);
            }
            line = line.Replace("**", string.Empty).Replace("`", string.Empty);
            sb.Append(line);
            if (li < lines.Length - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    private static IEnumerable<Inline> Parse(string text)
    {
        // Split into lines first so list markers and \n line breaks behave.
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        for (int li = 0; li < lines.Length; li++)
        {
            string line = lines[li];
            string trimmed = line.TrimStart();

            // Bullet list: "- foo" or "* foo" at line start (after spaces).
            bool isBullet = trimmed.Length >= 2
                            && (trimmed[0] == '-' || trimmed[0] == '*')
                            && trimmed[1] == ' ';
            if (isBullet)
            {
                yield return new Run("•  ") { FontWeight = FontWeights.Bold };
                foreach (var inl in ParseLine(trimmed[2..]))
                    yield return inl;
            }
            else
            {
                foreach (var inl in ParseLine(line))
                    yield return inl;
            }

            if (li < lines.Length - 1)
                yield return new LineBreak();
        }
    }

    /// <summary>
    /// Inline-level parse: scans for *, **, ` markers and emits Bold/Italic/Run.
    /// Single-pass, no backtracking — unmatched delimiters render as literal text.
    /// </summary>
    private static IEnumerable<Inline> ParseLine(string line)
    {
        if (line.Length == 0) yield break;

        int i = 0;
        var buf = new StringBuilder();

        Run FlushBuf()
        {
            var r = new Run(buf.ToString());
            buf.Clear();
            return r;
        }

        while (i < line.Length)
        {
            char c = line[i];

            // Inline code: `…`
            if (c == '`')
            {
                int end = line.IndexOf('`', i + 1);
                if (end > i)
                {
                    if (buf.Length > 0) yield return FlushBuf();
                    var code = new Run(line[(i + 1)..end])
                    {
                        FontFamily = new FontFamily("Consolas, 'Cascadia Mono', Menlo, monospace"),
                        Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF1, 0xF7)),
                    };
                    yield return code;
                    i = end + 1;
                    continue;
                }
            }

            // Bold: **…**  (must precede the single-* italic check)
            if (c == '*' && i + 1 < line.Length && line[i + 1] == '*')
            {
                int end = line.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 1)
                {
                    if (buf.Length > 0) yield return FlushBuf();
                    yield return new Bold(new Run(line[(i + 2)..end]));
                    i = end + 2;
                    continue;
                }
            }

            // Italic: *…* (any position) or _…_ (word boundaries only).
            // NE26: `_` emphasis must be flanked by non-word chars (or string
            // edges) on BOTH delimiters, so identifiers the chatbot routinely
            // emits — personal_pressure_scale, severe_strike_limit,
            // expires_at_iso, [OBSERVATION_ADD] — stay literal instead of being
            // italicised with their underscores swallowed. `*` keeps intraword
            // emphasis (CommonMark allows it and identifiers don't use `*`).
            if (c == '*' || c == '_')
            {
                int end = line.IndexOf(c, i + 1);
                bool valid = end > i + 1 && !ContainsNewline(line, i + 1, end);
                if (valid && c == '_')
                {
                    bool openBoundary  = i == 0 || !IsWordChar(line[i - 1]);
                    bool closeBoundary = end == line.Length - 1 || !IsWordChar(line[end + 1]);
                    valid = openBoundary && closeBoundary;
                }
                if (valid)
                {
                    if (buf.Length > 0) yield return FlushBuf();
                    yield return new Italic(new Run(line[(i + 1)..end]));
                    i = end + 1;
                    continue;
                }
            }

            buf.Append(c);
            i++;
        }

        if (buf.Length > 0) yield return FlushBuf();
    }

    /// <summary>NE26: a "word" char for underscore-emphasis boundary detection —
    /// letters, digits, and `_` itself (so runs of underscores in identifiers
    /// don't open emphasis). Deliberately ASCII-and-Unicode-letter aware via
    /// char.IsLetterOrDigit so 繁中 identifiers behave too.</summary>
    private static bool IsWordChar(char ch) => char.IsLetterOrDigit(ch) || ch == '_';

    private static bool ContainsNewline(string s, int start, int endExclusive)
    {
        for (int i = start; i < endExclusive; i++)
            if (s[i] == '\n' || s[i] == '\r') return true;
        return false;
    }
}
