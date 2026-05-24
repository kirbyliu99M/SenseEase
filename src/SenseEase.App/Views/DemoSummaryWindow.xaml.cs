using System;
using System.Windows;

namespace SenseEase.App.Views;

/// <summary>Preset suggestion produced by a setup-demo run. Tiered off the
/// user's pressure response during the calibration clip. All four deltas are
/// applied additively on top of existing personalization when the user clicks
/// Save in the calibration variant of <see cref="DemoSummaryWindow"/>.</summary>
public readonly record struct CalibrationPreset(
    string Tier,
    int FovDelta,
    double MaskDepthDelta,
    double GridVisibilityDelta,
    double FeatherWidthDelta);

/// <summary>
/// Post-demo summary screen. Has two modes:
/// (a) plain summary — just shows the 3 headline stats and a Close button;
/// (b) calibration mode — also shows a suggested preset derived from the
///     observed reactivity, with Save / Skip buttons. Caller reads
///     <see cref="SavedPreset"/> after <c>ShowDialog()</c> returns to know
///     whether to apply the preset.
/// </summary>
public partial class DemoSummaryWindow : Window
{
    /// <summary>True iff the user clicked Save in calibration mode. Always false
    /// in plain summary mode (the Close button doesn't save anything).</summary>
    public bool SavedPreset { get; private set; }

    /// <summary>Plain summary constructor — Demo button or ad-hoc Run from a
    /// user who already completed setup.</summary>
    public DemoSummaryWindow(TimeSpan duration, int maskActivations, double peakPressure)
    {
        InitializeComponent();
        FillStats(duration, maskActivations, peakPressure);
    }

    /// <summary>Calibration-mode constructor — invoked at the end of a
    /// setup-demo run. Shows the same stats PLUS the suggested preset, and
    /// swaps Close for Save / Skip.</summary>
    public DemoSummaryWindow(TimeSpan duration, int maskActivations, double peakPressure,
                             CalibrationPreset preset)
    {
        InitializeComponent();
        FillStats(duration, maskActivations, peakPressure);

        HeaderText.Text       = "校正完成 / Setup complete";
        ExplanatoryText.Text  = "我們已根據您的反應建議下列預設值。儲存後將立即套用 / Based on your reaction, the values below are suggested. Saving applies them immediately.";

        TierText.Text         = $"● {preset.Tier}";
        PresetFovText.Text    = $"{(preset.FovDelta > 0 ? "+" : "")}{preset.FovDelta}%";
        PresetMaskText.Text   = FormatDelta(preset.MaskDepthDelta);
        PresetGridText.Text   = FormatDelta(preset.GridVisibilityDelta);
        PresetFeatherText.Text = FormatDelta(preset.FeatherWidthDelta);

        CalibrationPanel.Visibility = Visibility.Visible;
        SkipButton.Visibility       = Visibility.Visible;
        PrimaryButton.Content       = "Save preset";
    }

    private void FillStats(TimeSpan duration, int maskActivations, double peakPressure)
    {
        DurationText.Text        = $"{duration.TotalSeconds:F0} s";
        MaskActivationsText.Text = maskActivations.ToString();
        PeakPressureText.Text    = peakPressure.ToString("F0");
    }

    private static string FormatDelta(double v)
        => v == 0 ? "—" : $"{(v > 0 ? "+" : "")}{v:0.00}";

    private void OnPrimaryClicked(object sender, RoutedEventArgs e)
    {
        // In calibration mode, primary button == Save. In plain summary mode
        // it's Close; the SavedPreset flag stays false and the caller ignores it.
        SavedPreset = CalibrationPanel.Visibility == Visibility.Visible;
        Close();
    }

    private void OnSkipClicked(object sender, RoutedEventArgs e)
    {
        SavedPreset = false;
        Close();
    }
}
