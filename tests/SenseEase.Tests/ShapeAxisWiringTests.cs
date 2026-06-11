using System;
using System.Linq;
using SenseEase.App.Configuration;
using SenseEase.Core.AdaptiveFit;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Shape-axis render-character wiring (Kirby ruling 2026-06-11). The 50-set grid's Shape
/// axis now drives TWO render-only (Q4) outputs per set — the GridVisibility baseline AND
/// the FeatherWidth character — re-seeded onto the live shader on a set move. These tests
/// pin the ENGINE-side contract of that wiring (the pure SetGrid mapping is pinned by
/// <see cref="AdaptiveFitGridTests"/>):
/// <list type="number">
///   <item><b>NON-user move RE-SEEDS</b> — a silent set move with no user shader override
///   re-seeds gv + feather from the NEW set's curve (Current.Shader tracks SetGrid).</item>
///   <item><b>USER-OVERRIDE SURVIVES</b> (A7) — once the user has explicitly set gv / feather
///   (/grid, /feather → <see cref="MockInferenceEngine.SetShaderParam"/>), a later silent set
///   move must NOT clobber their choice (same authority contract a profile change honours).</item>
///   <item><b>Q4 LOCK</b> — the Shape axis never perturbs a Band-only param (PeakMaskAlpha,
///   FovFloorPercent, Scale) at the engine resolve surface.</item>
/// </list>
///
/// <para>The driven (4,2) move is the same calm-window silent move the existing wiring tests
/// rely on (Band-DOWN, same 5級 segment). (4,2) is the Set0-column Shape (2), so its mapped
/// gv/feather are the byte-identical defaults — which is exactly why it is a clean fixture for
/// the user-override survival test: the engine WOULD re-seed 0.75/0.50, and the test proves the
/// user's DIFFERENT values win instead.</para>
///
/// <para>Every test that flips <see cref="AppConfig.AdaptiveFitSelectorEnabled"/> guards it in a
/// try/finally and restores it (the field is a process-wide static).</para>
/// </summary>
[Collection("AdaptiveFitSelectorFlag")]
public class ShapeAxisWiringTests
{
    private static readonly DateTime ClockBase = new(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc);
    private const int    DriveTicks  = 70;
    private const double TickSeconds = 2.0;
    private const int    SeededSampleN = 24; // > SelectorMinSamplesBeforeAnyMove (12)

    private static void SeedCalmDurable(InMemoryEventLogStore log)
    {
        for (int i = 0; i < SeededSampleN; i++)
        {
            log.Append(new EventLogEntry
            {
                Timestamp = ClockBase.AddMinutes(-(i + 1) * 3),
                Category  = "AdaptiveFit.Sample",
                Message   = "seeded",
                Pressure  = 5.0, // clearly calm → silent same-segment Band-DOWN move to (4,2)
            });
        }
    }

    private static void DriveCalm(MockInferenceEngine engine)
    {
        for (int i = 0; i < DriveTicks; i++)
            engine.Update(0.0, "Passenger", ClockBase.AddSeconds(i * TickSeconds));
    }

