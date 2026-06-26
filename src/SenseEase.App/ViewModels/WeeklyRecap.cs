using System;
using System.Globalization;
using SenseEase.Infrastructure.Reporting;

namespace SenseEase.App.ViewModels;

/// <summary>Pure logic for the weekly welcome recap (Kirby 2026-06-27): the once-per-week gate + the
/// deterministic fallback copy + the LLM prompts. No I/O, no UI, no clock dependency beyond the passed-in
/// time — so the Monday/ISO-week boundary logic and the bilingual copy golden-test directly. The orchestration
/// (read stores → <see cref="HealthCardDataBuilder"/> → compose → post the chat bubble → stamp prefs) lives in
/// <see cref="ChatbotViewModel"/>.
///
/// <para>Wording mirrors the in-app health-report card (<c>HealthReportWindow.ApplyCondition</c>) so the recap
/// and the card never disagree. The recap is de-identified by construction — it carries only a count + a coarse
/// condition word (the same posture as <see cref="HealthCardData"/>), never any utterance, date, or finer
/// timestamp.</para></summary>
public static class WeeklyRecap
{
    /// <summary>Due on the FIRST chat-open of a new ISO week (ISO weeks start Monday, so this is Monday-anchored
    /// but still fires if the user first opens chat on Tue/Wed…). Compare LOCAL times so it follows the user's
    /// calendar Monday, not UTC. <paramref name="lastShownLocal"/> null ⇒ never shown ⇒ due.</summary>
    public static bool IsDue(DateTime nowLocal, DateTime? lastShownLocal)
    {
        if (lastShownLocal is not { } last) return true;
        return WeekKey(nowLocal) != WeekKey(last);
    }

    private static (int year, int week) WeekKey(DateTime d)
        => (ISOWeek.GetYear(d), ISOWeek.GetWeekOfYear(d));

    /// <summary>A brand-new user's first week has nothing worth recapping — no protections AND no measured/
    /// profile condition. Skip (and DON'T stamp) so the recap debuts the first week there's real data.</summary>
    public static bool IsEmpty(HealthCardData? d)
        => d is null || (d.TimesProtected == 0 && d.Condition == VimsCondition.Unknown);

    /// <summary>The deterministic recap sentence — the instant fallback when the LLM is cold/slow/unavailable,
    /// and the floor the LLM output is judged against. Honest: only the count + the coarse condition.</summary>
    public static string FallbackText(int timesProtected, VimsCondition condition, bool english)
    {
        string phrase = ConditionPhrase(condition, english);
        if (timesProtected <= 0)
        {
            return english
                ? $"Welcome back. Last week was calm — no extra easing needed, and {phrase}."
                : $"歡迎回來。上週很平穩，不需要額外緩和，{phrase}。";
        }
        if (english)
        {
            string times = timesProtected == 1 ? "time" : "times";
            return $"Welcome back. Last week I eased your screen {timesProtected} {times}, and {phrase}.";
        }
        return $"歡迎回來。上週我為你緩和畫面 {timesProtected} 次，{phrase}。";
    }

    /// <summary>Warm, card-consistent phrasing of the condition (derived from HealthReportWindow's badge copy).</summary>
    public static string ConditionPhrase(VimsCondition condition, bool english) => condition switch
    {
        VimsCondition.Stable   => english ? "your comfort stayed steady"           : "你的舒適度保持穩定",
        VimsCondition.Moderate => english ? "you had some motion sensitivity"      : "偶有畫面敏感",
        VimsCondition.Severe   => english ? "you felt discomfort fairly often — be gentle with yourself"
                                          : "較常感到不適，記得多休息",
        _                      => english ? "we're still getting to know your week" : "仍在了解你的狀況",
    };

    /// <summary>The card's one-word condition label (Stable/Moderate/Severe · 穩定/中等/明顯), passed to the LLM
    /// as a FACT so a generated recap names the same state the health card shows.</summary>
    public static string ConditionWord(VimsCondition condition, bool english) => condition switch
    {
        VimsCondition.Stable   => english ? "Stable"   : "穩定",
        VimsCondition.Moderate => english ? "Moderate" : "中等",
        VimsCondition.Severe   => english ? "Severe"   : "明顯",
        _                      => english ? "Unknown"  : "未知",
    };

    /// <summary>System prompt for the optional LLM-written recap. Locks the language + role + the hard rule that
    /// it may use ONLY the supplied facts (no invented numbers/dates/claims). Mirrors the rest-advice shape.</summary>
    public static string LlmSystem(bool english) => english
        ? "You are SenseEase's calm comfort companion. Write ONE short, warm welcome-back sentence that recaps "
          + "the user's LAST WEEK using ONLY the facts provided. Never invent numbers, dates, settings changes, "
          + "or claims. No emojis. Plain, gentle, encouraging — this is a comfort tool, not an alarm."
        : "你是 SenseEase 冷靜、體貼的舒適陪伴。請用「一句」溫暖的歡迎語，根據提供的事實回顧使用者「上週」的情況。"
          + "只能使用所給事實，不可捏造數字、日期、設定變更或任何說法。不要使用表情符號。語氣平和、溫柔、鼓勵。";

    /// <summary>User prompt: the facts (protections count + condition word) for the one-sentence recap.</summary>
    public static string LlmUser(int timesProtected, VimsCondition condition, bool english)
    {
        string word = ConditionWord(condition, english);
        return english
            ? $"Facts — protections last week: {timesProtected}; overall condition: {word}. "
              + "Write the one-sentence welcome-back recap."
            : $"事實——上週緩和次數：{timesProtected}；整體狀況：{word}。請寫出這句歡迎回顧。";
    }
}
