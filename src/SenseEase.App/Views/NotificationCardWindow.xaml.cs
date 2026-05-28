using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SenseEase.App.Views;

/// <summary>
/// Pop-up sprint Task 4 + overlay-bar-redesign D1-D4: the expanded
/// notification card. Shown when the user clicks the bar or tray icon
/// (user-initiated) or, with Toggle A = ON, auto-expanded on a tier-up.
///
/// <para>overlay-bar-redesign updates:</para>
/// <list type="bullet">
///   <item>D1: light theme (white surface, dark text). All foreground/background
///         colour decisions now live in the XAML — this file only sets
///         <c>TitleText.Text</c> and <c>BodyText.Text</c>.</item>
///   <item>D2: numeric title readout — <c>SetContent</c> now takes
///         <c>(band, tier, pressure)</c> and formats the title as
///         <c>"VIMS Tier {tier} · Pressure {p}/100"</c>. The body shows the
///         tier-band description (the text that USED to be the title-suffix
///         in the v1 zh-only build).</item>
///   <item>D3: Power Save toggle + Settings gear. <see cref="IsPowerSaveActive"/>
///         flips the card into a "sensing paused" message; toggling the
///         CheckBox raises <see cref="PowerSaveToggled"/> for the host to
///         pause WGC + mark the tray. The gear button opens
///         <see cref="SettingsWindow"/> using the host-provided
///         <see cref="HasVestibularDiagnosis"/> lookup.</item>
///   <item>D4: deep-blue Open Dashboard button with stacked zh/en label.
///         Style moved to <c>PrimaryDeepBlueButtonStyle</c> in
///         Window.Resources.</item>
/// </list>
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

    /// <summary>overlay-bar-redesign D3: raised when the Power Save toggle
    /// flips. Host receives <c>true</c> for ON (pause sensing) and
    /// <c>false</c> for OFF (resume sensing).</summary>
    public event EventHandler<bool>? PowerSaveToggled;

    /// <summary>overlay-bar-redesign D3: provided by the host so the gear
    /// button can construct a <see cref="SettingsWindow"/> with the
    /// same vestibular-diagnosis lookup the host uses elsewhere. Falls
    /// back to <c>() => false</c> when the host doesn't wire it.</summary>
    public Func<bool>? HasVestibularDiagnosis { get; set; }

    /// <summary>Path Z bug-fix 7: invoked when the user changes a preference
    /// in the SettingsWindow opened from the gear icon. Host wires this to
    /// repaint the bar/tray immediately (matches the tray-menu Settings path
    /// in <see cref="MainWindow.ShowSettingsWindow"/>). No-op if unwired.</summary>
    public Action? OnPreferencesChanged { get; set; }

    /// <summary>v0.9.1 round-3: invoked when the user types into the in-card
    /// chatbot box and presses Send (or Enter). Host wires this to the
    /// ChatbotViewModel's Send pipeline so adjustments flow exactly like
    /// they do from the dashboard's chat tab — same parser, same engine
    /// hooks, same lexicon fallback. Receives the user's text; returns a
    /// short status string the card surfaces beneath the input (e.g. the
    /// chatbot's reply or an acknowledgement). Null = host hasn't wired
    /// it; the Send button is hidden in that case.</summary>
    public Func<string, Task<string?>>? SendChatMessage { get; set; }

    private readonly DispatcherTimer _idleTimer;
    private Storyboard? _activeFadeStoryboard;

    // v0.8 hotfix: click-outside-auto-close. User reported the card was
    // staying open after they clicked another app — they want it to
    // dismiss immediately when focus moves to a different process.
    // 500 ms poll of GetForegroundWindow + process-ID compare. If the
    // foreground window belongs to ANY other process (not SenseEase),
    // hide the card. Stops on Hide/Close so we don't poll forever.
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    private DispatcherTimer? _foregroundCheckTimer;
    private static readonly uint OurProcessId = (uint)Process.GetCurrentProcess().Id;

    // overlay-bar-redesign D2/D3: cache the last content the card was set to
    // so an IsPowerSaveActive flip can re-render without callers passing
    // values again. Default band is Grey for the rare path where the toggle
    // is set before any SetContent — the card simply shows the power-save
    // message anyway, so the cached band is irrelevant in that state.
    private TierColorPalette.TierBand _lastBand = TierColorPalette.TierBand.Grey;
    private int _lastTier = 1;
    private double _lastPressure;
    private bool _hasContent;
    private bool _isPowerSaveActive;
    private bool _suppressToggleEvent; // re-entrancy guard when host sets IsPowerSaveActive

    /// <summary>overlay-bar-redesign D3: when true, the card shows the
    /// "Power Save active — sensing paused" message instead of the numeric
    /// tier/pressure readout. Setting this property re-renders the card
    /// immediately so the surface stays in sync with the host's WGC state.
    /// The setter also syncs the in-card CheckBox without re-raising the
    /// <see cref="PowerSaveToggled"/> event (the host is the source of
    /// truth for this state).</summary>
    public bool IsPowerSaveActive
    {
        get => _isPowerSaveActive;
        set
        {
            if (_isPowerSaveActive == value) return;
            _isPowerSaveActive = value;
            // Keep the in-card CheckBox visually in sync without re-raising
            // PowerSaveToggled (otherwise the host setting this property in
            // response to its OWN handler would loop).
            _suppressToggleEvent = true;
            try { PowerSaveToggle.IsChecked = value; }
            finally { _suppressToggleEvent = false; }
            ApplyContentToUi();
        }
    }

    public NotificationCardWindow()
    {
        InitializeComponent();
        _idleTimer = new DispatcherTimer { Interval = IdleTimeout };
        _idleTimer.Tick += OnIdleTimerTick;
        Loaded += OnLoaded;
        IsVisibleChanged += OnIsVisibleChanged;
        Closed += (_, _) =>
        {
            _idleTimer.Stop();
            _foregroundCheckTimer?.Stop();
            _foregroundCheckTimer = null;
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Position just below the bar: top-centre, 12 dip below the bar's
        // bottom edge. Bar is 4 dip tall at y=0 → card top at y=16.
        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - Width) / 2.0;
        Top  = work.Top + 16;
        // Initial fade-in + timers handled by OnIsVisibleChanged below so
        // second/third/Nth Show() also resets state (v0.9.1 round-4 fix:
        // previously the idle-fade left Opacity=0 and OnLoaded didn't re-
        // fire, so re-showing the card produced an invisible window).
    }

    // v0.8 hotfix: keep the foreground-check timer aligned with the card's
    // visibility. Stops the timer when card is hidden (cheap when card isn't
    // open), restarts when Show()n. IsVisibleChanged fires on both Show and
    // Hide so this is the right hook (Loaded fires only once per construction).
    // v0.9.1 round-4: also resets per-Show state — Opacity, idle timer,
    // in-flight chat-send guard, and any pending fade storyboard — so the
    // card recovers cleanly across repeated Show/Hide cycles.
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            // Cancel any leftover fade animation (idle-fade may have set
            // Opacity to 0 via a storyboard whose animation is still attached
            // to the OpacityProperty). BeginAnimation(null) detaches it so
            // the local Opacity assignment below actually takes effect.
            BeginAnimation(OpacityProperty, null);
            _activeFadeStoryboard?.Stop(this);
            _activeFadeStoryboard = null;
            Opacity = 0;
            var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(220)));
            BeginAnimation(OpacityProperty, fadeIn);

            // Recover from a stuck in-flight chat send. If the previous
            // SendChatMessage callback threw before the finally block could
            // re-enable the UI, _chatSendInFlight would be left true and the
            // input/button would stay disabled across Hides. Reset here as a
            // belt-and-suspenders safeguard.
            _chatSendInFlight = false;
            if (ChatSendBtn is not null)  ChatSendBtn.IsEnabled = true;
            if (ChatInputBox is not null) ChatInputBox.IsEnabled = true;

            RestartIdleTimer();

            if (_foregroundCheckTimer is null)
            {
                _foregroundCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _foregroundCheckTimer.Tick += OnForegroundCheckTick;
            }
            _foregroundCheckTimer.Start();
        }
        else
        {
            _foregroundCheckTimer?.Stop();
        }
    }

    /// <summary>v0.8 hotfix: hide the card when any non-SenseEase window
    /// gains foreground. Implements the click-outside-auto-close UX the
    /// user explicitly asked for ("I want it to autoclose, not stay
    /// forever on the screen"). Compares foreground process ID against
    /// our own — any of OUR windows (MainWindow, PersistentBarWindow,
    /// NotificationCardWindow itself, SettingsWindow) keep the card alive;
    /// any external process triggers immediate Hide.</summary>
    private void OnForegroundCheckTick(object? sender, EventArgs e)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return;
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == 0) return; // GetWindowThreadProcessId failed; don't act
        if (pid != OurProcessId)
        {
            // Foreground belongs to another app → user has switched focus.
            // Hide the card. Idle timer + fade storyboard already handle
            // their own teardown via IsVisibleChanged-driven _foregroundCheckTimer
            // stop above + Closed handlers below.
            Hide();
        }
    }

    /// <summary>overlay-bar-redesign D2: sets card content for a VIMS tier
    /// + pressure readout. The numeric title is rounded to a whole number;
    /// the body carries the tier-band description (what the v1 title
    /// suffix communicated).
    /// <para>When <see cref="IsPowerSaveActive"/> is true, the inputs are
    /// cached but the rendered title/body is the power-save override
    /// message — so the card flips correctly on subsequent
    /// <c>IsPowerSaveActive = false</c> without callers re-passing
    /// values.</para></summary>
    public void SetContent(TierColorPalette.TierBand band, int tier, double pressure)
    {
        _lastBand = band;
        _lastTier = tier;
        _lastPressure = pressure;
        _hasContent = true;
        ApplyContentToUi();
    }

    private void ApplyContentToUi()
    {
        if (_isPowerSaveActive)
        {
            TitleText.Text = "VIMS · 省電模式中 / Power Save Active";
            BodyText.Text  = "感測已暫停。按下「省電模式」可恢復。/ Sensing paused. Toggle Power Save off to resume.";
            return;
        }

        // Default normal-state body: derive from the tier band so callers
        // that haven't called SetContent yet still get a sensible placeholder.
        string body = DescriptionForBand(_lastBand);
        if (_hasContent)
        {
            // Path Z UX iter 3: tier shown as N/10 so the scale is unambiguous
            // (VIMS tier is a 1–10 discrete band per OverlayWindow.VimsLevels).
            // Pressure has no engine-level upper bound (LeakyIntegrator keeps
            // accumulating); the dashboard caps its chart at 200 with the
            // mask trigger at 85, so show those reference points instead of
            // a fake "/100" denominator. User reported the old "/100" was
            // misleading because real pressure can exceed 300.
            TitleText.Text = $"VIMS Tier {_lastTier}/10 · Pressure {Math.Round(_lastPressure)} (mask at 85)";
        }
        else
        {
            // No SetContent yet — keep the title empty (matches the v1
            // "shown but unpopulated" state).
            TitleText.Text = string.Empty;
        }
        BodyText.Text = body;
    }

    private static string DescriptionForBand(TierColorPalette.TierBand band) => band switch
    {
        TierColorPalette.TierBand.Grey  => "目前視覺壓力低。如有疑問請開啟設定面板。",
        TierColorPalette.TierBand.Amber => "偵測到中度視覺壓力。建議短暫休息或暗化螢幕。",
        TierColorPalette.TierBand.Red   => "偵測到高度視覺壓力。建議立即啟動保護或暫停當前活動。",
        _                                => string.Empty,
    };

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

    private void OnPowerSaveToggled(object sender, RoutedEventArgs e)
    {
        // Guard against the re-entrant path where the host sets
        // IsPowerSaveActive — that setter assigns CheckBox.IsChecked which
        // re-fires this handler. _suppressToggleEvent stays true for the
        // duration of the property setter so we silently swallow this pass.
        if (_suppressToggleEvent) return;

        bool nowOn = PowerSaveToggle.IsChecked == true;
        // Update internal state and re-render so the title/body switch
        // immediately on user interaction even if the host doesn't echo
        // the value back through IsPowerSaveActive.
        if (_isPowerSaveActive != nowOn)
        {
            _isPowerSaveActive = nowOn;
            ApplyContentToUi();
        }
        PowerSaveToggled?.Invoke(this, nowOn);
    }

    /// <summary>overlay-bar-redesign D3: opens the Settings window using the
    /// host-provided vestibular-diagnosis lookup (or a no-diagnosis fallback
    /// when the host hasn't wired it). Modal-feeling — uses
    /// <c>ShowDialog</c> with the card as owner so the settings stay on top
    /// while open.</summary>
    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        // Path Z bug-fix 3: stop the card's idle-fade timer and cancel any
        // in-progress fade so the card doesn't disappear behind the modal
        // Settings dialog after 60 s of inactivity in Settings.
        _idleTimer.Stop();
        if (_activeFadeStoryboard is not null)
        {
            _activeFadeStoryboard.Stop(this);
            _activeFadeStoryboard = null;
            Opacity = 1.0;
        }

        try
        {
            var diagFn = HasVestibularDiagnosis ?? (() => false);
            var win = new SettingsWindow(diagFn) { Owner = this };
            // Path Z bug-fix 7: forward PreferencesChanged to the host so the
            // bar/tray repaint immediately on Toggle B change — matching the
            // tray-menu Settings path.
            if (OnPreferencesChanged is not null)
                win.PreferencesChanged += (_, _) => OnPreferencesChanged();
            win.ShowDialog();
        }
        finally
        {
            // Restart the idle countdown when Settings closes so the card
            // resumes its normal auto-collapse behaviour.
            RestartIdleTimer();
        }
    }

    /// <summary>Patch 9: primary action — surface the dashboard window. User
    /// flagged this as the most-important sub-feature of the bar/card
    /// optimization sprint: a card-launched route back to the main app
    /// without hunting in the taskbar.
    /// <para>Implementation notes:</para>
    /// <list type="bullet">
    ///   <item>Restore from Minimized state if needed (TaskbarIcon click +
    ///         Show alone wouldn't un-minimize).</item>
    ///   <item>Topmost true→false dance bumps the dashboard above
    ///         PersistentBarWindow (which is also Topmost) without permanently
    ///         pinning it there. Standard WPF pattern for "raise above
    ///         siblings without becoming sticky."</item>
    ///   <item>Card closes after activation so the user gets a single-action
    ///         flow — one click, dashboard up, card gone.</item>
    ///   <item>Null guard: if Application.Current.MainWindow is null (e.g.
    ///         the dashboard was already closed and the app is running tray-
    ///         only), the button silently no-ops rather than crashing. A
    ///         future polish could re-instantiate MainWindow in that case;
    ///         out of scope for Patch 9.</item>
    /// </list></summary>
    private void OnOpenDashboardClicked(object sender, RoutedEventArgs e)
    {
        var mainWindow = System.Windows.Application.Current?.MainWindow;
        if (mainWindow is null) { Close(); return; }

        if (mainWindow.WindowState == WindowState.Minimized)
            mainWindow.WindowState = WindowState.Normal;

        mainWindow.Show();
        mainWindow.Activate();
        // Topmost dance: bump above other Topmost siblings (PersistentBarWindow,
        // NotificationCardWindow itself) without permanently pinning the
        // dashboard. WPF resolves this synchronously on the dispatcher.
        mainWindow.Topmost = true;
        mainWindow.Topmost = false;

        Close();
    }

    private bool _chatSendInFlight;

    // v0.9.1 round-4: localized phrases the four quick-response buttons send.
    // The chatbot pipeline (FeedbackLexicon + Mistral NLU) interprets these
    // as ReportDiscomfort with the matching severity, producing the same
    // FOV/mask/grid deltas a typed equivalent would. zh phrasing matches
    // FeedbackLexicon's known triggers so the deterministic lexicon path
    // applies before any LLM round-trip — quick-response stays snappy.
    private static readonly string[] QuickDiscomfortPhrases =
    {
        string.Empty,                  // 0: unused
        "我有點不舒服",                // 1: slight
        "我不舒服",                    // 2: mild
        "我很不舒服",                  // 3: strong
        "我非常不舒服，請大力調整",     // 4: severe
    };

    /// <summary>v0.9.1 round-4: quick-response handler. Stuffs the matching
    /// localized phrase into the chat input and fires Send, so the existing
    /// chatbot pipeline interprets the discomfort and applies adjustments.
    /// Severity comes from the button's <c>Tag</c> ("1"–"4"). Reuses the
    /// SendChatMessage callback so the same status surface + double-send
    /// guard + idle-timer reset all apply.</summary>
    private async void OnQuickDiscomfortClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (!int.TryParse(btn.Tag?.ToString(), out int severity)) return;
        if (severity < 1 || severity >= QuickDiscomfortPhrases.Length) return;
        ChatInputBox.Text = QuickDiscomfortPhrases[severity];
        await TrySendChatAsync();
    }

    /// <summary>v0.9.1 round-3: send the typed message through the host's
    /// chatbot pipeline. Guards against double-send while one is in flight,
    /// disables the button visually, and surfaces a status line beneath
    /// the input. Errors are swallowed into the status line so a failing
    /// LLM doesn't crash the card.</summary>
    private async void OnChatSendClicked(object sender, RoutedEventArgs e)
    {
        await TrySendChatAsync();
    }

    private async void OnChatInputKeyDown(object sender, KeyEventArgs e)
    {
        // Enter sends, Shift+Enter inserts newline (matches dashboard convention).
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            await TrySendChatAsync();
        }
    }

    private async Task TrySendChatAsync()
    {
        if (_chatSendInFlight) return;
        string text = ChatInputBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text)) return;
        if (SendChatMessage is null)
        {
            ChatStatusText.Text = "Chatbot 未連線 / Chatbot not wired.";
            return;
        }

        _chatSendInFlight = true;
        ChatSendBtn.IsEnabled = false;
        ChatInputBox.IsEnabled = false;
        ChatStatusText.Text = "傳送中… / Sending…";
        // Treat any in-card activity as user interaction — defer the idle fade.
        RestartIdleTimer();

        string? reply = null;
        try
        {
            reply = await SendChatMessage(text).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ChatStatusText.Text = $"傳送失敗 / Send failed: {ex.GetType().Name}";
        }
        finally
        {
            _chatSendInFlight = false;
            ChatSendBtn.IsEnabled = true;
            ChatInputBox.IsEnabled = true;
            ChatInputBox.Clear();
            ChatInputBox.Focus();
            if (!string.IsNullOrEmpty(reply))
            {
                ChatStatusText.Text = reply;
            }
            else if (ChatStatusText.Text == "傳送中… / Sending…")
            {
                ChatStatusText.Text = "已套用 / Applied.";
            }
            RestartIdleTimer();
        }
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
