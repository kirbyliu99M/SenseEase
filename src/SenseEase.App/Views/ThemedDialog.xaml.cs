using System.Windows;
using System.Windows.Input;
using System.Windows.Shapes;
using SenseEase.App.Localization;

namespace SenseEase.App.Views;

/// <summary>Severity cue for <see cref="ThemedDialog"/> — tints the leading dot only
/// (calm, never an alarm icon). Error uses muted clay, not loud red (theme red line).</summary>
public enum DialogKind { Question, Warning, Error, Info }

/// <summary>
/// UI/UX (2026-06-09): themed, app-localized replacement for <c>System.Windows.MessageBox</c>.
/// Native MessageBox can't be themed (always native/light chrome) and its buttons follow the
/// OS language, not the app's — so an English message showed 確定/取消. This window themes via
/// the ThemeManager <c>Window.Loaded</c> class handler (dark palette + dark title bar) and
/// merges <c>SenseEaseTheme.xaml</c> view-locally for Light-mode tokens; button labels are
/// picked by the APP language. Use <see cref="Confirm"/> (OK/Cancel → bool) or
/// <see cref="Notify"/> (OK only).
/// </summary>
public partial class ThemedDialog : Window
{
    private ThemedDialog(string message, bool showCancel, DialogKind kind)
    {
        InitializeComponent();
        MessageText.Text = message;

        OkBtn.Content = LocalizationManager.Current.Pick("確定", "OK");
        if (showCancel)
            CancelBtn.Content = LocalizationManager.Current.Pick("取消", "Cancel");
        else
            CancelBtn.Visibility = Visibility.Collapsed;

        // Kind cue: tint the leading dot via a DYNAMIC resource reference (not a resolved
        // brush) so it stays correct across a live dark/light flip. Calm hues only —
        // Error = muted clay (ComfortHigh), never loud red.
        string dotKey = kind switch
        {
            DialogKind.Warning => "ComfortRising",
            DialogKind.Error   => "ComfortHigh",
            _                  => "Accent",
        };
        KindDot.SetResourceReference(Shape.FillProperty, dotKey);
    }

    /// <summary>Themed, app-localized confirmation (OK / Cancel). Returns true on OK.</summary>
    public static bool Confirm(Window? owner, string message, DialogKind kind = DialogKind.Question)
    {
        var dlg = new ThemedDialog(message, showCancel: true, kind) { Owner = owner };
        if (owner is null) dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog() == true;
    }

    /// <summary>Themed, app-localized notice (OK only).</summary>
    public static void Notify(Window? owner, string message, DialogKind kind = DialogKind.Info)
    {
        var dlg = new ThemedDialog(message, showCancel: false, kind) { Owner = owner };
        if (owner is null) dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.ShowDialog();
    }

    private void OnOk(object sender, RoutedEventArgs e)     { DialogResult = true;  Close(); }
    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Esc = cancel/dismiss (standard modal affordance), mirroring the Cancel button.
        if (e.Key == Key.Escape) { e.Handled = true; DialogResult = false; Close(); return; }
        base.OnKeyDown(e);
    }
}