    private static bool ResolvesToSet(MockInferenceEngine engine, GridCoord coord)
    {
        SetCurve expected = SetGrid.Curve(coord);
        for (int level = 1; level <= SetCurve.LevelCount; level++)
        {
            if (engine.ResolveLevel(level).PeakMaskAlpha != expected.ForLevel(level).PeakMaskAlpha)
                return false;
        }
        return true;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 1. NON-user silent move RE-SEEDS gv + feather from the new set's curve.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FlagOn_SilentMove_NoUserOverride_ReSeedsGvAndFeatherFromActiveSetCurve()
    {
        bool original = AppConfig.AdaptiveFitSelectorEnabled;
        try
        {
            AppConfig.AdaptiveFitSelectorEnabled = true;

            var log = new InMemoryEventLogStore();
            SeedCalmDurable(log);
            var engine = new MockInferenceEngine(null, null, null, log)
            {
                AdaptiveFitSelectorEnabled = true,
            };

            DriveCalm(engine);

            var moved = new GridCoord(4, 2);
            Assert.True(ResolvesToSet(engine, moved), "calm window should move silently to (4,2)");

            // One more calm tick to read the overlay-facing shader state after the move.
            SenseState s = engine.Update(0.0, "Passenger", ClockBase.AddSeconds(DriveTicks * TickSeconds));

            LevelParams movedLp = SetGrid.Curve(moved).ForLevel(s.VimsTier);
            // The engine re-seeded BOTH render-character outputs from the new set's curve.
            Assert.Equal(movedLp.GridVisibility, s.Shader.GridVisibility, 6);
            Assert.Equal(movedLp.FeatherWidth,   s.Shader.FeatherWidth, 6);
        }
        finally { AppConfig.AdaptiveFitSelectorEnabled = original; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 2. USER-OVERRIDE SURVIVES a silent set move (A7 _shaderUserModified guard).
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FlagOn_UserGridOverride_SurvivesASilentSetMove()
    {
        bool original = AppConfig.AdaptiveFitSelectorEnabled;
        try
        {
            AppConfig.AdaptiveFitSelectorEnabled = true;

            var log = new InMemoryEventLogStore();
            SeedCalmDurable(log);
            var engine = new MockInferenceEngine(null, null, null, log)
            {
                AdaptiveFitSelectorEnabled = true,
            };

            // The user EXPLICITLY sets grid visibility to a value that is NOT the per-set
            // baseline (0.75) — proving survival, not coincidence. /grid path → A7 owns it.
            const double UserGv = 0.20;
            engine.SetShaderParam(ShaderParam.GridVisibility, UserGv);

            DriveCalm(engine);

            // The set still moved (the user shader override does not block the selector)…
            var moved = new GridCoord(4, 2);
            Assert.True(ResolvesToSet(engine, moved),
                "the calm window must still move the set even with a user shader override");

            SenseState s = engine.Update(0.0, "Passenger", ClockBase.AddSeconds(DriveTicks * TickSeconds));

            // …but the user's gv SURVIVED the move — the set move did NOT clobber it back to the
            // per-set baseline (0.75). This is the A7 guard at work.
            Assert.Equal(UserGv, s.Shader.GridVisibility, 6);
            Assert.NotEqual(SetGrid.Curve(moved).ForLevel(s.VimsTier).GridVisibility, s.Shader.GridVisibility);
        }
        finally { AppConfig.AdaptiveFitSelectorEnabled = original; }
    }

    [Fact]
    public void FlagOn_UserFeatherOverride_SurvivesASilentSetMove()
    {
        bool original = AppConfig.AdaptiveFitSelectorEnabled;
        try
        {
            AppConfig.AdaptiveFitSelectorEnabled = true;

            var log = new InMemoryEventLogStore();
            SeedCalmDurable(log);
            var engine = new MockInferenceEngine(null, null, null, log)
            {
                AdaptiveFitSelectorEnabled = true,
            };

            // The user explicitly softens the feather well away from the default (0.50).
            const double UserFeather = 0.90;
            engine.SetShaderParam(ShaderParam.FeatherWidth, UserFeather);

            DriveCalm(engine);

            var moved = new GridCoord(4, 2);
            Assert.True(ResolvesToSet(engine, moved),
                "the calm window must still move the set even with a user feather override");

            SenseState s = engine.Update(0.0, "Passenger", ClockBase.AddSeconds(DriveTicks * TickSeconds));

            // The user's feather SURVIVED the set move (A7 guard) — not reset to the per-set
            // baseline (0.50 for the (4,2) Set0-column Shape).
            Assert.Equal(UserFeather, s.Shader.FeatherWidth, 6);
            Assert.NotEqual(SetGrid.Curve(moved).ForLevel(s.VimsTier).FeatherWidth, s.Shader.FeatherWidth);
        }
        finally { AppConfig.AdaptiveFitSelectorEnabled = original; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 3. Q4 LOCK at the engine resolve surface — Shape never moves a Band-only param.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveLevel_AcrossAllShapesAtFixedBand_NeverChangesBandOnlyParams()
    {
        // The engine's ResolveLevel reads SetGrid.Curve(activeSet). Sweep every Shape at a
        // fixed Band directly through SetGrid (the same curve the engine resolves) and confirm
        // the Band-only protection params are identical for all five shapes, level-for-level —
        // Shape drives ONLY the render character (gv/feather), never depth (Q4 lock).
        const int FixedBand = 6;
        SetCurve reference = SetGrid.Curve(new GridCoord(FixedBand, GridCoord.MinShape));
        for (int shape = GridCoord.MinShape + 1; shape <= GridCoord.MaxShape; shape++)
        {
            SetCurve other = SetGrid.Curve(new GridCoord(FixedBand, shape));
            for (int level = 1; level <= SetCurve.LevelCount; level++)
            {
                Assert.Equal(reference.ForLevel(level).PeakMaskAlpha,  other.ForLevel(level).PeakMaskAlpha, 6);
                Assert.Equal(reference.ForLevel(level).FovFloorPercent, other.ForLevel(level).FovFloorPercent);
                Assert.Equal(reference.ForLevel(level).Scale,          other.ForLevel(level).Scale, 6);
            }
        }

        // And the render character genuinely DOES move across the shapes (axis is live).
        var gvs = Enumerable.Range(GridCoord.MinShape, GridCoord.MaxShape - GridCoord.MinShape + 1)
            .Select(sh => SetGrid.Curve(new GridCoord(FixedBand, sh)).ForLevel(6).GridVisibility)
            .Distinct().Count();
        var fws = Enumerable.Range(GridCoord.MinShape, GridCoord.MaxShape - GridCoord.MinShape + 1)
            .Select(sh => SetGrid.Curve(new GridCoord(FixedBand, sh)).ForLevel(6).FeatherWidth)
            .Distinct().Count();
        Assert.True(gvs > 1 && fws > 1, "Shape must drive distinct gv + feather render character");
    }
}
