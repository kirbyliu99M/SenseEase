using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SenseEase.App.Localization;
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
        StrongerCommand      = new RelayCommand(_ => Stronger());
        GentlerCommand       = new RelayCommand(_ => Gentler());
        ToggleChatCommand    = new RelayCommand(_ => ToggleChat());
        ToggleSettingsCommand = new RelayCommand(_ => IsSettingsOpen = !IsSettingsOpen);
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
            OnPropertyChanged(nameof(Segments));
        }
    }

    public int Level => VimsTier;
    public string LevelText => $"Lv.{Level:00}";

    /// <summary>12 segment on/off flags, filled bottom-up (index 0 = top): segment i is
    /// lit iff i ≥ 12 − Level. The window's ItemsControl binds each bool via SegmentFillConverter.</summary>
    public bool[] Segments
    {
        get
        {
            var segs = new bool[MaxLevel];
            for (int i = 0; i < MaxLevel; i++) segs[i] = i >= MaxLevel - Level;
            return segs;
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

    public string ProfileName => LocalizationManager.Current.Pick("一般設定檔", "Typical profile");

    // ── Commands ─────────────────────────────────────────────────────────────
    public ICommand PlayPauseCommand { get; }
    public ICommand StrongerCommand { get; }
    public ICommand GentlerCommand { get; }
    public ICommand ToggleChatCommand { get; }
    public ICommand ToggleSettingsCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand OpenFullSettingsCommand { get; }

    private void PlayPause()
    {
        if (_controller is not null) { _controller.PlayPause(); return; }
        IsPaused = !IsPaused; // standalone optimistic
    }

    private void Stronger()
    {
        if (_controller is not null) { _controller.NudgeStronger(); return; }
        VimsTier = VimsTier + 1; // standalone optimistic display
    }

    private void Gentler()
    {
        if (_controller is not null) { _controller.NudgeGentler(); return; }
        VimsTier = VimsTier - 1;
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
        VimsTier = tier;
        IsPaused = paused;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
