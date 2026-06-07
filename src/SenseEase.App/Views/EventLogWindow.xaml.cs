using System.Windows;
using SenseEase.App.Theme;
using SenseEase.App.ViewModels;

namespace SenseEase.App.Views;

/// <summary>
/// Dedicated Event Log window (UI/UX fine-tune 2026-06-07). The read-only
/// event/diagnostic log was a top-level tab; it now lives in its own window
/// opened from a button in Settings, so it keeps its full-page list without
/// crowding either the primary nav or the Settings scroll.
///
/// <para>DataContext is the shared <see cref="MainViewModel"/> so the
/// <c>{Binding EventLog}</c> markup is identical to the former tab. Theming
/// tracks light/dark via <see cref="ThemeManager.TrackElement"/> (the window
/// merges the light theme view-locally; tracking merges the dark override on
/// top, incl. a live OS flip in Auto mode).</para>
/// </summary>
public partial class EventLogWindow : Window
{
    public EventLogWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) => ThemeManager.TrackElement(this);
        Closed += (_, _) => ThemeManager.UntrackElement(this);
    }

    /// <summary>The "事件 / Action" column fills the remaining list width.
    /// GridView columns can't star-size, so set the last column on every resize.
    /// Time column = 90; subtract it plus the vertical scrollbar and a small fudge
    /// so a row never trips the (disabled) horizontal scrollbar. Mirrors the
    /// handler the former Event Log tab carried.</summary>
    private void OnEventLogListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (EventLogActionColumn is null) return;
        double available = EventLogList.ActualWidth - 90
                           - SystemParameters.VerticalScrollBarWidth - 12;
        if (available > 120) EventLogActionColumn.Width = available;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
