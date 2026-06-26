using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SenseEase.App.Accessibility;
using SenseEase.App.Localization;
using SenseEase.App.Theme;
using SenseEase.Infrastructure.Reporting;

namespace SenseEase.App.Views;

/// <summary>
/// Health-report CARD window (Kirby 2026-06-26): the in-app, one-page replacement for the old
/// file-generating HTML/JSON report. Renders a <see cref="HealthCardData"/> — 3 hero numbers, a calm worded
/// condition badge (Stable teal · Moderate amber · Severe deep-indigo, NEVER a red alarm), and a
/// daily-protection sparkline. NOTHING is written to disk; the data is computed in memory by
/// <see cref="HealthCardDataBuilder"/>. Frameless themed-glass, opened from Settings like the event-log window.
/// </summary>
public partial class HealthReportWindow : Window
{
    public HealthReportWindow(HealthCardData data)
    {
        InitializeComponent();
        Loaded += (_, _) => { try { ThemeManager.TrackElement(this); } catch { /* no Application in tests */ } PlayEntrance(); };
        Closed += (_, _) => { try { ThemeManager.UntrackElement(this); } catch { } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Populate(data ?? HealthCardData.Empty);
    }

    private void Populate(HealthCardData d)
    {
        ProtectedNum.Text = d.TimesProtected.ToString("N0");
        ShieldedNum.Text  = d.SymptomsShielded.ToString("N0");
        ApplyCondition(d.Condition);
        DrawSparkline(d.TrendDailyProtection);
    }

    /// <summary>Calm condition ramp — a worded badge with a low-saturation tint per band. Deliberately NOT a
    /// red alarm (this is a comfort tool): Stable teal, Moderate amber, Severe deep-indigo, Unknown grey.</summary>
    private void ApplyCondition(VimsCondition c)
    {
        bool en = LocalizationManager.Current.IsEnglish;
        (string word, string desc, Color tone) = c switch
        {
            VimsCondition.Stable   => (en ? "Stable"   : "穩定", en ? "Calm and steady — keep it up."   : "平穩舒適，繼續保持。",   Color.FromRgb(0x2F, 0xA9, 0x8F)),
            VimsCondition.Moderate => (en ? "Moderate" : "中等", en ? "Some motion sensitivity lately."  : "近期偶有畫面敏感。",     Color.FromRgb(0xC8, 0x92, 0x2E)),
            VimsCondition.Severe   => (en ? "Severe"   : "明顯", en ? "Frequent discomfort — be gentle." : "較常感到不適，請多休息。", Color.FromRgb(0x5B, 0x53, 0xC4)),
            _                      => (en ? "—"        : "—",   en ? "Not enough data yet."            : "資料尚不足。",          Color.FromRgb(0x9A, 0xA0, 0xB4)),
        };
        ConditionText.Text = word;
        ConditionDesc.Text = desc;
        var solid = new SolidColorBrush(tone);
        ConditionText.Foreground = solid;
        ConditionDot.Fill = solid;
        ConditionBadge.Background = new SolidColorBrush(Color.FromArgb(0x22, tone.R, tone.G, tone.B));
    }

    /// <summary>Draw the daily-protection trend as a soft area + line. Geometry is in a normalized (index ×
    /// inverted-value) space and Stretch=Fill maps it across the panel, so it adapts without measuring width.
    /// A flat / no-data series shows an honest empty line instead of a fabricated chart.</summary>
    private void DrawSparkline(IReadOnlyList<int> trend)
    {
        var vals = trend ?? (IReadOnlyList<int>)Array.Empty<int>();
        int max = vals.Count > 0 ? vals.Max() : 0;
        if (vals.Count < 2 || max <= 0)
        {
            TrendEmpty.Visibility = Visibility.Visible;
            SparkLine.Visibility = SparkFill.Visibility = Visibility.Collapsed;
            return;
        }

        int n = vals.Count;
        const double H = 54;
        Point P(int i) => new(i, H - (vals[i] / (double)max) * H);   // invert Y: higher protection = higher on screen

        var line = new PathFigure { StartPoint = P(0), IsClosed = false };
        for (int i = 1; i < n; i++) line.Segments.Add(new LineSegment(P(i), true));
        SparkLine.Data = new PathGeometry(new[] { line });
        SparkLine.Stretch = Stretch.Fill;

        var area = new PathFigure { StartPoint = new Point(0, H), IsClosed = true };
        for (int i = 0; i < n; i++) area.Segments.Add(new LineSegment(P(i), false));
        area.Segments.Add(new LineSegment(new Point(n - 1, H), false));
        SparkFill.Data = new PathGeometry(new[] { area });
        SparkFill.Stretch = Stretch.Fill;

        // Derive the line + a soft fill from the live Accent token (calm-indigo brand); fall back to indigo.
        var accent = (TryFindResource("Accent") as SolidColorBrush)?.Color ?? Color.FromRgb(0x5B, 0x6C, 0xFF);
        SparkLine.Stroke = new SolidColorBrush(accent);
        SparkFill.Fill = new SolidColorBrush(Color.FromArgb(0x2E, accent.R, accent.G, accent.B));
    }

    private void PlayEntrance()
    {
        bool rm; try { rm = AccessibilityHelper.ReducedMotion(); } catch { rm = false; }
        if (rm) { SlabScale.ScaleX = SlabScale.ScaleY = 1; return; }
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var dur = new Duration(TimeSpan.FromMilliseconds(240));
        SlabScale.ScaleX = SlabScale.ScaleY = 0.97;
        SlabScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, dur) { EasingFunction = ease });
        SlabScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, dur) { EasingFunction = ease });
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
        => RootGrid.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 16, 16);

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { /* DragMove throws outside a left-button-down context — ignore */ }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
