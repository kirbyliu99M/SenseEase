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
    private readonly Action? _onIdleDismiss;

    public NpsSurveyViewModel(Action<int, string?>? onSubmit = null, Action? onDismiss = null, Action? onIdleDismiss = null)
    {
        _onSubmit = onSubmit;
        _onDismiss = onDismiss;
        _onIdleDismiss = onIdleDismiss;
        SelectScoreCommand = new RelayCommand(p => SelectedScore = AsScore(p));
        SubmitCommand = new RelayCommand(_ => Submit(), _ => SelectedScore.HasValue);
        DismissCommand = new RelayCommand(_ => _onDismiss?.Invoke());
    }

    /// <summary>The 5-point satisfaction scale (1 = poor … 5 = great). Iter 3 (kirby):
    /// the survey is now a "five-point QA", not the 0–10 NPS scale.</summary>
    public int[] Scores { get; } = { 1, 2, 3, 4, 5 };

    public string Prompt => LocalizationManager.Current.Pick(
        "您對 SenseEase 的整體感受如何？",
        "How would you rate your experience with SenseEase?");

    /// <summary>Low/high meaning anchors shown small + subtle ABOVE the 1–5 row (over the end buttons) so
    /// the scale direction is unambiguous. No "1 ="/"5 =" prefix — the numbers are right below.</summary>
    public string ScaleLowLabel  => LocalizationManager.Current.Pick("不滿意", "poor");
    public string ScaleHighLabel => LocalizationManager.Current.Pick("非常滿意", "great");

    /// <summary>Free-text label for the comment field (kirby: the fill bar is "其他建議").</summary>
    public string CommentLabel => LocalizationManager.Current.Pick("其他建議（選填）", "Other suggestions (optional)");

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

    /// <summary>The card timed out un-answered (idle auto-dismiss) — a WEAKER signal than an explicit
    /// "Not now", routed to <c>onIdleDismiss</c> so the host arms the short cooldown, not the 90-day quiet
    /// window. Falls back to <c>onDismiss</c> when no idle handler is wired (design-time / tests).</summary>
    public void NotifyIdleDismiss() => (_onIdleDismiss ?? _onDismiss)?.Invoke();

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
