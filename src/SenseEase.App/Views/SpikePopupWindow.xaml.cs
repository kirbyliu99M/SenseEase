using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace SenseEase.App.Views;

/// <summary>
/// Non-modal, top-most floating popup that appears when the VIMS pressure spike
/// detector fires. Shows the spike question + 2–4 option cards. Clicking an
/// option fires <see cref="OptionSelected"/> (the caller routes the choice into
/// the chatbot) then closes; clicking ✕ closes without sending anything.
///
/// Positioned top-center on the primary screen (issue #3 fix — previous build
/// only switched tabs which the user found insufficient as a notification).
/// </summary>
public partial class SpikePopupWindow : Window
{
    /// <summary>Raised when the user taps an option card. String is the option label.</summary>
    public event Action<string>? OptionSelected;

    public SpikePopupWindow(string question, string[] options,
        string? headerText = null, string? footerText = null)
    {
        InitializeComponent();
        QuestionText.Text = question ?? string.Empty;
        OptionList.ItemsSource = options ?? Array.Empty<string>();
        // Phase-1 reuse: the contextual FMS check-in supplies its own header/footer copy.
        // Both default to null → keep the XAML's legacy VIMS-spike strings (the chatbot path).
        if (headerText is not null) HeaderText.Text = headerText;
        if (footerText is not null) FooterText.Text = footerText;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Top-center placement on the work area, 60 px from the top so the user
        // sees it land in their peripheral vision rather than over the cursor.
        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - ActualWidth) / 2.0;
        Top  = work.Top + 60;

        // Slide-down + fade-in (~220 ms). Short enough to feel responsive on a
        // real spike, slow enough not to startle.
        var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(220)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(OpacityProperty, fadeIn);
    }

    private void OnOptionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string label } && !string.IsNullOrWhiteSpace(label))
        {
            OptionSelected?.Invoke(label);
        }
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
