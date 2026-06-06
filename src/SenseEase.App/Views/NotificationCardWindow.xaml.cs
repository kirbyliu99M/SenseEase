using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SenseEase.App.ViewModels;
using SenseEase.Core.Inference;

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

    /// <summary>bar-redesign: raised when the user nudges the intervention-depth
    /// control on the new compact bar. Payload is <c>+1</c> (the "+" pill — ease
    /// MORE / deeper intervention) or <c>-1</c> (the "−" pill — ease LESS / widen).
    /// The host routes this to the engine's ease path (see
    /// <c>MainViewModel.EaseView</c> / <c>ApplyUserFeedback</c>). Unlike Engage /
    /// Mute / Close, a +/- nudge does NOT hide the card — the user can keep
    /// adjusting in place.</summary>
    public event EventHandler<int>? InterventionAdjustRequested;

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

    // ── M3 Phase 7e: chatbot proposal section (Flow A) ───────────────────────

    /// <summary>Raised when the user clicks "接受" on the chatbot proposal
    /// section. Host calls <see cref="HabituationOverrideApplicator.ApplyAccepted"/>
    /// in response; the result is fed back via <see cref="ShowProposalApplyResult"/>
    /// so the card can render the intercept/safety message if one fired.</summary>
    public event EventHandler<ProfileOverrideProposal>? ProposalAccepted;

    /// <summary>Raised when the user clicks "拒絕". Host calls
    /// <see cref="HabituationOverrideApplicator.RecordDeclined"/>; the card
    /// clears the section immediately on click.</summary>
    public event EventHandler<ProfileOverrideProposal>? ProposalDeclined;

    /// <summary>Raised when the user clicks "稍後". Host keeps the proposal
    /// pending in memory; the card clears the section but the host can
    /// re-show it later (e.g. on next bar click) while the proposal hasn't
    /// expired.</summary>
    public event EventHandler<ProfileOverrideProposal>? ProposalDeferred;

    /// <summary>Phase 5 Step 7: raised when the in-card 15s expiry countdown
    /// crosses <c>ExpiresAtUtc</c> with no user action. Host calls
    /// <c>_viewModel.ExpireActiveProposal()</c> so the VM mirror clears
    /// immediately instead of waiting for the VM-side 60s watchdog —
    /// closes the race where the Card showed 已過期 while the VM still
    /// held the proposal active for up to ~45s. Fires exactly once per
    /// proposal (the countdown stops itself in the same branch).</summary>
    public event EventHandler<ProfileOverrideProposal>? ProposalExpired;

    private ProfileOverrideProposal? _activeProposal;

    /// <summary>Top-center revision: true when the card was opened by an explicit
    /// user action (clicking the bar / tray) rather than auto-surfaced on a
    /// tier-up. A user-opened card lingers (60 s idle window); an auto-surfaced
    /// card is far calmer and self-dismisses after a short 5 s idle window so it
    /// never nags. Read in <see cref="OnIsVisibleChanged"/> to pick the
    /// <see cref="IdleTimeout"/> before the idle timer (re)starts.</summary>
    public bool UserOpened { get; set; }

    private DispatcherTimer? _expiryCountdownTimer;

    /// <summary>The chatbot's currently-surfaced proposal, or null. Setting
    /// this to a non-null value reveals the proposal section + starts the
    /// expiry countdown; setting it to null hides the section.</summary>
    public ProfileOverrideProposal? ActiveProposal
    {
        get => _activeProposal;
        set
        {
            _activeProposal = value;
            if (value is null)
            {
                ProposalPanel.Visibility = Visibility.Collapsed;
                StopExpiryCountdown();
            }
            else
            {
                ApplyProposalToUi(value);
                ProposalPanel.Visibility = Visibility.Visible;
                StartExpiryCountdown();
            }
        }
    }

    /// <summary>Host calls this after <see cref="ProposalAccepted"/> fires so
    /// the card can surface the apply result (success → clear section;
    /// blocked-by-safety → keep section visible with the safety note in red).</summary>
    public void ShowProposalApplyResult(ApplyResult result)
    {
        if (result.IsSuccess)
        {
            // Apply succeeded — clear the section. Host has already logged.
            ActiveProposal = null;
            return;
        }

        // BlockedBySafety: keep section visible, surface the reason in the
        // safety-note callout (the M8 Border supplies the ⚠ glyph + chip).
        if (result.BlockedBySafety && _activeProposal is not null)
        {
            ProposalSafetyNoteText.Text = $"系統介入：{result.Reason}";
            ProposalSafetyNoteText.Visibility = Visibility.Visible;
            // Disable Accept so the user has to Decline or Later (clicking Accept
            // again would just re-block).
            ProposalAcceptBtn.IsEnabled = false;
        }
    }

    private void ApplyProposalToUi(ProfileOverrideProposal proposal)
    {
        ProposalSummaryText.Text = FormatProposalSummary(proposal);
        // H7: always lead with a non-empty "AI suggests" attribution so the user
        // can't mistake model-generated rationale for app/system chrome. The text
        // itself is already injection-stripped + length-capped at parse time
        // (HabituationOverrideParser.SanitizeRationale).
        ProposalRationaleText.Text = string.IsNullOrWhiteSpace(proposal.Rationale)
            ? SenseEase.App.Localization.LocalizationManager.Current.Pick("AI 建議：—", "AI suggests: —")
            : SenseEase.App.Localization.LocalizationManager.Current.Pick(
                  $"AI 建議：{proposal.Rationale}", $"AI suggests: {proposal.Rationale}");
        ProposalSafetyNoteText.Text = string.Empty;
        ProposalSafetyNoteText.Visibility = Visibility.Collapsed;
        ProposalAcceptBtn.IsEnabled = true;
        UpdateExpiryText(proposal);
    }

    private static string FormatProposalSummary(ProfileOverrideProposal p)
    {
        switch (p.Intent)
        {
            case OverrideIntent.AdjustSensitivity:
                return $"把 sensitivity 調整為 {p.ProposedValue}";
            case OverrideIntent.SwitchProfile:
                return $"把 profile 切換為 {p.ProposedValue}";
            case OverrideIntent.AdjustStrikeLimit:
                return $"把 severe-strike-limit 調整為 {p.ProposedValue}";
            default:
                return $"{p.Intent} = {p.ProposedValue}";
        }
    }

    private void StartExpiryCountdown()
    {
        StopExpiryCountdown();
        _expiryCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _expiryCountdownTimer.Tick += (_, _) =>
        {
            if (_activeProposal is null) { StopExpiryCountdown(); return; }
            UpdateExpiryText(_activeProposal);
            if (_activeProposal.ExpiresAtUtc <= DateTime.UtcNow)
            {
                // Capture BEFORE any mutation so the event payload is stable
                // even if a re-entrant handler touches ActiveProposal.
                var p = _activeProposal;
                StopExpiryCountdown();
                // Phase 5 Step 7: surface visible state THEN notify host so
                // the VM mirror clears at 15s instead of waiting for the
                // 60s backstop watchdog. Card's 已過期 text + disabled
                // buttons remain as the user-facing tombstone until the
                // user dismisses the card.
                ProposalAcceptBtn.IsEnabled = false;
                ProposalLaterBtn.IsEnabled = false;
                ProposalDeclineBtn.IsEnabled = false; // NE27: the tombstone is fully non-interactive (Decline was left clickable)
                ProposalExpiryText.Text = "已過期";
                // NE27: clear the logical active proposal so no stale reference
                // survives the expiry. Assign the backing field directly (NOT the
                // ActiveProposal setter) so the visible 已過期 tombstone — disabled
                // buttons + expiry text — stays on screen until the user dismisses
                // the card; the setter would collapse the panel immediately.
                _activeProposal = null;
                ProposalExpired?.Invoke(this, p);
            }
        };
        _expiryCountdownTimer.Start();
    }

    private void StopExpiryCountdown()
    {
        try { _expiryCountdownTimer?.Stop(); } catch { /* ignore */ }
        _expiryCountdownTimer = null;
    }

    private void UpdateExpiryText(ProfileOverrideProposal proposal)
    {
        TimeSpan remaining = proposal.ExpiresAtUtc - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            ProposalExpiryText.Text = "已過期";
            return;
        }
        // Render at minute granularity above 1h, else minute:second.
        ProposalExpiryText.Text = remaining.TotalHours >= 1
            ? $"剩 {(int)remaining.TotalHours} 小時 {remaining.Minutes:00} 分"
            : $"剩 {remaining.Minutes:00}:{remaining.Seconds:00}";
    }

    private void OnProposalAcceptClicked(object sender, RoutedEventArgs e)
    {
        if (_activeProposal is null) return;
        var p = _activeProposal;
        // Disable button immediately to avoid double-click. Host will call
        // ShowProposalApplyResult next to either clear the section (success)
        // or surface the intercept message.
        ProposalAcceptBtn.IsEnabled = false;
        ProposalAccepted?.Invoke(this, p);
    }

    private void OnProposalDeclineClicked(object sender, RoutedEventArgs e)
    {
        if (_activeProposal is null) return;
        var p = _activeProposal;
        ActiveProposal = null;
        ProposalDeclined?.Invoke(this, p);
    }

    private void OnProposalLaterClicked(object sender, RoutedEventArgs e)
    {
        if (_activeProposal is null) return;
        var p = _activeProposal;
        // Per user spec: 'Later' hides the proposal; user manually re-opens
        // via the bar. Card itself hides — host keeps proposal pending.
        ActiveProposal = null;
        ProposalDeferred?.Invoke(this, p);
        HideWithSlide();
    }

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

    // NE24: single-instance guard for the gear-icon Settings window. Holds the
    // currently-open SettingsWindow (if any) so repeated gear clicks reuse +
    // re-activate the existing window instead of stacking duplicate instances.
    // Cleared on that window's Closed event.
    private SettingsWindow? _openSettingsWindow;

    // overlay-bar-redesign D2/D3: cache the last content the card was set to
    // so an IsPowerSaveActive flip can re-render without callers passing
    // values again. Default band is VeryHigh (the calmest 級) for the rare
    // path where the toggle is set before any SetContent — the card simply
    // shows the power-save message anyway, so the cached band is irrelevant
    // in that state.
    private TierColorPalette.TierBand _lastBand = TierColorPalette.TierBand.VeryHigh;
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
        // HUD-card rework: a non-activating, frosted acrylic pill whose tint is
        // THEME-AWARE. EnableNoActivate keeps the card from stealing foreground
        // when it appears (its own controls stay clickable); EnableAcrylic frosts
        // the background behind the (now token-coloured) Surface. The previous
        // hardcoded HP-blue tint (0x33D69600) made the card read blue-white in
        // BOTH themes — wrong in dark. We instead pick a near-neutral light or
        // dark scrim that complements the {DynamicResource Surface} fill so the
        // card holds together in either theme. Both must run after the HWND
        // exists, hence SourceInitialized.
        this.SourceInitialized += (_, _) =>
        {
            SenseEase.App.Interop.ClickThrough.EnableNoActivate(this);
            ApplyThemeAwareAcrylic();
        };
        _idleTimer = new DispatcherTimer { Interval = IdleTimeout };
        _idleTimer.Tick += OnIdleTimerTick;
        Loaded += OnLoaded;
        IsVisibleChanged += OnIsVisibleChanged;
        LocationChanged += OnCardLocationChanged;
        // LIVE language toggle: the code-set strings (the 5級 headline + the
        // power-save override title/body) don't auto-update like the XAML
        // {loc:T} bindings do, so re-run the content setter whenever the user
        // flips language in Settings. It re-applies from the stored
        // _lastBand/_lastTier/_lastPressure (and the power-save override).
        // Unsubscribed on Close so the card can't leak onto the long-lived
        // LocalizationManager singleton.
        SenseEase.App.Localization.LocalizationManager.Current.PropertyChanged += OnUiLanguageChanged;
        Closed += (_, _) =>
        {
            _idleTimer.Stop();
            SenseEase.App.Localization.LocalizationManager.Current.PropertyChanged -= OnUiLanguageChanged;
        };
    }

    /// <summary>LIVE language toggle: re-apply the code-set card text (the 5級
    /// headline / power-save override) when the UI language changes. The XAML
    /// {loc:T} button labels + a11y names update themselves via their own live
    /// bindings; only the strings this code-behind assigns need the manual
    /// re-run. Re-uses the cached _lastBand/_lastTier/_lastPressure so no caller
    /// has to re-push content.</summary>
    private void OnUiLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null
            || e.PropertyName == nameof(SenseEase.App.Localization.LocalizationManager.Language)
            || e.PropertyName == nameof(SenseEase.App.Localization.LocalizationManager.IsEnglish))
        {
            ApplyContentToUi();
        }
    }

    /// <summary>HUD-card rework item 3: pick an acrylic scrim that suits the
    /// current theme. In dark mode the card surface is a deep slate, so the
    /// blur-behind tint is a dark near-black scrim (keeps the token text at
    /// &gt;=4.5:1); in light mode it's a soft near-white scrim so the card stays
    /// light. ABGR order (0xAABBGGRR). Re-applied on theme flips so a runtime
    /// light&#8596;dark change repaints the frost too.</summary>
    private void ApplyThemeAwareAcrylic()
    {
        // 0x.. = AA BB GG RR. Dark: ~85% #1B1E24-ish slate scrim; Light: ~85%
        // near-white scrim. The Surface token paints the actual fill on top of
        // this frost; the scrim only governs what bleeds through the blur.
        uint tint = SenseEase.App.Theme.ThemeManager.ShouldUseDark()
            ? 0xD9241E1Bu   // dark slate scrim
            : 0xD9FFFFFFu;  // soft near-white scrim
        SenseEase.App.Interop.AcrylicGlass.EnableAcrylic(this, tint);
    }

    // HUD-card rework item 4: draggable + remember-position state.
    //  _hasRememberedPosition — true once we've placed the card at a persisted
    //    Left/Top (or the user has dragged it). While true the late SizeChanged
    //    re-anchor is SUPPRESSED so a SizeToContent=Height settle never yanks a
    //    user-placed card back to top-center.
    //  _suppressPositionPersist — guards LocationChanged against persisting our
    //    OWN programmatic placement (AnchorTopCenter / restore), so we only save
    //    positions the USER chose.
    private bool _hasRememberedPosition;
    private bool _suppressPositionPersist;
    // True for the duration of an in-flight DragMove. While dragging, WPF fires
    // LocationChanged on every move tick — we mark the position dirty but DON'T
    // hit disk per-tick; the single Save() runs once when DragMove returns (drag
    // end). The host's own LocationChanged subscription still re-pushes the WGC
    // exclude rect per tick so the overlay follows the card live.
    private bool _dragInProgress;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // HUD-card rework item 4: restore the saved card position if the user has
        // dragged the card before (Game-Bar "remember where I put it"); otherwise
        // fall back to the top-center first-run anchor. SizeToContent=Height means
        // the final height isn't known until layout, so we re-anchor on
        // SizeChanged too — but ONLY while the card has no remembered position.
        RestoreOrAnchorPosition();
        SizeChanged -= OnCardSizeChanged;   // idempotent across repeated Loads
        SizeChanged += OnCardSizeChanged;
        // Initial fade-in + timers handled by OnIsVisibleChanged below so
        // second/third/Nth Show() also resets state (v0.9.1 round-4 fix:
        // previously the idle-fade left Opacity=0 and OnLoaded didn't re-
        // fire, so re-showing the card produced an invisible window).
    }

    private void OnCardSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // Only the top-center fallback needs the late re-anchor (its X depends on
        // the settled width). A remembered/dragged position is absolute and must
        // be left exactly where the user put it.
        if (!_hasRememberedPosition) AnchorTopCenter();
    }

    /// <summary>HUD-card rework item 4: place the card. If UserPreferences has a
    /// saved HudLeft/HudTop, restore there (clamped on-screen); else fall back to
    /// the top-center first-run anchor. Programmatic placement is wrapped in the
    /// persist-suppress guard so it doesn't echo back into a save.</summary>
    private void RestoreOrAnchorPosition()
    {
        double? savedLeft = null, savedTop = null;
        try
        {
            var prefs = SenseEase.App.Configuration.UserPreferences.Current;
            savedLeft = prefs.HudLeft;
            savedTop = prefs.HudTop;
        }
        catch { /* prefs unavailable (e.g. bare test app) → fall back to anchor */ }

        if (savedLeft is double l && savedTop is double t && IsOnAnyScreen(l, t))
        {
            _suppressPositionPersist = true;
            try
            {
                Left = l;
                Top = t;
            }
            finally { _suppressPositionPersist = false; }
            _hasRememberedPosition = true;
        }
        else
        {
            AnchorTopCenter();
        }
    }

    /// <summary>Sanity-clamp: a saved position is only honoured if its top-left
    /// still lands inside the current virtual screen (a monitor could have been
    /// unplugged since the position was saved). Otherwise we fall back to the
    /// top-center anchor so the card never opens off-screen.</summary>
    private static bool IsOnAnyScreen(double left, double top)
    {
        double vl = SystemParameters.VirtualScreenLeft;
        double vt = SystemParameters.VirtualScreenTop;
        double vr = vl + SystemParameters.VirtualScreenWidth;
        double vb = vt + SystemParameters.VirtualScreenHeight;
        // Require at least a 48x48 dip corner to remain visible.
        return left >= vl - 8 && top >= vt - 8 && left + 48 <= vr && top + 48 <= vb;
    }

    /// <summary>Top-center revision: anchor the card horizontally centered at the
    /// TOP of the working area (12 dip down from the top edge). Re-run on
    /// SizeChanged so the SizeToContent=Height measurement keeps the pill
    /// centered as its width is the only fixed dimension. Wrapped in the
    /// persist-suppress guard so the programmatic move isn't saved as a user
    /// drag.</summary>
    private void AnchorTopCenter()
    {
        _suppressPositionPersist = true;
        try
        {
            Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2.0;
            Top = SystemParameters.WorkArea.Top + 12;
        }
        finally { _suppressPositionPersist = false; }
    }

    /// <summary>HUD-card rework item 4: drag the whole window from any non-control
    /// part of the card surface (Game-Bar style). MouseLeftButtonDown on a child
    /// Button/CheckBox/TextBox is handled by that control and never bubbles here,
    /// so the controls stay clickable; only a press on the bare surface drags.
    /// DragMove persists the new position via the LocationChanged handler (the
    /// final settle) — we also save once here on the drag's mouse-up as a
    /// belt-and-suspenders for the rare case LocationChanged is coalesced.</summary>
    private void OnCardDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        // Any interaction counts as activity — keep the card alive while dragging.
        RestartIdleTimer();
        _dragInProgress = true;
        try
        {
            // Blocks until the drag completes (mouse-up). LocationChanged fires
            // per tick during this call but is throttled off disk by _dragInProgress.
            DragMove();
        }
        catch
        {
            // DragMove throws if the button was already released (fast click) —
            // harmless, ignore.
        }
        finally
        {
            _dragInProgress = false;
        }
        // Drag finished — remember + persist the final resting position once.
        _hasRememberedPosition = true;
        SaveCurrentPosition();
    }

    /// <summary>HUD-card rework item 4: persist the card's Left/Top whenever the
    /// window actually moves (drag end / programmatic settle). Suppressed during
    /// our own AnchorTopCenter / restore placement so only USER moves are saved,
    /// and throttled off disk DURING an in-flight DragMove (the single Save runs
    /// when DragMove returns). The host re-pushes the WGC exclude rect on its own
    /// LocationChanged subscription, so the overlay keeps following the card.</summary>
    private void OnCardLocationChanged(object? sender, EventArgs e)
    {
        if (_suppressPositionPersist) return;
        // A real move the user caused. Remember it so SizeChanged won't re-center.
        _hasRememberedPosition = true;
        // While a drag is mid-flight, don't hammer the disk every tick — the
        // OnCardDragStart finally-block persists once on drag end. Persist here
        // only for moves that arrive OUTSIDE a drag (e.g. a future keyboard nudge
        // or a programmatic move that isn't suppressed).
        if (_dragInProgress) return;
        SaveCurrentPosition();
    }

    private void SaveCurrentPosition()
    {
        // Don't persist NaN (can occur before first layout) or an off-screen
        // position that slipped through.
        if (double.IsNaN(Left) || double.IsNaN(Top)) return;
        try
        {
            var prefs = SenseEase.App.Configuration.UserPreferences.Current;
            prefs.HudLeft = Left;
            prefs.HudTop = Top;
            prefs.Save();
        }
        catch { /* best-effort persistence; never throw out of a move handler */ }
    }

    // Top-center revision: per-Show state reset + slide-from-top entrance.
    // IsVisibleChanged fires on both Show and Hide (Loaded fires only once per
    // construction). On Show we reset Opacity, the idle timer, the in-flight
    // chat-send guard, and any pending fade storyboard so the card recovers
    // cleanly across repeated Show/Hide cycles.
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            // Cancel any leftover animation (idle-fade may have set Opacity to 0,
            // or a slide-out exit may be holding SlideTransform.Y at -12, via
            // animations still attached to those properties). BeginAnimation(null)
            // detaches them so the entrance / a11y instant-set below actually
            // takes effect.
            BeginAnimation(OpacityProperty, null);
            SlideTransform?.BeginAnimation(TranslateTransform.YProperty, null);
            PopScale?.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            PopScale?.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _activeFadeStoryboard?.Stop(this);
            _activeFadeStoryboard = null;

            // HUD-card rework item 3: the card window is constructed once and
            // re-Shown across the session, so the theme could have flipped
            // (Settings → Appearance, or an OS Auto flip) since the one-time
            // SourceInitialized acrylic call. Re-pick the theme-matching scrim on
            // every Show so a card opened after a light↔dark switch frosts
            // correctly. The {DynamicResource Surface} fill already swaps via the
            // ThemeManager palette merge on Loaded.
            ApplyThemeAwareAcrylic();

            // Recover from a stuck in-flight chat send. If the previous
            // SendChatMessage callback threw before the finally block could
            // re-enable the UI, _chatSendInFlight would be left true and the
            // input/button would stay disabled across Hides. Reset here as a
            // belt-and-suspenders safeguard.
            _chatSendInFlight = false;
            if (ChatSendBtn is not null)  ChatSendBtn.IsEnabled = true;
            if (ChatInputBox is not null) ChatInputBox.IsEnabled = true;

            // Per-Show hygiene: prior typing or status text shouldn't carry
            // across Hide→Show. ProposalSafetyNoteText is intentionally left
            // alone — its lifecycle is tied to the active-proposal flow.
            if (ChatInputBox is not null)   ChatInputBox.Text = string.Empty;
            if (ChatStatusText is not null) ChatStatusText.Text = string.Empty;

            // Top-center revision: a user-opened card lingers (60 s idle window);
            // an auto-surfaced card self-dismisses after a short 5 s window so it
            // never nags. Choose the timeout BEFORE (re)starting the idle timer.
            IdleTimeout = UserOpened ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(5);
            RestartIdleTimer();

            // Slide-from-top entrance. C1 (a11y): Reduce Motion → appear
            // instantly (no slide / no fade); the card pops for a user who may
            // already feel unwell, so respect their setting.
            if (SenseEase.App.Accessibility.AccessibilityHelper.ReducedMotion())
            {
                Opacity = 1;
                if (SlideTransform is not null) SlideTransform.Y = 0;
                if (PopScale is not null) { PopScale.ScaleX = 1; PopScale.ScaleY = 1; }
            }
            else
            {
                BeginSlideIn();
            }
        }
    }

    /// <summary>Top-center revision: ~200 ms slide-from-top entrance — the card eases
    /// down from -10 dip while fading 0→1 AND popping in from 0.96→1.0 scale (CubicEase
    /// EaseOut, centre origin) for a softer, more fluent appearance than a bare slide.
    /// Runs only when Reduce Motion is off; the a11y path in
    /// <see cref="OnIsVisibleChanged"/> snaps straight to the resting state instead.</summary>
    private void BeginSlideIn()
    {
        Opacity = 0;
        if (SlideTransform is not null) SlideTransform.Y = -10;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = new Duration(TimeSpan.FromMilliseconds(200));

        var opacityIn = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };
        BeginAnimation(OpacityProperty, opacityIn);

        if (SlideTransform is not null)
        {
            var slideIn = new DoubleAnimation(-10, 0, dur) { EasingFunction = ease };
            SlideTransform.BeginAnimation(TranslateTransform.YProperty, slideIn);
        }

        // Subtle scale "pop" 96% → 100% from the centre, so the card grows in rather
        // than only sliding. Base ScaleX/Y stay 1.0 (set in XAML) so clearing the
        // animation reverts to full size — we only animate the From value here.
        if (PopScale is not null)
        {
            var scaleIn = new DoubleAnimation(0.96, 1.0, dur) { EasingFunction = ease };
            PopScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
            PopScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn);
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

    /// <summary>HUD-card rework item 2: set the level read-out that sits BETWEEN
    /// the [ − ] and [ + ] intervention pills (e.g. "L5"). The host calls this
    /// from the current VimsTier before Show() so the cluster reads
    /// [ − ] [ L5 ] [ + ]. Clamped to the native 12-level scale for display
    /// sanity; the value is purely cosmetic (the +/- buttons drive the engine,
    /// not this label).</summary>
    public void SetLevel(int level)
    {
        int clamped = Math.Max(1, Math.Min(12, level));
        if (LevelText is not null) LevelText.Text = $"L{clamped}";
    }

    private void ApplyContentToUi()
    {
        if (_isPowerSaveActive)
        {
            TitleText.Text = SenseEase.App.Localization.LocalizationManager.Current.Pick(
                "VIMS · 已暫停", "VIMS · Paused");
            TelemetryText.Text = string.Empty;
            BodyText.Text  = SenseEase.App.Localization.LocalizationManager.Current.Pick(
                "防護已暫停，點一下即可恢復。", "Protection is paused. Tap to resume.");
            return;
        }

        // Default normal-state body: derive from the tier band so callers
        // that haven't called SetContent yet still get a sensible placeholder.
        string body = DescriptionForBand(_lastBand);
        if (_hasContent)
        {
            // v0.9.1 round-5: localized title. zh-only below the VIMS product
            // term per user spec — "VIMS 視覺壓力 第 N 級 / 12 · 壓力指數 P"
            // (native 12-level scale — Adaptive Fit Q2=B; was / 10).
            // Removed the internal "(mask at 85)" debug suffix from the
            // user-facing surface; the dashboard chart still surfaces the 85
            // threshold for power users. Pressure is shown WITHOUT a "/100"
            // denominator — LeakyIntegrator pressure has no engine-level
            // upper bound (real values can exceed 300 under sustained spikes)
            // so a "/100" suffix would mislead users into thinking the scale
            // caps at 100.
            // BC-1: the headline is the LANGUAGE-AWARE 5級「動態感」status label — the ONLY
            // user-facing status (the screen-motion indicator), self-contained
            // ("動態感 · 極低" / "Very Calm"). Single source: ComfortBands.FeelLabelLocalized
            // (tier ≤ 0 → 準備中 / Getting ready). Re-picks live on a language toggle
            // (the LocalizationManager subscription re-runs this setter).
            TitleText.Text = SenseEase.App.Converters.ComfortBands.FeelLabelLocalized(
                _lastTier, SenseEase.App.Localization.LocalizationManager.Current.IsEnglish);
            TelemetryText.Text = $"第 {_lastTier} 級 / 12　·　壓力 {Math.Round(_lastPressure)}";
        }
        else
        {
            // No SetContent yet — keep the title empty (matches the v1
            // "shown but unpopulated" state).
            TitleText.Text = string.Empty;
            TelemetryText.Text = string.Empty;
        }
        BodyText.Text = body;
    }

    // The HUD's primary label is now the LANGUAGE-AWARE 5級 動態感 motion label,
    // produced inline in ApplyContentToUi via ComfortBands.FeelLabelLocalized
    // (single source shared with the dashboard hero / pill / tray). The old
    // band→3-token headline map (平穩/上升/較高) was retired with that switch.

    // D4: calm, app-action body copy — what SenseEase is doing, no detective
    // "偵測到…" register, no urgent imperatives (retires the banned phrases).
    // 5級 動態感 collapse: VeryHigh & High → old Grey/calm copy; Medium → old
    // Amber copy; Low & VeryLow → old Red copy (reuses the existing 3 strings).
    private static string DescriptionForBand(TierColorPalette.TierBand band) => band switch
    {
        TierColorPalette.TierBand.VeryHigh => "畫面平穩，一切安好。",
        TierColorPalette.TierBand.High     => "畫面平穩，一切安好。",
        TierColorPalette.TierBand.Medium   => "畫面開始活躍，正在輕輕緩和邊緣。需要的話可以短暫休息。",
        TierColorPalette.TierBand.Low      => "畫面變化較大，正在加強緩和。可以的話稍微休息一下。",
        TierColorPalette.TierBand.VeryLow  => "畫面變化較大，正在加強緩和。可以的話稍微休息一下。",
        _                                  => string.Empty,
    };

    private void OnCardMouseMove(object sender, MouseEventArgs e)
    {
        // Reset the idle timer AND cancel any in-progress idle-fade (the
        // reversible "last-chance pull-back" path): any movement inside the
        // CardBorder snaps opacity back to 1 and defers the idle dismiss.
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

    /// <summary>Top-center revision: dismiss with a ~150 ms slide-up exit — the
    /// reverse of the entrance (Y 0→-12, Opacity 1→0, QuadraticEase EaseOut) then
    /// <see cref="Window.Hide"/>. Under Reduce Motion the card hides instantly
    /// (no slide). Used by the explicit dismiss affordances (× / Escape / 稍後);
    /// the 60 s idle path keeps its own gentle 5 s opacity fade.</summary>
    private void HideWithSlide()
    {
        if (SenseEase.App.Accessibility.AccessibilityHelper.ReducedMotion())
        {
            Hide();
            return;
        }

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var dur = new Duration(TimeSpan.FromMilliseconds(150));

        var opacityOut = new DoubleAnimation(1, 0, dur) { EasingFunction = ease };
        opacityOut.Completed += (_, _) => Hide();
        BeginAnimation(OpacityProperty, opacityOut);

        if (SlideTransform is not null)
        {
            var slideOut = new DoubleAnimation(0, -12, dur) { EasingFunction = ease };
            SlideTransform.BeginAnimation(TranslateTransform.YProperty, slideOut);
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => HideWithSlide();

    /// <summary>C2 (audit): keyboard escape route. Pressing Escape while the
    /// card has focus dismisses it — the same gentle slide-up <see cref="Hide"/>
    /// as the × button (never quits the app). Apple HIG / WCAG both require a
    /// cancel/back affordance on every transient surface; the card is one.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            HideWithSlide();
            return;
        }
        base.OnKeyDown(e);
    }

    private void OnEngageClicked(object sender, RoutedEventArgs e)
    {
        EngageClicked?.Invoke(this, EventArgs.Empty);
        Hide();
    }

    /// <summary>bar-redesign: "+" intervention-depth pill — ease MORE (deeper).
    /// Raises <see cref="InterventionAdjustRequested"/> with +1. Per spec a +/-
    /// nudge keeps the card OPEN (no Hide) so the user can keep adjusting; the
    /// host routes the event to the same engine ease path Engage uses. We do NOT
    /// also raise <see cref="EngageClicked"/> here — that handler hides the card
    /// and would double-apply the ease — keeping the +/- contract single-shot.</summary>
    private void OnInterventionUpClicked(object sender, RoutedEventArgs e)
    {
        RestartIdleTimer();
        InterventionAdjustRequested?.Invoke(this, +1);
    }

    /// <summary>bar-redesign: "−" intervention-depth pill — ease LESS (widen).
    /// Raises <see cref="InterventionAdjustRequested"/> with -1. Keeps the card
    /// open.</summary>
    private void OnInterventionDownClicked(object sender, RoutedEventArgs e)
    {
        RestartIdleTimer();
        InterventionAdjustRequested?.Invoke(this, -1);
    }

    /// <summary>bar-redesign: flip the bar between its main face (status +
    /// controls) and its chat face (the deliberate route to the chatbot). Both
    /// the main-face flip glyph (↩) and the chat-face back arrow (←) call this —
    /// it simply toggles which face is visible. The flip is gated behind
    /// ReducedMotion so a user who feels unwell never gets an animation; we keep
    /// the swap instant either way (no animation is applied here), but route the
    /// branch so a future cross-fade would only run when motion is allowed.</summary>
    private void OnFlipClicked(object sender, RoutedEventArgs e)
    {
        RestartIdleTimer();
        bool toChat = MainFace.Visibility == Visibility.Visible;

        if (SenseEase.App.Accessibility.AccessibilityHelper.ReducedMotion())
        {
            // Reduce Motion: swap instantly, no transition.
            MainFace.Visibility = toChat ? Visibility.Collapsed : Visibility.Visible;
            ChatFace.Visibility = toChat ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            // Motion allowed: same instant swap today; a cross-fade could be
            // attached here behind this branch without touching the a11y path.
            MainFace.Visibility = toChat ? Visibility.Collapsed : Visibility.Visible;
            ChatFace.Visibility = toChat ? Visibility.Visible : Visibility.Collapsed;
        }

        if (toChat) ChatInputBox.Focus();
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
    /// when the host hasn't wired it).
    /// <para>UI Consolidation Sprint Phase 1.5 (2026-05-30, user feedback):
    /// changed from modal <c>ShowDialog</c> to non-modal <c>Show</c> so the
    /// user can keep interacting with the main console AND the Pop-up Card
    /// while Settings is open. The card's idle-fade timer is suspended for
    /// the lifetime of the SettingsWindow and restarted via its
    /// <see cref="Window.Closed"/> event (instead of the synchronous
    /// <c>finally</c> block that <c>ShowDialog</c> required).</para></summary>
    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        // Stop the card's idle-fade timer + cancel any in-progress fade so
        // the card doesn't disappear behind the Settings window after 60s.
        _idleTimer.Stop();
        if (_activeFadeStoryboard is not null)
        {
            _activeFadeStoryboard.Stop(this);
            _activeFadeStoryboard = null;
            Opacity = 1.0;
        }

        // NE24: single-instance guard. If a Settings window opened from this gear
        // is already up, just re-surface it rather than stacking a duplicate.
        if (_openSettingsWindow is not null)
        {
            if (_openSettingsWindow.WindowState == WindowState.Minimized)
                _openSettingsWindow.WindowState = WindowState.Normal;
            _openSettingsWindow.Activate();
            return;
        }

        var diagFn = HasVestibularDiagnosis ?? (() => false);
        var win = new SettingsWindow(diagFn) { Owner = this };
        _openSettingsWindow = win;
        // Path Z bug-fix 7: forward PreferencesChanged to the host so the
        // bar/tray repaint immediately on Toggle B change — matching the
        // tray-menu Settings path.
        if (OnPreferencesChanged is not null)
            win.PreferencesChanged += (_, _) => OnPreferencesChanged();
        // Phase 1.5: non-modal handoff. Without ShowDialog's synchronous
        // wait, we need an asynchronous restart of the idle timer keyed to
        // Settings actually closing. NE24: also drop the single-instance
        // reference so the next gear click opens a fresh window.
        win.Closed += (_, _) =>
        {
            _openSettingsWindow = null;
            RestartIdleTimer();
        };
        win.Show();
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

    // bar-redesign: the four QuickDiscomfort buttons (and their localized-phrase
    // table + OnQuickDiscomfortClicked handler) were removed when the card became
    // a compact bar — the intervention-depth +/- pills (InterventionAdjustRequested)
    // now carry the "adjust how much" control, and the chat face carries free-text.

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
            ChatStatusText.Text = "Chatbot 尚未連線。";
            return;
        }

        _chatSendInFlight = true;
        ChatSendBtn.IsEnabled = false;
        ChatInputBox.IsEnabled = false;
        ChatStatusText.Text = "傳送中…";
        // Treat any in-card activity as user interaction — defer the idle fade.
        RestartIdleTimer();

        string? reply = null;
        try
        {
            reply = await SendChatMessage(text).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ChatStatusText.Text = $"傳送失敗：{ex.GetType().Name}";
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
            else if (ChatStatusText.Text == "傳送中…")
            {
                ChatStatusText.Text = "已套用。";
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
