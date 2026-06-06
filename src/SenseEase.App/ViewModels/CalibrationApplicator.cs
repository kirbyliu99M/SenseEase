using System.Collections.Generic;
using System.Collections.ObjectModel;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;

namespace SenseEase.App.ViewModels;

/// <summary>
/// Pure logic shared between the production wiring (MainViewModel
/// completion handlers) and integration tests. Given a fresh
/// <see cref="CalibrationResponses"/> + sensor snapshot, applies the result
/// to the engine and appends the summary + advisory bubbles to the
/// supplied Messages collection.
///
/// Lives here (rather than in MainViewModel) so headless tests can exercise
/// the end-to-end happy path / cancel / advisory flows without booting WPF
/// (no Application.Current.Dispatcher dependency). The MainViewModel
/// completion handlers delegate to <see cref="Apply"/> after dismissing the
/// card and updating first-run state.
/// </summary>
public static class CalibrationApplicator
{
    /// <summary>Apply <paramref name="responses"/> using <paramref name="sensor"/>
    /// to the <paramref name="engine"/> and post the summary + advisory bubbles to
    /// <paramref name="messages"/>. Returns the computed <see cref="CalibrationResult"/>
    /// so callers can branch further (e.g. for logging).
    ///
    /// Behaviour matrix:
    ///   Completeness == Abandoned → no engine change, no messages posted.
    ///   Completeness == Partial   → engine updated, partial summary posted.
    ///   Completeness == Complete  → engine updated, complete summary posted.
    /// Medical advisory (vestibular / high_risk_combo) → second message appended
    /// after the summary.</summary>
    public static CalibrationResult Apply(
        CalibrationResponses responses,
        CalibrationSensorData sensor,
        MockInferenceEngine engine,
        ObservableCollection<ChatMessageViewModel> messages)
    {
        var result = CalibrationProcessor.Process(responses, sensor);

        if (result.Completeness == CalibrationCompleteness.Abandoned)
        {
            // Do not mutate the engine, do not post any message. Caller still
            // gets the result so it can log "card dismissed before Q3".
            return result;
        }

        engine.ApplyCalibrationResult(result);

        bool partial = result.Completeness == CalibrationCompleteness.Partial;
        messages.Add(new ChatMessageViewModel(
            BuildSummary(result, responses, partial),
            ChatRole.Assistant));

        string? advisory = BuildAdvisory(result.MedicalAdvisory);
        if (advisory is not null)
        {
            messages.Add(new ChatMessageViewModel(advisory, ChatRole.Assistant));
        }
        return result;
    }

    internal static string BuildSummary(
        CalibrationResult result, CalibrationResponses responses, bool partial)
    {
        string symptoms = FormatSymptoms(responses.Q2Symptoms);
        int q3 = responses.Q3Intensity ?? 0;
        string leading = partial ? "Calibration 已部分完成" : "Calibration 完成";
        return $"{leading}。本次強度 {q3}/5，主要不適：{symptoms}。\n"
             + $"系統已調整為 {result.Profile} profile。";
    }

    internal static string FormatSymptoms(IReadOnlyList<SymptomType>? symptoms)
    {
        if (symptoms is null || symptoms.Count == 0) return "未指定";
        var labels = new List<string>(symptoms.Count);
        foreach (var s in symptoms)
        {
            labels.Add(s switch
            {
                SymptomType.Dizziness      => "頭暈",
                SymptomType.Nausea         => "想吐",
                SymptomType.EyeStrain      => "眼睛疲勞",
                SymptomType.Headache       => "頭痛",
                SymptomType.Imbalance      => "平衡感差",
                SymptomType.Disorientation => "方向感混亂",
                _                          => s.ToString(),
            });
        }
        return string.Join("、", labels);
    }

    /// <summary>Bilingual advisory text per spec §6. Returns null when no
    /// advisory applies (i.e. <paramref name="advisoryKey"/> is null or
    /// unrecognised).</summary>
    internal static string? BuildAdvisory(string? advisoryKey) => advisoryKey switch
    {
        "vestibular" =>
            "醫療提醒 / Medical advisory:\n"
          + "您填答有前庭/內耳相關診斷。系統已套用最敏感的防護設定（觸發壓力降至 50，"
          + "嚴重事件上限設為 1）。若您持續感到不適或正在就醫，"
          + "請務必諮詢您的醫療專業人員。\n"
          + "You reported a diagnosed vestibular condition. SenseEase has applied the "
          + "most sensitive protection (trigger pressure lowered to 50, severe-event "
          + "limit set to 1). If discomfort persists or you are seeing a doctor, "
          + "please consult your healthcare provider.",

        "high_risk_combo" =>
            "醫療提醒 / Medical advisory:\n"
          + "您回報高強度（Q3=5）並具有兩項以上長期風險因子。"
          + "若這種強度反應經常發生，建議您諮詢醫療專業人員以排除其他原因。\n"
          + "You reported high intensity (Q3=5) combined with two or more long-term risk "
          + "factors. If this level of reaction recurs, consider consulting a healthcare "
          + "provider to rule out other causes.",

        _ => null,
    };
}
