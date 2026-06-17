using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SenseEase.App.Localization;

namespace SenseEase.App.ViewModels;

/// <summary>Who authored a popup-chat bubble. (Prefixed <c>Popup*</c> to avoid colliding
/// with the existing <see cref="SenseEase.Core.Models.ChatRole"/>.)</summary>
public enum PopupChatRole { Bot, User }

/// <summary>An inline answer button under a bot bubble (e.g. an FMS option, or
/// Accept/Decline/Later on a habituation proposal).</summary>
public sealed class PopupChatOption
{
    public PopupChatOption(string label, ICommand command) { Label = label; Command = command; }
    public string Label { get; }
    public ICommand Command { get; }
}

/// <summary>One popup-chat message. Bubbles are created fully-formed and added to
/// <see cref="PopupChatViewModel.Messages"/>; the computed bools drive the XAML
/// alignment / visibility triggers.</summary>
public sealed class PopupChatBubble
{
    public PopupChatBubble(string text, PopupChatRole role,
                           bool isSystemNotice = false,
                           ICommand? undoCommand = null,
                           IReadOnlyList<PopupChatOption>? options = null)
    {
        Text = text;
        Role = role;
        IsSystemNotice = isSystemNotice;
        UndoCommand = undoCommand;
        Options = options;
    }

    public string Text { get; }
    public PopupChatRole Role { get; }
    public bool IsSystemNotice { get; }
    public ICommand? UndoCommand { get; }
    public IReadOnlyList<PopupChatOption>? Options { get; }

    public bool IsUser => Role == PopupChatRole.User;
    public bool IsBot => Role == PopupChatRole.Bot;
    public bool HasUndo => UndoCommand is not null;
    public bool HasOptions => Options is { Count: > 0 };
}

/// <summary>
/// UI-RESHAPE prep — self-contained view-model for the slide-out AI chat panel. It is
/// deliberately decoupled from the real <c>ChatbotViewModel</c> / NLU engine (which the
/// parallel B5 work is changing); the future <c>ShellController</c> bridges the two
/// post-B5. The window binds only members declared here.
///
/// <para><b>System-notice + Undo</b> (<see cref="AddSystemNotice"/>) and the inline
/// <b>followup question</b> (<see cref="AskFollowup"/>) are the reusable patterns the B5
/// Adaptation-Gate notice ("I eased your overlay a little — Undo?") and the gated
/// FMS / habituation check-in will call into.</para>
/// </summary>
public sealed class PopupChatViewModel : INotifyPropertyChanged
{
    public ObservableCollection<PopupChatBubble> Messages { get; } = new();

    public PopupChatViewModel()
    {
        SendCommand = new RelayCommand(_ => Send(), _ => !string.IsNullOrWhiteSpace(Draft));
        ClearCommand = new RelayCommand(_ => Messages.Clear());
        // Seed the greeting (mirrors the reference HTML's first bot bubble).
        Messages.Add(new PopupChatBubble(
            LocalizationManager.Current.Pick(
                "目前偵測到中度動態畫面。有什麼我可以幫你的嗎？",
                "Moderate on-screen motion right now. How can I help?"),
            PopupChatRole.Bot));
    }

    private string _draft = string.Empty;
    public string Draft
    {
        get => _draft;
        set { if (_draft != value) { _draft = value; OnPropertyChanged(); (SendCommand as RelayCommand)?.RaiseCanExecuteChanged(); } }
    }

    public ICommand SendCommand { get; }
    public ICommand ClearCommand { get; }

    private void Send()
    {
        var text = Draft?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        Messages.Add(new PopupChatBubble(text, PopupChatRole.User));
        Draft = string.Empty;
        // Placeholder reply — the real NLU pipeline is wired through ShellController post-B5.
        Messages.Add(new PopupChatBubble(
            LocalizationManager.Current.Pick("（SenseEase AI 連線中…）", "(SenseEase AI is connecting…)"),
            PopupChatRole.Bot));
    }

    /// <summary>Append a one-tap-reversible system notice (the B5 "I eased your overlay…
    /// Undo?" pattern). The Undo button fires <paramref name="onUndo"/> once.</summary>
    public void AddSystemNotice(string text, Action onUndo)
    {
        ICommand undo = new RelayCommand(_ => onUndo());
        Messages.Add(new PopupChatBubble(text, PopupChatRole.Bot, isSystemNotice: true, undoCommand: undo));
    }

    /// <summary>Append a bot question that renders <paramref name="options"/> as inline
    /// answer buttons (the gated FMS / habituation-proposal check-in pattern).</summary>
    public void AskFollowup(string question, IReadOnlyList<PopupChatOption> options)
        => Messages.Add(new PopupChatBubble(question, PopupChatRole.Bot, options: options));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
