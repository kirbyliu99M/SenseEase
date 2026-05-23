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
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        string? text = e.NewValue as string;
        if (string.IsNullOrEmpty(text)) return;

        foreach (var inline in Parse(text!))
            tb.Inlines.Add(inline);
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

            // Italic: *…* or _…_
            if (c == '*' || c == '_')
            {
                int end = line.IndexOf(c, i + 1);
                if (end > i + 1 && !ContainsNewline(line, i + 1, end))
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

    private static bool ContainsNewline(string s, int start, int endExclusive)
    {
        for (int i = start; i < endExclusive; i++)
            if (s[i] == '\n' || s[i] == '\r') return true;
        return false;
    }
}
