using System;
using System.IO;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Configuration;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Wave B1a (G1a) engine integrity — four behaviours pinned here:
///   1. CW-7 / LOGIC-10  — shader personalization (MaskDepth / GridVisibility /
///      FeatherWidth) survives a restart, replayed from the adjustment log; untouched
///      knobs keep their profile defaults.
///   2. FEAT-07 / D-8    — flag-OFF still seeds a fresh user at TYPICAL / L5 (the
///      revert no longer strands them at RESILIENT/L1).
///   3. LOGIC-03         — the OFF-dispatcher persist queue still round-trips the
///      snapshot to disk and a flush drains it.
///   4. LOGIC-13 / D-5   — the freshly-seeded L5 median is held against the first-tick
///      fast-release during the onboarding window, then normal projection resumes.
/// </summary>
public class MockInferenceEngineWaveB1aTests : IDisposable
{
    private readonly string _tempDir;

    public MockInferenceEngineWaveB1aTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "senseease-b1a-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string Path2(string name) => Path.Combine(_tempDir, name);

    // ══════════════════════════════════════════════════════════════════════════
    // 1. CW-7 / LOGIC-10 — shader replay at boot
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Ctor_ReplaysPersistedShaderKnobs_UntouchedKnobsKeepProfileDefaults()
    {
        // Persist a user-driven MaskDepth tightening + a FeatherWidth softening to the
        // adjustment log (the same store the chatbot/G1 path appends to). GridVisibility is
        // left untouched, so it must restore to the profile default (here RESILIENT/TYPICAL
        // baseline = ShaderParams.Default).
        string logPath = Path2("adjustments.jsonl");
        var seedLog = new FileAdjustmentLog(logPath);
        seedLog.Append(new AdjustmentRecord
        {
            Parameter = "MaskDepth", Delta = -0.20, Source = "user",
            OldValue = ShaderParams.DefaultMaskDepth, NewValue = ShaderParams.DefaultMaskDepth - 0.20,
        });
        seedLog.Append(new AdjustmentRecord
        {
            Parameter = "FeatherWidth", Delta = 0.10, Source = "user",
            OldValue = ShaderParams.DefaultFeatherWidth, NewValue = ShaderParams.DefaultFeatherWidth + 0.10,
        });

        // Restart: a fresh engine constructed from the SAME on-disk log.
        var replayLog = new FileAdjustmentLog(logPath);
        var engine = new MockInferenceEngine(adjustmentLog: replayLog);

        var shader = engine.Current.Shader;
        // Touched knobs restored to base + persisted net delta.
        Assert.Equal(ShaderParams.DefaultMaskDepth - 0.20, shader.MaskDepth, 6);
        Assert.Equal(ShaderParams.DefaultFeatherWidth + 0.10, shader.FeatherWidth, 6);
        // Untouched knob keeps the profile default (NOT zeroed, NOT reset to a bare new()).
        Assert.Equal(ShaderParams.DefaultGridVisibility, shader.GridVisibility, 6);
    }

    [Fact]
    public void Ctor_GridOff_SurvivesRestart()
    {
        // The headline NE2 symptom: "/grid off" (GridVisibility → 0) reverting to 0.75 on
        // every launch. A persisted full-down GridVisibility delta must restore to ~0.
        string logPath = Path2("adjustments.jsonl");
        var seedLog = new FileAdjustmentLog(logPath);
        seedLog.Append(new AdjustmentRecord
        {
            Parameter = "GridVisibility", Delta = -ShaderParams.DefaultGridVisibility, Source = "user",
            OldValue = ShaderParams.DefaultGridVisibility, NewValue = 0.0,
        });

        var engine = new MockInferenceEngine(adjustmentLog: new FileAdjustmentLog(logPath));

        Assert.Equal(0.0, engine.Current.Shader.GridVisibility, 6);
    }

    [Fact]
    public void Ctor_NoShaderHistory_KeepsProfileDefaults()
    {
        // Only a FOV adjustment exists (no shader knobs touched) — all three shader knobs
        // must stay at the profile defaults, byte-identical to today.
        string logPath = Path2("adjustments.jsonl");
        var seedLog = new FileAdjustmentLog(logPath);
        seedLog.Append(new AdjustmentRecord { Parameter = "FovRadius", Delta = -10, Source = "user" });

        var engine = new MockInferenceEngine(adjustmentLog: new FileAdjustmentLog(logPath));

        var shader = engine.Current.Shader;
        Assert.Equal(ShaderParams.DefaultMaskDepth, shader.MaskDepth, 6);
        Assert.Equal(ShaderParams.DefaultGridVisibility, shader.GridVisibility, 6);
        Assert.Equal(ShaderParams.DefaultFeatherWidth, shader.FeatherWidth, 6);
    }

