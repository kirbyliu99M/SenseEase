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

    public PopupChatViewModel Chat { get; } = new();

    public PopupBarViewModel(IShellController? controller = null)
    {
        _controller = controller;

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
        set { if (_isChatOpen != value) { _isChatOpen = value; OnPropertyChanged(); } }
    }

    private bool _isSettingsOpen;
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set { if (_isSettingsOpen != value) { _isSettingsOpen = value; OnPropertyChanged(); } }
    }

    // Settings-dropdown toggles (local UI state for now; wired to engine prefs post-B5).
    private bool _fovMaskOn = true;
    public bool FovMaskOn { get => _fovMaskOn; set { if (_fovMaskOn != value) { _fovMaskOn = value; OnPropertyChanged(); } } }

    private bool _anchorGridOn = true;
    public bool AnchorGridOn { get => _anchorGridOn; set { if (_anchorGridOn != value) { _anchorGridOn = value; OnPropertyChanged(); } } }

    private bool _autoAdjustOn = true;
    public bool AutoAdjustOn { get => _autoAdjustOn; set { if (_autoAdjustOn != value) { _autoAdjustOn = value; OnPropertyChanged(); } } }

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
