using System;
using System.IO;
using System.Text;
using SenseEase.App.Services;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// FMS owner-migration (2026-06-19): the adaptive 7-point window + dedicated fms_log.jsonl.
/// The window math is pure/deterministic; the log roundtrip uses an isolated temp directory.
/// </summary>
public class FmsCheckInServiceTests
{
    // ── Adaptive window (pure) — Kirby's worked examples ─────────────────────────────────

    [Fact]
    public void Window_FirstCheckIn_NoHistory_Is0To6()
        => Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, FmsCheckInService.WindowFor(null));

    [Theory]
    [InlineData(6, new[] { 3, 4, 5, 6, 7, 8, 9 })]    // pick 6 → 3–9
    [InlineData(8, new[] { 5, 6, 7, 8, 9, 10, 11 })]  // pick 8 → 5–11
    [InlineData(5, new[] { 2, 3, 4, 5, 6, 7, 8 })]    // pick 5 → 2–8
    [InlineData(3, new[] { 0, 1, 2, 3, 4, 5, 6 })]    // centre 3 → 0–6
    public void Window_ReCentresOnLastPick(int last, int[] expected)
        => Assert.Equal(expected, FmsCheckInService.WindowFor(last));

    [Theory]
    [InlineData(0, new[] { 0, 1, 2, 3, 4, 5, 6 })]      // low clamp — shift, not truncate
    [InlineData(1, new[] { 0, 1, 2, 3, 4, 5, 6 })]
    [InlineData(20, new[] { 14, 15, 16, 17, 18, 19, 20 })]  // high clamp
    [InlineData(18, new[] { 14, 15, 16, 17, 18, 19, 20 })]
    public void Window_ClampsToKeepSevenInsideRange(int last, int[] expected)
        => Assert.Equal(expected, FmsCheckInService.WindowFor(last));

    [Fact]
    public void Window_AlwaysSevenValues_AcrossWholeScale()
    {
        for (int last = 0; last <= 20; last++)
        {
            var w = FmsCheckInService.WindowFor(last);
            Assert.Equal(7, w.Length);
            Assert.All(w, v => Assert.InRange(v, 0, 20));
            Assert.Contains(last, w);                         // the last pick is always offer-able again
            for (int i = 1; i < w.Length; i++) Assert.Equal(w[i - 1] + 1, w[i]); // consecutive
        }
    }

    // ── W-F: "worse than shown" escape — bump the window UP (FmsCheckInService.WindowAbove) ─────────────

    [Theory]
    [InlineData(0, new[] { 7, 8, 9, 10, 11, 12, 13 })]    // first window 0-6 → 7-13
    [InlineData(7, new[] { 14, 15, 16, 17, 18, 19, 20 })] // 7-13 → 14-20 (the severe window)
    public void WindowAbove_ShiftsUpOneWindow(int currentLow, int[] expected)
        => Assert.Equal(expected, FmsCheckInService.WindowAbove(currentLow));

    [Theory]
    [InlineData(14)]  // already at the top window → no "worse"
    [InlineData(20)]
    public void WindowAbove_AtTop_ReturnsNull(int currentLow)
        => Assert.Null(FmsCheckInService.WindowAbove(currentLow));

    [Fact]
    public void WindowAbove_ReachesSevere_FromFirstWindowInTwoBumps()
    {
        // Acute-severe first contact: 0-6 → (Worse) 7-13 → (Worse) 14-20, then 20 is offer-able.
        int[] first = FmsCheckInService.WindowFor(null);          // 0-6
        int[]? second = FmsCheckInService.WindowAbove(first[0]);  // 7-13
        Assert.NotNull(second);
        int[]? third = FmsCheckInService.WindowAbove(second![0]); // 14-20
        Assert.NotNull(third);
        Assert.Contains(20, third!);                              // severe is reachable
        Assert.Null(FmsCheckInService.WindowAbove(third![0]));    // no window beyond the top
    }

    // ── W-F2: ‹ "back / lower numbers" — page the window DOWN (FmsCheckInService.WindowBelow) ──────────

    [Theory]
    [InlineData(14, new[] { 7, 8, 9, 10, 11, 12, 13 })]   // top 14-20 → 7-13
    [InlineData(7, new[] { 0, 1, 2, 3, 4, 5, 6 })]        // 7-13 → 0-6
    public void WindowBelow_ShiftsDownOneWindow(int currentLow, int[] expected)
        => Assert.Equal(expected, FmsCheckInService.WindowBelow(currentLow));

    [Fact]
    public void WindowBelow_AtBottom_ReturnsNull()
        => Assert.Null(FmsCheckInService.WindowBelow(0));   // already at the lowest numbers

    [Theory]
    // ‹ undoes › for the canonical windows: WindowBelow(WindowAbove(lo)) returns to the original window.
    [InlineData(0)]
    [InlineData(7)]
    public void WindowBelow_UndoesWindowAbove(int lo)
    {
        int[]? up = FmsCheckInService.WindowAbove(lo);
        Assert.NotNull(up);
        int[]? backDown = FmsCheckInService.WindowBelow(up![0]);
        Assert.NotNull(backDown);
        Assert.Equal(lo, backDown![0]);                    // returned to the exact starting window
    }

    // ── Dedicated log: roundtrip in an isolated temp dir ─────────────────────────────────

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "senseease_fms_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ReadLastValue_NoFile_ReturnsNull_AndDefaultWindow()
    {
        var svc = new FmsCheckInService(NewTempDir());   // internal ctor (InternalsVisibleTo Tests)
        Assert.Null(svc.ReadLastValue());
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, svc.NextWindow());
    }

    [Fact]
    public void Record_ThenNextWindow_ReCentresOnLoggedValue()
    {
        var dir = NewTempDir();
        var svc = new FmsCheckInService(dir);
        svc.Record(8, "spike", "browser", tier: 6, protectionState: "on", mode: "Driver", ofi: 42.5);
        Assert.Equal(8, svc.ReadLastValue());
        Assert.Equal(new[] { 5, 6, 7, 8, 9, 10, 11 }, svc.NextWindow());
        // a row was actually written to fms_log.jsonl, carrying the efference-copy context (mode + raw ofi).
        string log = Path.Combine(dir, "fms_log.jsonl");
        Assert.True(File.Exists(log));
        string txt = File.ReadAllText(log);
        Assert.Contains("\"fms\":8", txt);
        Assert.Contains("\"mode\":\"Driver\"", txt);
        Assert.Contains("\"ofi\":42.5", txt);
    }

    [Fact]
    public void Record_MultipleRows_LastValueIsMostRecent()
    {
        var svc = new FmsCheckInService(NewTempDir());
        svc.Record(4, "spike", null, 3, "off", "Passenger", 12.0);
        svc.Record(11, "spike", null, 8, "on", "Driver", 60.0);
        Assert.Equal(11, svc.ReadLastValue());
    }

    [Fact]
    public void Record_OutOfRange_IsIgnored()
    {
        var svc = new FmsCheckInService(NewTempDir());
        svc.Record(25, "spike", null, 5, "on", "Passenger", 0.0);   // > 20 → not written
        Assert.Null(svc.ReadLastValue());
        svc.Record(-1, "spike", null, 5, "on", "Passenger", 0.0);   // < 0 → not written
        Assert.Null(svc.ReadLastValue());
    }
}
