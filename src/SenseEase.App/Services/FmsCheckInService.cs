using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using SenseEase.App.Configuration;
using SenseEase.App.Localization;

namespace SenseEase.App.Services;

/// <summary>
/// FMS owner-migration (chatbot-tuning sprint 2026-06-19): owns the in-chat FMS 0–20 quick-rate
/// check-in's <b>adaptive 7-point window</b> and its <b>dedicated dev-monitoring log</b>.
///
/// <para><b>Adaptive window</b> (Kirby's spec): the box shows 7 small square number-buttons in one row —
/// a width-7 window <i>re-centred on the user's last pick</i> rather than a hard-to-scan 0–20 strip.
/// window = <c>[last−3 … last+3]</c>, clamped to keep all 7 values inside [0,20]. First-ever check-in
/// (no history) shows 0–6. e.g. pick 6 → next 3–9; pick 8 → 5–11; pick 5 → 2–8. The tapped integer IS the
/// logged FMS 0–20 value; the full range is still reachable over successive check-ins.</para>
///
/// <para><b>Dedicated log</b>: each response appends one self-describing row to
/// <c>fms_log.jsonl</c> under <c>%LOCALAPPDATA%\SenseEase\</c> — parallel to the NPS
/// <c>survey_log.jsonl</c> (see <see cref="NpsService"/>), so dev can monitor FMS responses cleanly
/// without sifting the mixed-kind symptom_log.jsonl. Best-effort IO; never throws into the caller.
/// The data directory is injectable for tests. This does NOT replace the existing
/// <c>MainViewModel.RecordFmsRating</c> symptom-store + engine-feed path — it is an additive dev sink.</para>
/// </summary>
public sealed class FmsCheckInService
{
    public const int WindowSize = 7;
    public const int FmsMin = 0;
    public const int FmsMax = 20;
    /// <summary>First-ever window centre (no history) → 0–6.</summary>
    public const int DefaultCenter = 3;

    private readonly Func<DateTime> _clock;
    private readonly string _dir;

    /// <summary>Production ctor: data under %LOCALAPPDATA%\SenseEase\.</summary>
    public FmsCheckInService(Func<DateTime>? utcClock = null) : this(null, utcClock) { }

    /// <summary>Test seam: isolate the data directory without mutating global AppConfig state.</summary>
    internal FmsCheckInService(string? dataDirOverride, Func<DateTime>? utcClock = null)
    {
        _clock = utcClock ?? (() => DateTime.UtcNow);
        _dir = dataDirOverride ?? Path.Combine(AppConfig.DataRoot, "SenseEase");
    }

    private string FmsLogPath => Path.Combine(_dir, "fms_log.jsonl");

    /// <summary>The 7 button values to show this check-in: a width-7 window re-centred on the last
    /// logged pick (or the default 0–6 when there is no history).</summary>
    public int[] NextWindow() => WindowFor(ReadLastValue());

    /// <summary>Pure window math (testable): width-7, centred on <paramref name="lastValue"/> (or the
    /// default centre), shifted — not truncated — so all 7 values stay within [0,20].</summary>
    internal static int[] WindowFor(int? lastValue)
    {
        int center = lastValue ?? DefaultCenter;
        // shift the low edge so the window keeps width 7 inside [0,20]: lo ∈ [0, 14].
        int lo = Math.Clamp(center - WindowSize / 2, FmsMin, FmsMax - (WindowSize - 1));
        var window = new int[WindowSize];
        for (int i = 0; i < WindowSize; i++) window[i] = lo + i;
        return window;
    }

    /// <summary>W-F (2026-06-20): the next window UP from one starting at <paramref name="currentLow"/> — the
    /// "worse than shown" escape. Shifts the low edge up by the window width (non-overlapping), clamped to keep
    /// 7 values in [0,20]; returns null when already at the top window (no higher reach). 0-6 → 7-13 → 14-20 → null.</summary>
    public static int[]? WindowAbove(int currentLow)
    {
        if (currentLow >= FmsMax - (WindowSize - 1)) return null;   // already at the top (lo 14) → no "worse"
        int lo = Math.Clamp(currentLow + WindowSize, FmsMin, FmsMax - (WindowSize - 1));
        var window = new int[WindowSize];
        for (int i = 0; i < WindowSize; i++) window[i] = lo + i;
        return window;
    }

    /// <summary>Append one FMS response row to fms_log.jsonl. Out-of-range values are ignored.
    /// <paramref name="mode"/> + <paramref name="ofi"/> carry the EFFERENCE-COPY context (Driver/Passenger
    /// weighting + the raw on-screen motion it weighted) so each rating can be read against whether the
    /// provoking motion was self-generated. Best-effort — an IO failure must never break the chat turn.</summary>
    public void Record(int fms, string trigger, string? appContext, int tier, string protectionState,
        string mode, double ofi)
    {
        if (fms < FmsMin || fms > FmsMax) return;
        try
        {
            Directory.CreateDirectory(_dir);
            string row = JsonSerializer.Serialize(new
            {
                ts = _clock().ToString("O", CultureInfo.InvariantCulture),
                fms,
                scaleMax = FmsMax,           // self-describing scale so mixed-scale rows can't be confused
                trigger,
                app_context = appContext,
                tier,
                protection_state = protectionState,
                // Efference-copy context (Kirby 2026-06-20): the Driver/Passenger weighting the engine applied
                // + the raw on-screen motion it weighted. Driver = the user is generating the motion (predicted
                // by their own efference copy → attenuated ×0.25); Passenger = passive viewing (×2.0) — the 8×
                // asymmetry already baked into Pressure (the spike trigger). Lets a rating be read against
                // whether the provoking motion was self-generated.
                mode,
                ofi = System.Math.Round(ofi, 1),
                lang = LocalizationManager.Current.IsEnglish ? "en" : "zh",
                source = "chat",
            });
            File.AppendAllText(FmsLogPath, row + Environment.NewLine, Encoding.UTF8);
        }
        catch { /* best-effort dev sink — never throw into the UI */ }
    }

    /// <summary>The most recent logged FMS value (drives the next window's centre), or null when there
    /// is no history yet / the log is unreadable. Reads the last non-empty line of fms_log.jsonl.</summary>
    internal int? ReadLastValue()
    {
        try
        {
            if (!File.Exists(FmsLogPath)) return null;
            string? last = null;
            foreach (var line in File.ReadLines(FmsLogPath))
                if (!string.IsNullOrWhiteSpace(line)) last = line;
            if (last is null) return null;
            using var doc = JsonDocument.Parse(last);
            if (doc.RootElement.TryGetProperty("fms", out var f) && f.TryGetInt32(out var v)
                && v is >= FmsMin and <= FmsMax)
                return v;
        }
        catch { /* fail-safe: unreadable → no history → default window */ }
        return null;
    }
}
