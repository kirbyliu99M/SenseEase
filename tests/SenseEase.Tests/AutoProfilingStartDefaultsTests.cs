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
/// Alpha-2 addendum (cancel explicit calibration → auto-profiling) — contract
/// tests for <see cref="MockInferenceEngine.ApplyAutoProfilingStartDefaults"/>.
///
/// The flag is engine-INSTANCE scoped (<c>engine.AutoProfilingEnabled</c>), NOT
/// the static <c>AppConfig.AutoProfilingEnabled</c>, so each test sets the
/// instance property to avoid cross-test static leakage (the engine never reads
/// the App layer directly — same rule as the AdaptiveFit selector flag).
///
/// Three invariants:
///   1. FLAG-OFF FRESH SEED (FEAT-07 / D-8, 2026-06-11) — flag-off NO LONGER restores
///      explicit calibration (3131387 deleted it), so a fresh user is still seeded
///      TYPICAL + L5; only App-side vestibular/console/selector-UI react to the flag.
///      The seed is no longer a flag-gated no-op.
///   2. FLAG-ON FRESH USER — a genuinely fresh user (no snapshot existed at load,
///      LoadedCalibrationCount==0, no vestibular) is seeded TYPICAL + L5.
///   3. FLAG-(ON/OFF) EXISTING USER — an existing user (persisted snapshot with a
///      positive calibration_count) is NEVER overwritten; the fresh-user guard
///      (_profileExistedAtLoad / LoadedCalibrationCount>0) — now the SOLE gate —
///      protects them regardless of the flag.
/// </summary>
public class AutoProfilingStartDefaultsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _profilePath;

    public AutoProfilingStartDefaultsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "senseease-autoprof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _profilePath = Path.Combine(_tempDir, "habituation_profile.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── 1. FLAG-OFF FRESH SEED (FEAT-07 / D-8) ───────────────────────────────
    // A default engine boots on RESILIENT, but a FRESH user must NOT be stranded there
    // when the flag is off — 3131387 deleted the explicit-calibration flow the old
    // "revert" promised, so flag-off now seeds TYPICAL/L5 just like flag-on. The flag
    // gates only App-side vestibular prompt / console visibility / selector-UI signals
    // (none of which live in the engine), so the seed runs regardless of the flag.

    [Fact]
    public void FlagOff_FreshUser_StillSeedsTypicalAndL5()
    {
        var engine = new MockInferenceEngine();

        // Today's default before any seeding.
        Assert.Equal(HabituationProfile.Resilient, engine.HabituationProfile);

        // Flag deliberately left at its default (false). Per D-8 the seed still applies —
        // the fresh-user guard is now the ONLY gate, and a default engine is fresh.
        Assert.False(engine.AutoProfilingEnabled);
        engine.ApplyAutoProfilingStartDefaults();

        Assert.Equal(HabituationProfile.Adapting, engine.HabituationProfile);
        Assert.Equal(SenseDefaults.AutoProfilingStartLevel, engine.CurrentLevel);
        Assert.Equal(5, engine.CurrentLevel);
    }

    // ── 2. FLAG-ON FRESH USER ────────────────────────────────────────────────
    // A brand-new user has NO profile snapshot (empty temp store ⇒ Load()
    // returns null ⇒ _profileExistedAtLoad==false, LoadedCalibrationCount==0, no
    // vestibular). With the flag ON, the seeder moves them to TYPICAL + L5「中」,
    // the Set0 median from which the AdaptiveFit selector converges.

    [Fact]
    public void FlagOn_FreshUser_SeedsTypicalProfileAndStartLevel()
    {
        // Genuine fresh user: a store pointed at a non-existent file ⇒ Load()
        // returns null ⇒ the engine boots with _profileExistedAtLoad==false.
        var store = new FileHabituationProfileStore(_profilePath);
        Assert.False(File.Exists(_profilePath)); // nothing persisted yet

        var engine = new MockInferenceEngine(profileStore: store);

        // Sanity: a fresh user has no prior calibrations.
        Assert.Equal(0, engine.LoadedCalibrationCount);
        Assert.False(engine.IsVestibularProtectionEnabled);
        // Pre-seed default is still RESILIENT.
        Assert.Equal(HabituationProfile.Resilient, engine.HabituationProfile);

        // Mirror the flag onto the engine INSTANCE (NOT static AppConfig), then seed.
        engine.AutoProfilingEnabled = true;
        engine.ApplyAutoProfilingStartDefaults();

        Assert.Equal(HabituationProfile.Adapting, engine.HabituationProfile);
        Assert.Equal(SenseDefaults.AutoProfilingStartLevel, engine.CurrentLevel);
        Assert.Equal(5, engine.CurrentLevel); // belt-and-suspenders: L5 median seed
    }

    // ── 3. FLAG-ON EXISTING USER NOT OVERWRITTEN ─────────────────────────────
    // Pre-write a SENSITIVE snapshot with a positive calibration_count (+ a
    // last_calibration_at_iso so the H44 anti-tamper guard does NOT zero the
    // count). Construct the engine from that store: _profileExistedAtLoad==true
    // AND LoadedCalibrationCount>0. The seeder must early-return, leaving the
    // persisted SENSITIVE profile intact — an existing user is never re-seeded.

    [Fact]
    public void FlagOn_ExistingUser_ProfileIsNotOverwritten()
    {
        IHabituationProfileStore store = new FileHabituationProfileStore(_profilePath);
        store.Save(new HabituationProfileSnapshot
        {
            // SchemaVersion left at its DTO default (current supported version).
            UpdatedAtIso         = "2026-06-01T08:00:00.0000000Z",
            Profile              = "SENSITIVE",
            ProfileSource        = "calibration_demo",
            // A positive count REQUIRES a matching last_calibration_at_iso, else
            // the engine's H44 anti-tamper clamp resets it to 0 on load (which
            // would weaken this test). Provide both so the guard reads count>0.
            CalibrationCount     = 3,
            LastCalibrationAtIso = "2026-06-01T08:00:00.0000000Z",
        });

        var engine = new MockInferenceEngine(profileStore: store);

        // Preconditions: the engine restored the existing user's state.
        Assert.Equal(HabituationProfile.Reactive, engine.HabituationProfile);
        Assert.True(engine.LoadedCalibrationCount > 0);

        // Flag ON, but the fresh-user guard must protect the existing profile.
        engine.AutoProfilingEnabled = true;
        engine.ApplyAutoProfilingStartDefaults();

        // UNCHANGED — still the persisted SENSITIVE profile, NOT TYPICAL.
        Assert.Equal(HabituationProfile.Reactive, engine.HabituationProfile);
        Assert.NotEqual(HabituationProfile.Adapting, engine.HabituationProfile);
    }
}
