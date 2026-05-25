using System.Windows;
using System.Windows.Controls;
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
}
