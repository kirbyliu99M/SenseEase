using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace SenseEase.App.Views;

/// <summary>
/// Pop-up sprint Task 1 VALIDATION-ONLY window. Renders a 4×80 px chrome-matched
/// stub at the bar's intended position (top-centre of primary monitor) with a
/// 200 ms red↔cyan adversarial animation, so the WGC pipeline sees the worst-case
/// pixel-delta a bar window can produce. Used to prove (a) the feedback loop
/// exists without mitigation and (b) the Option B pixel-mask in WgcSensingProvider
/// closes it. Created ONLY when env var SENSEEASE_WGC_BAR_VALIDATION=1 is set
/// at app launch — see MainWindow.xaml.cs for the spawn site. Not shipped to
/// production users.
/// </summary>
public partial class BarStubWindow : Window
{
    private readonly DispatcherTimer _stimulusTimer;
    private bool _isRed = true;

    private static readonly SolidColorBrush RedBrush  = MakeFrozen(Color.FromArgb(0xFF, 0xFF, 0x00, 0x00));
    private static readonly SolidColorBrush CyanBrush = MakeFrozen(Color.FromArgb(0xFF, 0x00, 0xFF, 0xFF));

    private static SolidColorBrush MakeFrozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public BarStubWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;

        _stimulusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _stimulusTimer.Tick += (_, _) =>
        {
            _isRed = !_isRed;
            StimulusBorder.Background = _isRed ? RedBrush : CyanBrush;
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Top-centre on the primary monitor's work area. Bar is 80 dip wide ×
        // 4 dip tall; placement matches what the real bar will use in Task 2.
        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - Width) / 2.0;
        Top  = work.Top; // flush with top edge — bar is 4 px so this is visually subtle

        _stimulusTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e) => _stimulusTimer.Stop();

    /// <summary>
    /// Returns the bar's physical-pixel rectangle on the primary monitor for the
    /// WGC mask. Translates WPF DIP coords to physical pixels via the window's
    /// DPI scale. Called after the window is loaded so ActualWidth/ActualHeight
    /// are populated.
    /// </summary>
    public (int Left, int Top, int Width, int Height) GetMonitorRectInPhysicalPixels()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX;
        double sy = dpi.DpiScaleY;
        int left   = (int)Math.Floor(Left * sx);
        int top    = (int)Math.Floor(Top  * sy);
        int width  = (int)Math.Ceiling(ActualWidth  * sx);
        int height = (int)Math.Ceiling(ActualHeight * sy);
        return (left, top, width, height);
    }
}
