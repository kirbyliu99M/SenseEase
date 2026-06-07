using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Input;
using SenseEase.App.Localization;
using SenseEase.Core.Inference;

namespace SenseEase.App.ViewModels;

// ─────────────────────────────────────────────────────────────────────────────
// CalibrationConfirmCardViewModel (H11) — the user-confirmation gate shown AFTER
// the 5-card calibration completes but BEFORE the result mutates the engine.
//
// It renders a plain-language recap of the user's Q1–Q5 answers + a preview of
// what will change (profile + protection scale), and exposes Accept / Decline.
// The host (MainViewModel) computes the CalibrationResult with the PURE
// CalibrationProcessor.Process (no engine mutation), passes it here, and only
// calls CalibrationApplicator.ApplyComputed when Accepted fires — so a simulated
// or real calibration delta can never change a setting until the user accepts.
//
// Bilingual via LocalizationManager.Pick (zh-Hant / en), following the live
// language toggle like the rest of the UI.
// ─────────────────────────────────────────────────────────────────────────────

// All properties are set once at construction (the recap + outcome are a snapshot
// of the completed calibration), so this VM is immutable and needs no
// INotifyPropertyChanged — WPF reads each binding once when the DataContext is set.
public sealed class CalibrationConfirmCardViewModel
{
    public CalibrationConfirmCardViewModel(CalibrationResponses responses, CalibrationResult result)
    {
        if (responses is null) throw new ArgumentNullException(nameof(responses));
        if (result is null) throw new ArgumentNullException(nameof(result));

        RecapLines = BuildRecap(responses);
        OutcomeText = BuildOutcome(result);

        AcceptCommand  = new RelayCommand(_ => Accepted?.Invoke(this, EventArgs.Empty));
        DeclineCommand = new RelayCommand(_ => Declined?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>Fired when the user accepts — the host applies the computed result.</summary>
    public event EventHandler? Accepted;

    /// <summary>Fired when the user declines — the host keeps the current settings.</summary>
    public event EventHandler? Declined;

    public string HeaderText { get; } =
        LocalizationManager.Current.Pick("確認您的校正結果", "Review your calibration");

    public string IntroText { get; } =
        LocalizationManager.Current.Pick(
            "根據您的回答，SenseEase 將套用以下設定。套用前請確認：",
            "Based on your answers, SenseEase will apply the settings below. Please confirm before applying:");

    /// <summary>Plain-language recap of Q1–Q5 (label + value, one per line).</summary>
    public IReadOnlyList<string> RecapLines { get; }

    /// <summary>What will change — profile + protection scale preview.</summary>
    public string OutcomeText { get; }

    public string AcceptText  { get; } = LocalizationManager.Current.Pick("套用設定", "Apply");
    public string DeclineText { get; } = LocalizationManager.Current.Pick("先不要", "Not now");

    public ICommand AcceptCommand  { get; }
    public ICommand DeclineCommand { get; }

    // ── Recap construction ───────────────────────────────────────────────────

    private static IReadOnlyList<string> BuildRecap(CalibrationResponses r)
    {
        string na = LocalizationManager.Current.Pick("未回答", "not answered");
        var lines = new List<string>(5)
        {
            Line(LocalizationManager.Current.Pick("最不適的時段", "Most uncomfortable part"),
                 r.Q1Segment is { } seg ? Segment(seg) : na),
            Line(LocalizationManager.Current.Pick("主要不適", "Main symptoms"),
                 Symptoms(r.Q2Symptoms, na)),
            Line(LocalizationManager.Current.Pick("不適強度", "Intensity"),
                 r.Q3Intensity is { } q3 ? $"{q3}/5" : na),
            Line(LocalizationManager.Current.Pick("與平常比較", "Compared to usual"),
                 r.Q4NoBaseline
                     ? LocalizationManager.Current.Pick("平常不看這類畫面", "doesn't usually watch this")
                     : (r.Q4Comparison is { } cmp ? Baseline(cmp) : na)),
            Line(LocalizationManager.Current.Pick("風險因子", "Risk factors"),
                 RiskFactors(r.Q5RiskFactors, na)),
        };
        return lines;
    }

    private static string Line(string label, string value) => $"{label}：{value}";

    private static string Segment(UncomfortableSegment s) => s switch
    {
        UncomfortableSegment.Start  => LocalizationManager.Current.Pick("開始時", "the start"),
        UncomfortableSegment.Middle => LocalizationManager.Current.Pick("中段",   "the middle"),
        UncomfortableSegment.End    => LocalizationManager.Current.Pick("結尾時", "the end"),
        UncomfortableSegment.None   => LocalizationManager.Current.Pick("全程都還好", "none — all fine"),
        _                           => s.ToString(),
    };

    private static string Symptoms(IReadOnlyList<SymptomType>? symptoms, string na)
    {
        if (symptoms is null) return na;
        if (symptoms.Count == 0) return LocalizationManager.Current.Pick("都沒有", "none");
        var labels = new List<string>(symptoms.Count);
        foreach (var s in symptoms) labels.Add(Symptom(s));
        return string.Join(LocalizationManager.Current.Pick("、", ", "), labels);
    }

    private static string Symptom(SymptomType s) => s switch
    {
        SymptomType.Dizziness      => LocalizationManager.Current.Pick("頭暈",     "dizziness"),
        SymptomType.Nausea         => LocalizationManager.Current.Pick("想吐",     "nausea"),
        SymptomType.EyeStrain      => LocalizationManager.Current.Pick("眼睛疲勞", "eye strain"),
        SymptomType.Headache       => LocalizationManager.Current.Pick("頭痛",     "headache"),
        SymptomType.Imbalance      => LocalizationManager.Current.Pick("平衡感差", "imbalance"),
        SymptomType.Disorientation => LocalizationManager.Current.Pick("方向感混亂", "disorientation"),
        _                          => s.ToString(),
    };

    private static string Baseline(BaselineComparison b) => b switch
    {
        BaselineComparison.Worse   => LocalizationManager.Current.Pick("比平常更不適", "worse than usual"),
        BaselineComparison.Similar => LocalizationManager.Current.Pick("和平常差不多", "about the same"),
        BaselineComparison.Better  => LocalizationManager.Current.Pick("比平常好",     "better than usual"),
        _                          => b.ToString(),
    };

    private static string RiskFactors(IReadOnlyList<RiskFactor>? factors, string na)
    {
        if (factors is null) return na;
        if (factors.Count == 0) return na;
        var labels = new List<string>(factors.Count);
        foreach (var f in factors) labels.Add(Risk(f));
        return string.Join(LocalizationManager.Current.Pick("、", ", "), labels);
    }

    private static string Risk(RiskFactor f) => f switch
    {
        RiskFactor.MotionSickness       => LocalizationManager.Current.Pick("容易暈車/暈船", "motion sickness"),
        RiskFactor.Migraine             => LocalizationManager.Current.Pick("偏頭痛",        "migraine"),
        RiskFactor.VestibularDiagnosed  => LocalizationManager.Current.Pick("前庭/內耳診斷", "vestibular diagnosis"),
        RiskFactor.VrSusceptible        => LocalizationManager.Current.Pick("VR 易暈",       "VR susceptibility"),
        RiskFactor.UnexplainedDizziness => LocalizationManager.Current.Pick("不明原因頭暈",  "unexplained dizziness"),
        RiskFactor.None                 => LocalizationManager.Current.Pick("都沒有",        "none"),
        RiskFactor.Skipped              => LocalizationManager.Current.Pick("選擇不回答",    "declined to share"),
        _                               => f.ToString(),
    };

    private static string BuildOutcome(CalibrationResult result)
    {
        string profile = result.Profile.ToString().ToUpperInvariant();
        string scale = result.PersonalPressureScale.ToString("0.00", CultureInfo.InvariantCulture);
        return LocalizationManager.Current.Pick(
            $"將套用 {profile} profile，個人防護強度 {scale}×。",
            $"Will apply the {profile} profile at {scale}× personal protection.");
    }
}