    [Fact]
    public void Ctor_ShaderReplay_HonoursResetTare()
    {
        // The reset sentinel tares the log (same semantics as WeightFor("FovRadius")): a
        // shader delta BEFORE a reset must not be replayed.
        string logPath = Path2("adjustments.jsonl");
        var seedLog = new FileAdjustmentLog(logPath);
        seedLog.Append(new AdjustmentRecord { Parameter = "MaskDepth", Delta = -0.30, Source = "user" });
        seedLog.Append(new AdjustmentRecord { Parameter = "Reset",     Delta = 0,     Source = "reset" });

        var engine = new MockInferenceEngine(adjustmentLog: new FileAdjustmentLog(logPath));

        // The pre-reset MaskDepth delta is tared away → profile default restored.
        Assert.Equal(ShaderParams.DefaultMaskDepth, engine.Current.Shader.MaskDepth, 6);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 2. FEAT-07 / D-8 — flag-OFF keeps the TYPICAL / L5 seed
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void FlagOff_FreshUser_StillSeedsTypicalAndL5()
    {
        // D-8: flag-off no longer "restores explicit calibration" (deleted in 3131387); it
        // must NOT strand a fresh user at RESILIENT/L1. A fresh user (no snapshot, no prior
        // calibration, no vestibular) seeds TYPICAL/L5 even with AutoProfilingEnabled=false.
        string profilePath = Path2("habituation_profile.json");
        var store = new FileHabituationProfileStore(profilePath);
        Assert.False(File.Exists(profilePath)); // genuine fresh user

        var engine = new MockInferenceEngine(profileStore: store);
        Assert.Equal(HabituationProfile.Resilient, engine.HabituationProfile); // pre-seed default

        // Flag deliberately left OFF.
        Assert.False(engine.AutoProfilingEnabled);
        engine.ApplyAutoProfilingStartDefaults();

        // Seed still applied (the flag no longer gates it).
        Assert.Equal(HabituationProfile.Adapting, engine.HabituationProfile);
        Assert.Equal(SenseDefaults.AutoProfilingStartLevel, engine.CurrentLevel);
        Assert.Equal(5, engine.CurrentLevel);
    }

    [Fact]
    public void FlagOff_ExistingUser_IsStillNotOverwritten()
    {
        // The fresh-user guard is now the SOLE gate — an existing calibrated user must still
        // be protected even with the flag off.
        string profilePath = Path2("habituation_profile.json");
        IHabituationProfileStore store = new FileHabituationProfileStore(profilePath);
        store.Save(new HabituationProfileSnapshot
        {
            UpdatedAtIso         = "2026-06-01T08:00:00.0000000Z",
            Profile              = "SENSITIVE",
            ProfileSource        = "calibration_demo",
            CalibrationCount     = 2,
            LastCalibrationAtIso = "2026-06-01T08:00:00.0000000Z",
        });

        var engine = new MockInferenceEngine(profileStore: store);
        Assert.Equal(HabituationProfile.Reactive, engine.HabituationProfile);

        Assert.False(engine.AutoProfilingEnabled); // flag OFF
        engine.ApplyAutoProfilingStartDefaults();

        // Unchanged — the existing SENSITIVE profile survives.
        Assert.Equal(HabituationProfile.Reactive, engine.HabituationProfile);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 3. LOGIC-03 — off-dispatcher persist queue still round-trips + flushes
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void BackgroundPersist_DefaultsOff_PersistsSynchronously()
    {
        // Default (OFF) path: the persist is synchronous, so the file is on disk the instant
        // Update returns — the read-after-write contract existing tests depend on.
        string profilePath = Path2("habituation_profile.json");
        var store = new FileHabituationProfileStore(profilePath);
        var engine = new MockInferenceEngine(profileStore: store);
        Assert.False(engine.BackgroundPersistEnabled);
        engine.SetHabituationPaused(false);

        engine.Update(50.0, "Passenger");

        Assert.True(File.Exists(profilePath));
        Assert.Equal("RESILIENT", store.Load()!.Profile);
    }

    [Fact]
    public void BackgroundPersist_Enabled_RoundTripsAfterFlush()
    {
        // ON path: the persist runs on the single-writer background queue; after a flush the
        // snapshot must be on disk with the same state (W2.0-C carry-forward intact).
        string profilePath = Path2("habituation_profile.json");
        var store = new FileHabituationProfileStore(profilePath);
        var engine = new MockInferenceEngine(profileStore: store) { BackgroundPersistEnabled = true };

        // A profile change is a guaranteed persist trigger.
        engine.SetHabituationProfile(HabituationProfile.Reactive, "calibration_demo");
        engine.FlushPendingPersists();

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal("REACTIVE", loaded!.Profile);   // Stage 4a: persisted string follows the rename
        Assert.Equal("calibration_demo", loaded.ProfileSource);
    }

    [Fact]
    public void FlushPendingPersists_IsSafe_WhenNothingQueued()
    {
        var engine = new MockInferenceEngine();
        var ex = Record.Exception(() => engine.FlushPendingPersists());
        Assert.Null(ex);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 4. LOGIC-13 / D-5 — fresh-seed onboarding protection
    // ══════════════════════════════════════════════════════════════════════════

    private static readonly DateTime ClockBase = new(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FreshSeed_CalmTicksWithinWindow_TierStaysAtFive()
    {
        // Seed L5, then drive calm (zero-OFI) ticks INSIDE the onboarding window. Without the
        // D-5 protection the very first tick would fast-release the tier to ~L1; with it the
        // median holds.
        var engine = new MockInferenceEngine { AutoProfilingEnabled = true };
        engine.ApplyAutoProfilingStartDefaults();
        Assert.Equal(5, engine.CurrentLevel);

        // A handful of calm ticks, total elapsed well under the protection window.
        for (int i = 0; i < 5; i++)
            engine.Update(0.0, "Passenger", ClockBase.AddSeconds(i * 1.0));

        Assert.Equal(5, engine.CurrentLevel); // held — the fresh user actually sees L5「中」
    }

    [Fact]
    public void FreshSeed_AfterWindowElapses_NormalProjectionResumes()
    {
        // Drive calm ticks PAST the protection window: normal projection takes over and the
        // calm pressure releases the tier off the median.
        var engine = new MockInferenceEngine { AutoProfilingEnabled = true };
        engine.ApplyAutoProfilingStartDefaults();
        Assert.Equal(5, engine.CurrentLevel);

        // Two ticks whose dt sum exceeds the window (each clamped to <= 2.0 s of real dt, so
        // step the wall-clock by large gaps; the engine clamps dt but accumulates each tick).
        // Use enough ticks that accumulated dt clears the SettlingMinSeconds window.
        for (int i = 0; i < 40; i++)
            engine.Update(0.0, "Passenger", ClockBase.AddSeconds(i * 2.0));

        Assert.True(engine.CurrentLevel < 5,
            "after the onboarding window the calm projection should release the tier off L5");
    }

    [Fact]
    public void FreshSeed_GenuinePressureIncrease_EndsProtectionAndRises()
    {
        // A genuine pressure surge during the window ends protection immediately and the tier
        // rises (the user actually needs more protection — the median was never a ceiling).
        var engine = new MockInferenceEngine { AutoProfilingEnabled = true };
        engine.ApplyAutoProfilingStartDefaults();
        Assert.Equal(5, engine.CurrentLevel);

        // Sustained high OFI drives pressure up; tier should climb above the L5 seed.
        for (int i = 0; i < 20; i++)
            engine.Update(120.0, "Passenger", ClockBase.AddSeconds(i * 0.5));

        Assert.True(engine.CurrentLevel > 5,
            "a genuine pressure-driven increase must end onboarding protection and raise the tier");
    }

    [Fact]
    public void NonSeededEngine_HasNoOnboardingProtection_FastReleaseUnchanged()
    {
        // Byte-identity guard: an engine that never seeded (no ApplyAutoProfilingStartDefaults)
        // must behave exactly as today — a calm tick after a high tier fast-releases normally.
        var engine = new MockInferenceEngine();

        // Drive pressure up to climb the tier, then go calm — the fast-release should fire
        // (no onboarding hold, because protection was never armed).
        for (int i = 0; i < 10; i++)
            engine.Update(120.0, "Passenger", ClockBase.AddSeconds(i * 0.5));
        int high = engine.CurrentLevel;
        Assert.True(high > 1);

        for (int i = 0; i < 5; i++)
            engine.Update(0.0, "Passenger", ClockBase.AddSeconds(10 + i * 0.5));

        Assert.True(engine.CurrentLevel < high, "calm fast-release must still work for a non-seeded engine");
    }
}
