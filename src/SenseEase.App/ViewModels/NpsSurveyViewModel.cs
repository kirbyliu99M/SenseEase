using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SenseEase.App.Localization;

namespace SenseEase.App.ViewModels;

/// <summary>
/// UI-RESHAPE prep — self-contained view-model for the subtle Teams-style NPS survey
/// window. The host injects <c>onSubmit</c> / <c>onDismiss</c> (which forward to
/// <see cref="Services.NpsService"/>); with neither injected it is a safe no-op, so the
/// window constructs cleanly under smoke tests + design-time.
/// </summary>
public sealed class NpsSurveyViewModel : INotifyPropertyChanged
{
    private readonly Action<int, string?>? _onSubmit;
    private readonly Action? _onDismiss;

    public NpsSurveyViewModel(Action<int, string?>? onSubmit = null, Action? onDismiss = null)
    {
        _onSubmit = onSubmit;
        _onDismiss = onDismiss;
        SelectScoreCommand = new RelayCommand(p => SelectedScore = AsScore(p));
        SubmitCommand = new RelayCommand(_ => Submit(), _ => SelectedScore.HasValue);
        DismissCommand = new RelayCommand(_ => _onDismiss?.Invoke());
    }

    /// <summary>The 0–10 likelihood-to-recommend scale.</summary>
    public int[] Scores { get; } = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

    public string Prompt => LocalizationManager.Current.Pick(
        "喜歡 SenseEase 嗎？您有多大可能推薦給朋友？",
        "Enjoying SenseEase? How likely are you to recommend it?");

    public string CommentPlaceholder => LocalizationManager.Current.Pick(
        "想多說一點嗎？（選填）", "Anything to add? (optional)");

    private int? _selectedScore;
    public int? SelectedScore
    {
        get => _selectedScore;
        set
        {
            if (_selectedScore == value) return;
            _selectedScore = value;
            OnPropertyChanged();
            (SubmitCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private string? _comment;
    public string? Comment
    {
        get => _comment;
        set { if (_comment != value) { _comment = value; OnPropertyChanged(); } }
    }

    public ICommand SelectScoreCommand { get; }
    public ICommand SubmitCommand { get; }
    public ICommand DismissCommand { get; }

    private void Submit()
    {
        if (!SelectedScore.HasValue) return;
        _onSubmit?.Invoke(SelectedScore.Value, string.IsNullOrWhiteSpace(Comment) ? null : Comment!.Trim());
    }

    private static int? AsScore(object? p) => p switch
    {
        int i => i,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) => v,
        _ => null,
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
