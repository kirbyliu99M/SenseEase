using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SenseEase.App.Services;

namespace SenseEase.App.ViewModels;

/// <summary>
/// UI-RESHAPE prep — self-contained view-model for the glass popup bar. It binds to an
/// optional <see cref="IShellController"/> (the decoupling seam); with no controller it
/// behaves as a fully-functional standalone UI (so smoke tests + design-time work),
/// updating its own optimistic state. When a controller is present, the engine owns the
/// real tier and pushes it back via <see cref="ApplyState"/> on
/// <see cref="IShellController.StateChanged"/>.
///
/// <para>The 12-segment strength meter is READ-ONLY display (the engine owns the level);
/// the +/- buttons are deliberate-preference NUDGES routed to the controller, not direct
/// setters — matching the locked VimsTier read-only contract.</para>
/// </summary>
public sealed class PopupBarViewModel : INotifyPropertyChanged
{
    public const int MaxLevel = 12;

    private readonly IShellController? _controller;

    /// <summary>The REAL AI chat (NLU + B5 adaptation notices + gated FMS/proposal followups all
    /// flow into its Messages). Null in design-time / tests; the chat panel binds Chat.* safely.</summary>
    public ChatbotViewModel? Chat { get; }

    public PopupBarViewModel(IShellController? controller = null, ChatbotViewModel? chat = null)
    {
        _controller = controller;
        Chat = chat;

        PlayPauseCommand     = new RelayCommand(_ => PlayPause());
        ToggleChatCommand    = new RelayCommand(_ => ToggleChat());
        ToggleSettingsCommand = new RelayCommand(_ => IsSettingsOpen = !IsSettingsOpen);
        ToggleExpandCommand  = new RelayCommand(_ => IsExpanded = !IsExpanded);
        OpenLogsCommand      = new RelayCommand(_ => _controller?.OpenLogs());
        OpenFullSettingsCommand = new RelayCommand(_ => _controller?.OpenSettings());

        if (_controller is not null)
        {
            _controller.StateChanged += (_, _) => ApplyState(_controller.CurrentTier, _controller.IsPaused);
            ApplyState(_controller.CurrentTier, _controller.IsPaused);
        }
    }

    // ── State ────────────────────────────────────────────────────────────────
    private int _vimsTier = 5;
    /// <summary>Engine VIMS tier (1–12). Read-only from the UI's perspective; the
    /// segments + pulse converters bind to it OneWay.</summary>
    public int VimsTier
    {
        get => _vimsTier;
        private set
        {
            var clamped = Math.Max(1, Math.Min(MaxLevel, value));
            if (_vimsTier == clamped) return;
            _vimsTier = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Level));
            OnPropertyChanged(nameof(LevelText));
            OnPropertyChanged(nameof(IntensityLevel));
        }
    }

    public int Level => VimsTier;
    public string LevelText => $"Lv.{Level:00}";

    // ── Intensity (item 9) — TWO-WAY for the drag slider. get = the engine tier; set routes a user
    // drag to the controller's ABSOLUTE setter (the engine owns the level, so we never write VimsTier
    // directly when a controller is present). Feedback-loop-guarded: _applyingEngineLevel is set while
    // ApplyState pushes engine state, and a set equal to the current tier is a no-op (snap echo).
    private bool _applyingEngineLevel;
    public int IntensityLevel
    {
        get => VimsTier;
        set
        {
            if (_applyingEngineLevel) return;
            var clamped = Math.Max(1, Math.Min(MaxLevel, value));
            if (clamped == VimsTier) return;
            if (_controller is not null) _controller.SetIntensityLevel(clamped);
            else VimsTier = clamped; // standalone optimistic (design-time / tests)
        }
    }

    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        private set { if (_isPaused != value) { _isPaused = value; OnPropertyChanged(); } }
    }

    private bool _isChatOpen;
    public bool IsChatOpen
    {
        get => _isChatOpen;
        set
        {
            if (_isChatOpen == value) return;
            _isChatOpen = value;
            if (value) IsSettingsOpen = false; // mutually exclusive — only one panel open at a time
            OnPropertyChanged();
        }
    }

    private bool _isSettingsOpen;
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set
        {
            if (_isSettingsOpen == value) return;
            _isSettingsOpen = value;
            if (value) IsChatOpen = false; // mutually exclusive
            OnPropertyChanged();
        }
    }

    // ── Two-stage reveal (iter 3) ──────────────────────────────────────────────
    // The bar reveals CONDENSED (status · power · chat); the expand chevron grows it to
    // FULL (adds the ±strength meter, settings gear, pin). Click-to-expand — no auto-expand
    // timer (an unpredictable size change = involuntary motion, bad for a motion-sickness tool).
    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            if (!value) IsSettingsOpen = false; // the gear leaves with the expanded set
            OnPropertyChanged();
        }
    }

    // Quick-settings toggles — wired to the engine through the controller (錨點格線 / 漸進適應 /
    // 常駐遮罩). The getters read LIVE engine state so the switches reflect reality on open; with no
    // controller (design-time / tests) they fall back to local state so the UI still works.
    private bool _anchorGridOn = true;
    public bool AnchorGridOn
    {
        get => _controller?.AnchorGridEnabled ?? _anchorGridOn;
        set { if (AnchorGridOn == value) return; _anchorGridOn = value; if (_controller is not null) _controller.AnchorGridEnabled = value; OnPropertyChanged(); }
    }

    private bool _gradedExposureOn = true;
    public bool GradedExposureOn
    {
        get => _controller?.GradedExposureEnabled ?? _gradedExposureOn;
        set { if (GradedExposureOn == value) return; _gradedExposureOn = value; if (_controller is not null) _controller.GradedExposureEnabled = value; OnPropertyChanged(); }
    }

    private bool _persistentMaskOn;
    public bool PersistentMaskOn
    {
        get => _controller?.PersistentMaskEnabled ?? _persistentMaskOn;
        set { if (PersistentMaskOn == value) return; _persistentMaskOn = value; if (_controller is not null) _controller.PersistentMaskEnabled = value; OnPropertyChanged(); }
    }

    /// <summary>True when the local chat LLM is loaded + available (drives the chat-header dot).</summary>
    public bool IsLlmOnline => _controller?.IsLlmOnline ?? (Chat?.IsLlmOnline ?? false);

    // ── Commands ─────────────────────────────────────────────────────────────
    public ICommand PlayPauseCommand { get; }
    public ICommand ToggleChatCommand { get; }
    public ICommand ToggleSettingsCommand { get; }
    public ICommand ToggleExpandCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand OpenFullSettingsCommand { get; }

    private void PlayPause()
    {
        if (_controller is not null) { _controller.PlayPause(); return; }
        IsPaused = !IsPaused; // standalone optimistic
    }

    private void ToggleChat()
    {
        IsChatOpen = !IsChatOpen;
        if (IsChatOpen) _controller?.OpenChat();
    }

    /// <summary>Push live engine state into the bar (called by the host on
    /// <see cref="IShellController.StateChanged"/>).</summary>
    public void ApplyState(int tier, bool paused)
    {
        // Guard the slider's two-way binding: while the engine pushes its tier, suppress the
        // IntensityLevel setter so the echo can't re-enter the controller.
        _applyingEngineLevel = true;
        try { VimsTier = tier; IsPaused = paused; }
        finally { _applyingEngineLevel = false; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
