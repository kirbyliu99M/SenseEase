using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SenseEase.Core.Inference;

namespace SenseEase.App.ViewModels;

// ─────────────────────────────────────────────────────────────────────────────
// CalibrationCardViewModel — 5-question state machine for the first-time
// calibration (buttons path). Owned by ChatbotViewModel (or MainViewModel,
// depending on Stage 3 final wiring); the host sets it to non-null to surface
// the card UI and to null to dismiss.
//
// The 5 cards correspond to spec §3:
//   Card 1: Q1 — uncomfortable segment (single-select + freetext)
//   Card 2: Q2 — symptom dominance   (multi-select + freetext)
//   Card 3: Q3 — intensity 1-5       (single-select, MANDATORY + freetext)
//   Card 4: Q4 — baseline comparison (single-select + freetext)
//   Card 5: Q5 — risk factors        (multi-select + freetext)
//
// Multi-select mutual-exclusivity rules:
//   Q2: "都沒有" (Q2None) is mutually exclusive with all six symptom options.
//   Q5: "跳過不答" (Q5Skipped) is mutually exclusive with everything (including
//       "都沒有"). "都沒有" (Q5None) is mutually exclusive with the five real
//       risk options but coexists ONLY with itself.
//
// Q3 mandatory rule: CanAdvance is false on card 3 until Q3Intensity is set.
// On other cards CanAdvance is always true (skipping is allowed per spec §3).
//
// Dismiss semantics:
//   - Cancel mid-flow with Q3 unanswered → CalibrationCompleteness.Abandoned
//   - Cancel mid-flow with Q3 answered  → CalibrationCompleteness.Partial
//   - Complete card 5                   → CalibrationCompleteness.Complete iff
//     ALL five primary fields are non-null; else Partial.
// Completion classification is handled by CalibrationProcessor itself; the VM
// just packs CalibrationResponses and lets the processor decide.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class CalibrationCardViewModel : INotifyPropertyChanged
{
    public const int CardCount = 5;

    public CalibrationCardViewModel()
    {
        NextCommand     = new RelayCommand(_ => GoNext(),     _ => CanAdvance);
        BackCommand     = new RelayCommand(_ => GoBack(),     _ => CurrentCardIndex > 0);
        CompleteCommand = new RelayCommand(_ => Complete(),   _ => CanAdvance && IsLastCard);
        CancelCommand   = new RelayCommand(_ => Cancel());
    }

    // ── Navigation state ────────────────────────────────────────────────────

    private int _currentCardIndex;
    public int CurrentCardIndex
    {
        get => _currentCardIndex;
        private set { if (Set(ref _currentCardIndex, value)) NotifyNavComputed(); }
    }

    public bool IsLastCard => CurrentCardIndex == CardCount - 1;

    /// <summary>"Card 3 / 5" style label for the UI header.</summary>
    public string CardCounterText => $"{CurrentCardIndex + 1} / {CardCount}";

    /// <summary>Gates the Next / Complete button. Mandatory on card 3 only.</summary>
    public bool CanAdvance => CurrentCardIndex switch
    {
        2 => Q3Intensity is not null, // Card 3 is Q3 → mandatory
        _ => true,
    };

    // Visibility helpers for the XAML — only one card visible at a time.
    public bool IsCard1Visible => CurrentCardIndex == 0;
    public bool IsCard2Visible => CurrentCardIndex == 1;
    public bool IsCard3Visible => CurrentCardIndex == 2;
    public bool IsCard4Visible => CurrentCardIndex == 3;
    public bool IsCard5Visible => CurrentCardIndex == 4;

    // ── Commands ────────────────────────────────────────────────────────────

    public ICommand NextCommand     { get; }
    public ICommand BackCommand     { get; }
    public ICommand CompleteCommand { get; }
    public ICommand CancelCommand   { get; }

    private void GoNext()
    {
        if (!CanAdvance) return;
        if (CurrentCardIndex < CardCount - 1) CurrentCardIndex++;
    }

    private void GoBack()
    {
        if (CurrentCardIndex > 0) CurrentCardIndex--;
    }

    private void Complete()
    {
        if (!CanAdvance) return;
        Completed?.Invoke(this, BuildResponses());
    }

    private void Cancel()
    {
        // Caller decides whether to treat the cancel as Abandoned (Q3 null) or
        // Partial (Q3 answered) — we hand them the responses snapshot either way
        // and let CalibrationProcessor classify completeness.
        Cancelled?.Invoke(this, BuildResponses());
    }

    /// <summary>Fires when the user clicks Complete on card 5. Payload is the
    /// packed responses ready for <c>CalibrationProcessor.Process</c>.</summary>
    public event EventHandler<CalibrationResponses>? Completed;

    /// <summary>Fires when the user dismisses the card UI mid-flow.</summary>
    public event EventHandler<CalibrationResponses>? Cancelled;

    // ── Card 1 (Q1) ─────────────────────────────────────────────────────────

    private UncomfortableSegment? _q1Segment;
    public UncomfortableSegment? Q1Segment
    {
        get => _q1Segment;
        set => Set(ref _q1Segment, value);
    }
    private string? _q1Freetext;
    public string? Q1Freetext { get => _q1Freetext; set => Set(ref _q1Freetext, value); }

    // ── Card 2 (Q2 — multi-select with mutual exclusivity) ──────────────────
    // Stored as discrete booleans so the XAML can bind each chip directly to
    // one property. ToResponses() collects them into a SymptomType list.

    private bool _q2Dizziness;       public bool Q2Dizziness     { get => _q2Dizziness;     set { if (Set(ref _q2Dizziness, value)     && value) ClearQ2None(); } }
    private bool _q2Nausea;          public bool Q2Nausea        { get => _q2Nausea;        set { if (Set(ref _q2Nausea, value)        && value) ClearQ2None(); } }
    private bool _q2EyeStrain;       public bool Q2EyeStrain     { get => _q2EyeStrain;     set { if (Set(ref _q2EyeStrain, value)     && value) ClearQ2None(); } }
    private bool _q2Headache;        public bool Q2Headache      { get => _q2Headache;      set { if (Set(ref _q2Headache, value)      && value) ClearQ2None(); } }
    private bool _q2Imbalance;       public bool Q2Imbalance     { get => _q2Imbalance;     set { if (Set(ref _q2Imbalance, value)     && value) ClearQ2None(); } }
    private bool _q2Disorientation;  public bool Q2Disorientation{ get => _q2Disorientation;set { if (Set(ref _q2Disorientation, value)&& value) ClearQ2None(); } }

    /// <summary>"都沒有" — when checked, clears all six symptom flags. Mutually
    /// exclusive with them (and they clear it via <see cref="ClearQ2None"/>).</summary>
    private bool _q2None;
    public bool Q2None
    {
        get => _q2None;
        set
        {
            if (!Set(ref _q2None, value) || !value) return;
            _q2Dizziness = _q2Nausea = _q2EyeStrain
                = _q2Headache = _q2Imbalance = _q2Disorientation = false;
            // Notify XAML to redraw the now-unchecked chips.
            NotifyAllQ2Symptoms();
        }
    }

    private void ClearQ2None()
    {
        if (!_q2None) return;
        _q2None = false;
        OnPropertyChanged(nameof(Q2None));
    }

    private void NotifyAllQ2Symptoms()
    {
        OnPropertyChanged(nameof(Q2Dizziness));
        OnPropertyChanged(nameof(Q2Nausea));
        OnPropertyChanged(nameof(Q2EyeStrain));
        OnPropertyChanged(nameof(Q2Headache));
        OnPropertyChanged(nameof(Q2Imbalance));
        OnPropertyChanged(nameof(Q2Disorientation));
    }

    private string? _q2Freetext;
    public string? Q2Freetext { get => _q2Freetext; set => Set(ref _q2Freetext, value); }

    // ── Card 3 (Q3 — intensity, MANDATORY) ──────────────────────────────────

    private int? _q3Intensity;
    public int? Q3Intensity
    {
        get => _q3Intensity;
        set
        {
            if (Set(ref _q3Intensity, value))
            {
                // CanAdvance depends on this; recompute.
                OnPropertyChanged(nameof(CanAdvance));
                CommandsChanged();
            }
        }
    }

    private string? _q3Freetext;
    public string? Q3Freetext { get => _q3Freetext; set => Set(ref _q3Freetext, value); }

    // ── Card 4 (Q4) ─────────────────────────────────────────────────────────

    private BaselineComparison? _q4Comparison;
    public BaselineComparison? Q4Comparison
    {
        get => _q4Comparison;
        set
        {
            if (Set(ref _q4Comparison, value) && value is not null && _q4NoBaseline)
            {
                // Picking a baseline comparison clears the "I don't usually watch"
                // flag — the two are mutually exclusive.
                _q4NoBaseline = false;
                OnPropertyChanged(nameof(Q4NoBaseline));
            }
        }
    }

    private bool _q4NoBaseline;
    public bool Q4NoBaseline
    {
        get => _q4NoBaseline;
        set
        {
            if (Set(ref _q4NoBaseline, value) && value && _q4Comparison is not null)
            {
                _q4Comparison = null;
                OnPropertyChanged(nameof(Q4Comparison));
            }
        }
    }

    private string? _q4Freetext;
    public string? Q4Freetext { get => _q4Freetext; set => Set(ref _q4Freetext, value); }

    // ── Card 5 (Q5 — multi-select with two-tier mutual exclusivity) ────────

    private bool _q5MotionSickness;       public bool Q5MotionSickness       { get => _q5MotionSickness;       set { if (Set(ref _q5MotionSickness, value)       && value) ClearQ5NoneAndSkipped(); } }
    private bool _q5Migraine;             public bool Q5Migraine             { get => _q5Migraine;             set { if (Set(ref _q5Migraine, value)             && value) ClearQ5NoneAndSkipped(); } }
    private bool _q5VestibularDiagnosed;  public bool Q5VestibularDiagnosed  { get => _q5VestibularDiagnosed;  set { if (Set(ref _q5VestibularDiagnosed, value)  && value) ClearQ5NoneAndSkipped(); } }
    private bool _q5VrSusceptible;        public bool Q5VrSusceptible        { get => _q5VrSusceptible;        set { if (Set(ref _q5VrSusceptible, value)        && value) ClearQ5NoneAndSkipped(); } }
    private bool _q5UnexplainedDizziness; public bool Q5UnexplainedDizziness { get => _q5UnexplainedDizziness; set { if (Set(ref _q5UnexplainedDizziness, value) && value) ClearQ5NoneAndSkipped(); } }

    private bool _q5None;
    /// <summary>"都沒有". Clears the five risk options + Q5Skipped when set.</summary>
    public bool Q5None
    {
        get => _q5None;
        set
        {
            if (!Set(ref _q5None, value) || !value) return;
            _q5MotionSickness = _q5Migraine = _q5VestibularDiagnosed
                = _q5VrSusceptible = _q5UnexplainedDizziness = false;
            _q5Skipped = false;
            NotifyAllQ5();
        }
    }

    private bool _q5Skipped;
    /// <summary>"跳過不答". Mutually exclusive with EVERYTHING including Q5None.</summary>
    public bool Q5Skipped
    {
        get => _q5Skipped;
        set
        {
            if (!Set(ref _q5Skipped, value) || !value) return;
            _q5MotionSickness = _q5Migraine = _q5VestibularDiagnosed
                = _q5VrSusceptible = _q5UnexplainedDizziness = false;
            _q5None = false;
            NotifyAllQ5();
        }
    }

    private void ClearQ5NoneAndSkipped()
    {
        bool changed = false;
        if (_q5None)    { _q5None    = false; changed = true; OnPropertyChanged(nameof(Q5None)); }
        if (_q5Skipped) { _q5Skipped = false; changed = true; OnPropertyChanged(nameof(Q5Skipped)); }
        _ = changed;
    }

    private void NotifyAllQ5()
    {
        OnPropertyChanged(nameof(Q5MotionSickness));
        OnPropertyChanged(nameof(Q5Migraine));
        OnPropertyChanged(nameof(Q5VestibularDiagnosed));
        OnPropertyChanged(nameof(Q5VrSusceptible));
        OnPropertyChanged(nameof(Q5UnexplainedDizziness));
        OnPropertyChanged(nameof(Q5None));
        OnPropertyChanged(nameof(Q5Skipped));
    }

    private string? _q5Freetext;
    public string? Q5Freetext { get => _q5Freetext; set => Set(ref _q5Freetext, value); }

    // ── Pack into the immutable responses record ────────────────────────────

    /// <summary>Snapshot the current card state as the immutable input to
    /// <see cref="CalibrationProcessor.Process"/>. Lists are null for
    /// "user didn't answer" and empty for explicit "都沒有".</summary>
    public CalibrationResponses BuildResponses()
    {
        return new CalibrationResponses
        {
            Q1Segment     = Q1Segment,
            Q1Freetext    = TrimToNull(Q1Freetext),

            Q2Symptoms    = PackQ2(),
            Q2Freetext    = TrimToNull(Q2Freetext),

            Q3Intensity   = Q3Intensity,
            Q3Freetext    = TrimToNull(Q3Freetext),

            Q4Comparison  = Q4Comparison,
            Q4NoBaseline  = Q4NoBaseline,
            Q4Freetext    = TrimToNull(Q4Freetext),

            Q5RiskFactors = PackQ5(),
            Q5Freetext    = TrimToNull(Q5Freetext),

            // Source tag default is Buttons per CalibrationResponses init, no
            // need to set explicitly — but doing so makes intent obvious.
            Q1Source = CalibrationSource.Buttons,
            Q2Source = CalibrationSource.Buttons,
            Q3Source = CalibrationSource.Buttons,
            Q4Source = CalibrationSource.Buttons,
            Q5Source = CalibrationSource.Buttons,
        };
    }

    private IReadOnlyList<SymptomType>? PackQ2()
    {
        if (Q2None) return Array.Empty<SymptomType>(); // explicit "no symptoms"
        var list = new List<SymptomType>(6);
        if (Q2Dizziness)      list.Add(SymptomType.Dizziness);
        if (Q2Nausea)         list.Add(SymptomType.Nausea);
        if (Q2EyeStrain)      list.Add(SymptomType.EyeStrain);
        if (Q2Headache)       list.Add(SymptomType.Headache);
        if (Q2Imbalance)      list.Add(SymptomType.Imbalance);
        if (Q2Disorientation) list.Add(SymptomType.Disorientation);
        return list.Count == 0 ? null : list;
    }

    private IReadOnlyList<RiskFactor>? PackQ5()
    {
        if (Q5Skipped) return new[] { RiskFactor.Skipped };
        if (Q5None)    return new[] { RiskFactor.None };
        var list = new List<RiskFactor>(5);
        if (Q5MotionSickness)       list.Add(RiskFactor.MotionSickness);
        if (Q5Migraine)             list.Add(RiskFactor.Migraine);
        if (Q5VestibularDiagnosed)  list.Add(RiskFactor.VestibularDiagnosed);
        if (Q5VrSusceptible)        list.Add(RiskFactor.VrSusceptible);
        if (Q5UnexplainedDizziness) list.Add(RiskFactor.UnexplainedDizziness);
        return list.Count == 0 ? null : list;
    }

    private static string? TrimToNull(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ── INotifyPropertyChanged helpers ──────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void NotifyNavComputed()
    {
        OnPropertyChanged(nameof(IsLastCard));
        OnPropertyChanged(nameof(CardCounterText));
        OnPropertyChanged(nameof(CanAdvance));
        OnPropertyChanged(nameof(IsCard1Visible));
        OnPropertyChanged(nameof(IsCard2Visible));
        OnPropertyChanged(nameof(IsCard3Visible));
        OnPropertyChanged(nameof(IsCard4Visible));
        OnPropertyChanged(nameof(IsCard5Visible));
        CommandsChanged();
    }

    private void CommandsChanged()
    {
        (NextCommand     as RelayCommand)?.RaiseCanExecuteChanged();
        (BackCommand     as RelayCommand)?.RaiseCanExecuteChanged();
        (CompleteCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}
