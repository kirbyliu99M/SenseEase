using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SenseEase.Core.Models;
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
    /// calendar Monday, not UTC. <paramref name="lastShownLocal"/> null ⇒ never shown ⇒ due.
    ///
    /// <para>The recap DEBUTS in the user's SECOND week of use: it is never due during the install/first-run ISO
    /// week (<paramref name="firstRunLocal"/> — there is no full prior week to recap yet, and greeting a
    /// day-one user with "last week" is wrong). <paramref name="firstRunLocal"/> null (unknown install week —
    /// legacy/empty flag, or a long-standing user) ⇒ this gate is a no-op, so upgraders keep the recap and the
    /// empty-first-week skip still guards genuine new users.</para></summary>
    public static bool IsDue(DateTime nowLocal, DateTime? lastShownLocal, DateTime? firstRunLocal = null)
    {
        if (firstRunLocal is { } fr && WeekKey(nowLocal) == WeekKey(fr)) return false;   // install week → not yet
        if (lastShownLocal is not { } last) return true;
        return WeekKey(nowLocal) != WeekKey(last);
    }

    private static (int year, int week) WeekKey(DateTime d)
        => (ISOWeek.GetYear(d), ISOWeek.GetWeekOfYear(d));

    /// <summary>A brand-new user's first week has nothing worth recapping — no protections AND no measured/
    /// profile condition. Skip (and DON'T stamp) so the recap debuts the first week there's real data.</summary>
    public static bool IsEmpty(HealthCardData? d)
        => d is null || (d.TimesProtected == 0 && d.Condition == VimsCondition.Unknown);

    /// <summary>Monday 00:00 of the ISO week containing <paramref name="utc"/> (ISO weeks start Monday). The
    /// previous ISO week is then <c>[result-7d, result)</c>.</summary>
    public static DateTime IsoWeekMondayUtc(DateTime utc)
    {
        utc = utc.ToUniversalTime();
        int offsetFromMonday = ((int)utc.DayOfWeek + 6) % 7;   // .NET DayOfWeek has Sun=0 → Mon→0 … Sun→6
        return utc.Date.AddDays(-offsetFromMonday);
    }

    /// <summary>Build LAST WEEK's de-identified summary — the PREVIOUS ISO week (Mon→Sun immediately before
    /// <paramref name="nowUtc"/>'s week), so the recap's "last week" claim is honest no matter which weekday the
    /// user first opens chat (ultrareview bug_001). <see cref="HealthCardDataBuilder"/> has only a LOWER bound,
    /// so we exclude this-week-and-later rows here, then anchor the builder's lower bound at the prior Monday —
    /// the effective window is exactly <c>[prevMonday, thisMonday)</c>. Undated rows are kept (fail-open,
    /// mirroring the builder). Pure (no I/O, clock passed in) → golden-testable.</summary>
    public static HealthCardData BuildPriorWeek(
        IEnumerable<SymptomEvent>? events,
        IEnumerable<DailyBehaviorAggregate>? behavior,
        string? profile,
        int? currentVimsTier,
        DateTime nowUtc)
    {
        DateTime thisMonday = IsoWeekMondayUtc(nowUtc);                          // exclusive upper bound
        string cutoff = thisMonday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var lastWeekEvents = (events ?? Enumerable.Empty<SymptomEvent>())
            .Where(e => e is not null && UtcDateBefore(e!.TsUtc, cutoff))
            .ToList();
        var lastWeekBehavior = (behavior ?? Enumerable.Empty<DailyBehaviorAggregate>())
            .Where(b => b is not null && (string.IsNullOrWhiteSpace(b!.Date)
                                          || string.CompareOrdinal(b.Date, cutoff) < 0))
            .ToList();

        // Lower bound = thisMonday-7 = the prior Monday; combined with the upper exclusion above ⇒ the prior ISO week.
        return HealthCardDataBuilder.Build(
            lastWeekEvents, lastWeekBehavior, profile, currentVimsTier, thisMonday, windowDays: 7);
    }

    /// <summary>True if <paramref name="tsUtc"/>'s UTC date is strictly before <paramref name="cutoffDate"/>
    /// (yyyy-MM-dd). Empty/unparseable ⇒ true (fail-open — never silently drop an undated row, mirroring
    /// HealthCardDataBuilder.OnOrAfterDate).</summary>
    private static bool UtcDateBefore(string? tsUtc, string cutoffDate)
    {
        if (string.IsNullOrWhiteSpace(tsUtc)) return true;
        if (!DateTime.TryParse(tsUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t))
            return true;
        return string.CompareOrdinal(
            t.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), cutoffDate) < 0;
    }

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
