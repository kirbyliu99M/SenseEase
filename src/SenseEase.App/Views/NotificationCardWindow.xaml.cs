using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SenseEase.App.Views;

/// <summary>
/// Pop-up sprint Task 4: the expanded notification card. Shown when the user
/// clicks the bar or tray icon (Task 4 wiring) or, in a future Task 5 with
/// Toggle A = ON, auto-expanded on a tier-up or spike event.
///
/// <para>Auto-collapse: after <see cref="IdleTimeout"/> seconds (60 by spec)
/// with no MouseMove inside the card, opacity fades from 1.0 → 0.0 over
/// <see cref="FadeDuration"/> seconds (5 by spec), then the window hides. Per
/// spec §2 the fade gives the user a "last-chance pull-back" — if they move
/// the mouse onto the card mid-fade, the animation reverses and the idle
/// timer resets.</para>
///
/// <para>Three buttons + close. Each emits a typed event for the host to
/// handle: <see cref="EngageClicked"/>, <see cref="MuteClicked"/>,
/// <see cref="Closed"/> (standard Window event). All three close the card
/// — close + engage instantly via <see cref="Hide"/>, mute also instantly +
/// the host is expected to record the mute on its
/// <see cref="MuteClicked"/> handler.</para>
///
/// <para>No internal Engage action wiring — that's spec-ambiguous in Task 4
/// scope ("建議動作" varies by tier). The host owns the action; the card
/// just collects the click and goes away.</para>
/// </summary>
public partial class NotificationCardWindow : Window
{
    /// <summary>How long the card sits idle (no MouseMove over the
    /// CardBorder) before the fade-collapse begins. 60 s per spec; tests
    /// shrink this for determinism.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Duration of the opacity 1→0 fade after idle timeout. 5 s
    /// per spec. The fade is reversible — MouseMove during fade snaps
    /// opacity back to 1 and resets the idle timer.</summary>
    public TimeSpan FadeDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Raised on "啟動保護" click. Host runs the actual engage
    /// action; the card hides immediately on click (does NOT wait for the
    /// handler to complete).</summary>
    public event EventHandler? EngageClicked;

    /// <summary>Raised on "6 小時內不再提示" click. Host calls
    /// <c>NotificationCardController.Mute(band)</c> in response. The card
    /// hides immediately on click.</summary>
    public event EventHandler? MuteClicked;

    private readonly DispatcherTimer _idleTimer;
    private Storyboard? _activeFadeStoryboard;

    public NotificationCardWindow()
    {
        InitializeComponent();
        _idleTimer = new DispatcherTimer { Interval = IdleTimeout };
        _idleTimer.Tick += OnIdleTimerTick;
        Loaded += OnLoaded;
        Closed += (_, _) => _idleTimer.Stop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Position just below the bar: top-centre, 12 dip below the bar's
        // bottom edge. Bar is 4 dip tall at y=0 → card top at y=16.
        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - Width) / 2.0;
        Top  = work.Top + 16;

        // Smooth fade-in (~220 ms) so the card doesn't snap on screen.
        Opacity = 0;
        var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(220)));
        BeginAnimation(OpacityProperty, fadeIn);

        // Start the idle countdown. MouseMove on CardBorder restarts it.
        RestartIdleTimer();
    }

    /// <summary>Sets card content for a given VIMS tier band. Called by the
    /// host before <see cref="Show"/>. Title + body are localized zh
    /// (i18n service will replace literals when one lands — same TODO as
    /// the tray service).</summary>
    public void SetContent(TierColorPalette.TierBand band)
    {
        switch (band)
        {
            case TierColorPalette.TierBand.Grey:
                TitleText.Text = "VIMS 狀態：舒緩";
                BodyText.Text  = "目前視覺壓力低。如有疑問請開啟設定面板。";
                break;
            case TierColorPalette.TierBand.Amber:
                TitleText.Text = "VIMS 狀態：中度";
                BodyText.Text  = "偵測到中度視覺壓力。建議短暫休息或暗化螢幕。";
                break;
            case TierColorPalette.TierBand.Red:
                TitleText.Text = "VIMS 狀態：高壓";
                BodyText.Text  = "偵測到高度視覺壓力。建議立即啟動保護或暫停當前活動。";
                break;
        }
    }

    private void OnCardMouseMove(object sender, MouseEventArgs e)
    {
        // Any movement inside the CardBorder counts as user interaction.
        // Reset both the idle timer AND cancel any in-progress fade (the
        // "last-chance pull-back" path).
        if (_activeFadeStoryboard is not null)
        {
            _activeFadeStoryboard.Stop(this);
            _activeFadeStoryboard = null;
            Opacity = 1.0;
        }
        RestartIdleTimer();
    }

    private void RestartIdleTimer()
    {
        _idleTimer.Stop();
        _idleTimer.Interval = IdleTimeout;
        _idleTimer.Start();
    }

    private void OnIdleTimerTick(object? sender, EventArgs e)
    {
        _idleTimer.Stop();
        // Start the fade. Reversible — MouseMove during the fade pulls
        // opacity back to 1 via OnCardMouseMove above.
        var fade = new DoubleAnimation(1.0, 0.0, new Duration(FadeDuration));
        Storyboard.SetTarget(fade, this);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        var sb = new Storyboard();
        sb.Children.Add(fade);
        sb.Completed += (_, _) =>
        {
            // Only hide if the fade actually finished (not interrupted by
            // mouse-move). When interrupted, _activeFadeStoryboard is set
            // to null in OnCardMouseMove BEFORE Stop completes, so this
            // event fires with _activeFadeStoryboard == null → skip hide.
            if (_activeFadeStoryboard is null) return;
            _activeFadeStoryboard = null;
            Hide();
        };
        _activeFadeStoryboard = sb;
        sb.Begin(this, isControllable: true);
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Hide();

    private void OnEngageClicked(object sender, RoutedEventArgs e)
    {
        EngageClicked?.Invoke(this, EventArgs.Empty);
        Hide();
    }

    private void OnMuteClicked(object sender, RoutedEventArgs e)
    {
        MuteClicked?.Invoke(this, EventArgs.Empty);
        Hide();
    }

    /// <summary>Monitor-coords physical-pixel rect for the WGC exclude mask
    /// (Option II: always-on mask for any visible UI). Same DPI maths as
    /// PersistentBarWindow.</summary>
    public (int Left, int Top, int Width, int Height) GetMonitorRectInPhysicalPixels()
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX;
        double sy = dpi.DpiScaleY;
        int left   = (int)Math.Floor(Left * sx);
        int top    = (int)Math.Floor(Top  * sy);
        int width  = (int)Math.Ceiling(ActualWidth  * sx);
        int height = (int)Math.Ceiling(ActualHeight * sy);
        return (left, top, width, height);
    }
}
