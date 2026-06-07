using System.Windows.Controls;

namespace SenseEase.App.Views;

/// <summary>
/// Code-behind for <see cref="CalibrationConfirmCardView"/> (H11). Pure view:
/// the DataContext is a <see cref="ViewModels.CalibrationConfirmCardViewModel"/>
/// and Accept / Decline are Command-bound, so there is no view logic here beyond
/// InitializeComponent.
/// </summary>
public partial class CalibrationConfirmCardView : UserControl
{
    public CalibrationConfirmCardView()
    {
        InitializeComponent();
    }
}
