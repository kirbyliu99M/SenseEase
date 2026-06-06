using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SenseEase.App.Accessibility;
using SenseEase.App.ViewModels;
using SenseEase.Core.Inference;

namespace SenseEase.App.Views;

/// <summary>
/// Code-behind for <see cref="CalibrationCardView"/>. The XAML uses Click
/// handlers (not Command bindings) for the single-select radio groups on
/// Q1/Q3/Q4 because each option needs to map the radio's Tag value into a
/// specific typed VM property — there is no clean Command-with-parameter
/// route to nullable enums / nullable ints without a custom MarkupExtension.
/// Keeping these handlers as a thin shim keeps the XAML readable and the VM
/// fully unit-testable without any view dependency.
/// </summary>
public partial class CalibrationCardView : UserControl
{
    public CalibrationCardView()
    {
        InitializeComponent();
    }

    private CalibrationCardViewModel? Vm => DataContext as CalibrationCardViewModel;

    private void OnQ1OptionClicked(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if (sender is RadioButton { Tag: UncomfortableSegment seg })
            Vm.Q1Segment = seg;
    }

    private void OnQ3OptionClicked(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        // Tag is a string in XAML; parse to int.
        if (sender is RadioButton { Tag: object tagObj }
            && int.TryParse(tagObj.ToString(), out int level))
        {
            Vm.Q3Intensity = level;
        }
    }

    private void OnQ4OptionClicked(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if (sender is RadioButton { Tag: BaselineComparison cmp })
            Vm.Q4Comparison = cmp;
    }

    // ── M14(c): brief "已儲存 / Saved" confirmation before the card dismisses ──
    //
    // The host clears ActiveCalibrationCard synchronously when CompleteCommand
    // fires, so the card would vanish instantly with no acknowledgement. Instead
    // we surface the SavedOverlay first, then defer CompleteCommand by a short
    // beat so the user sees the confirmation. The decorative fade-in is gated on
    // ReducedMotion() — when motion is minimized the overlay snaps to full
    // opacity instead of fading.
    private const int SavedHoldMs = 650;
    private bool _completing;

    private void OnCompleteClicked(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;

        // Guard against double-invoke (Enter + click, or repeated clicks while
        // the confirmation is up).
        if (_completing) return;
        if (!Vm.CompleteCommand.CanExecute(null)) return;
        _completing = true;

        ShowSavedConfirmation();

        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(SavedHoldMs),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // Re-check: the VM could have changed underneath us.
            if (Vm is { } vm && vm.CompleteCommand.CanExecute(null))
                vm.CompleteCommand.Execute(null);
            // No need to reset _completing — the host dismisses this view.
        };
        timer.Start();
    }

    private void ShowSavedConfirmation()
    {
        if (SavedOverlay is null) return;

        SavedOverlay.Visibility = Visibility.Visible;

        if (AccessibilityHelper.ReducedMotion())
        {
            // Reduced motion: snap to the target opacity, no fade.
            SavedOverlay.BeginAnimation(OpacityProperty, null);
            SavedOverlay.Opacity = 0.96;
            return;
        }

        var fade = new DoubleAnimation
        {
            From = 0.0,
            To = 0.96,
            Duration = TimeSpan.FromMilliseconds(160),
        };
        SavedOverlay.BeginAnimation(OpacityProperty, fade);
    }
}
