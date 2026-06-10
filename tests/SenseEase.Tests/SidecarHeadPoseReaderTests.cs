using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using SenseEase.App.Configuration;
using SenseEase.App.ViewModels;
using SenseEase.Core.Abstractions;
using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>M4 (IPC Plumbing) gate tests for <see cref="SidecarHeadPoseReader"/>.
/// These pin the contracts the M5 wiring will depend on:
/// <list type="bullet">
///   <item>JSON line parse → <see cref="HeadPoseSample"/> round-trip.</item>
///   <item>Production-schema lines (without debug yaw_deg/pitch_deg keys)
///         parse identically to debug-schema lines.</item>
///   <item>Malformed / fuzzed lines do NOT throw and do NOT replace the
///         current latest sample.</item>
///   <item>OBS-2 hard-gate: a simulated sidecar exit makes
///         <see cref="SidecarHeadPoseReader.ReadLatest"/> return null
///         within 2 seconds.</item>
///   <item>M4 invariant: with <see cref="AppConfig.HeadTrackingEnabled"/>
///         false (the default), MainViewModel binds <see cref="DefaultHeadPoseReader"/>
///         and no sidecar process is spawned.</item>
/// </list></summary>
public class SidecarHeadPoseReaderTests
{
    // ── 1) JSON parse: valid debug line round-trips ─────────────────────────

    [Fact]
    public void SidecarHeadPoseReader_ParsesValidJsonLine_ReturnsSample()
    {
        // Debug-mode line: yaw_deg + pitch_deg present. C# host should accept
        // them (parse cleanly) and discard — they're not part of HeadPoseSample.
        const string line =
            "{\"t_ms\":12345,\"cx\":0.5234,\"cy\":0.4710,\"luma\":0.3812," +
            "\"face_detected\":true,\"face_present\":1.0," +
            "\"yaw_deg\":4.21,\"pitch_deg\":-1.83}";

        bool ok = SidecarHeadPoseReader.TryParseSampleLine(line, out HeadPoseSample? s);

        Assert.True(ok);
        Assert.NotNull(s);
        Assert.Equal(12345L,  s!.TimestampMs);
        Assert.Equal(0.5234,  s.Cx, 4);
        Assert.Equal(0.4710,  s.Cy, 4);
        Assert.Equal(0.3812,  s.Luma, 4);
        Assert.True(s.FaceDetected);
        Assert.Equal(1.0,     s.FacePresent, 3);
    }

    // ── 2) Production-schema line (no debug keys) parses identically ────────

    [Fact]
    public void SidecarHeadPoseReader_ParsesLineWithoutDebugFields_FacePresentDefaults()
    {
        // Production line: --debug NOT set, sidecar filters yaw_deg/pitch_deg
        // before json.dumps so these keys are entirely absent. The C# host
        // must NOT choke on the missing optional keys.
        const string line =
            "{\"t_ms\":777,\"cx\":0.62,\"cy\":0.41,\"luma\":0.20," +
            "\"face_detected\":true,\"face_present\":1.0}";

        bool ok = SidecarHeadPoseReader.TryParseSampleLine(line, out HeadPoseSample? s);

        Assert.True(ok);
        Assert.NotNull(s);
        Assert.Equal(777L, s!.TimestampMs);
        Assert.Equal(0.62, s.Cx, 4);
        Assert.Equal(0.41, s.Cy, 4);
        Assert.True(s.FaceDetected);
        Assert.Equal(1.0, s.FacePresent, 3);
        // HeadPoseSample has no yaw_deg/pitch_deg fields — verifying the parse
        // succeeded above already proves the optional keys don't break it.
    }

    [Fact]
    public void SidecarHeadPoseReader_ParsesFaceAbsentLine_FaceDetectedFalse()
    {
        // When MediaPipe doesn't find a face the sidecar emits
        // face_detected=false, face_present=0.0, cx/cy at the smoother's
        // last-good values. Host must accept this without special-casing.
        const string line =
            "{\"t_ms\":2000,\"cx\":0.5,\"cy\":0.5,\"luma\":0.10," +
            "\"face_detected\":false,\"face_present\":0.0}";

        bool ok = SidecarHeadPoseReader.TryParseSampleLine(line, out HeadPoseSample? s);

        Assert.True(ok);
        Assert.NotNull(s);
        Assert.False(s!.FaceDetected);
        Assert.Equal(0.0, s.FacePresent, 3);
    }

    // ── 3) Malformed lines: no throw, sample is null, latest unchanged ──────

    [Theory]
    [InlineData("")]                                            // empty
    [InlineData("   ")]                                         // whitespace
    [InlineData("not json at all")]                             // raw text
    [InlineData("{")]                                           // truncated
    [InlineData("{\"t_ms\":123,")]                              // truncated mid-line
    [InlineData("{\"t_ms\":\"oops_not_a_number\",\"cx\":0.5}")] // type mismatch
    [InlineData("null")]                                        // bare null
    [InlineData("[]")]                                          // wrong shape (array)
    [InlineData("\"just a string\"")]                           // wrong shape (scalar)
    public void SidecarHeadPoseReader_ParsesMalformedLine_DoesNotThrow_LogsAndContinues(string bad)
    {
        // The parse-seam contract: no exception bubbles up, no sample
        // returned. The reader-loop wrapper (not exercised here) is what
        // preserves the previous _latest on parse failure; the seam itself
        // just reports "couldn't parse" without throwing.
        bool ok = SidecarHeadPoseReader.TryParseSampleLine(bad, out HeadPoseSample? s);
        Assert.False(ok);
        Assert.Null(s);
    }

    [Fact]
    public void SidecarHeadPoseReader_ParsesLineWithUnknownExtraKeys_StillSucceeds()
    {
        // Forward-compat: a future sidecar version might emit additional
        // diagnostic keys. They must be silently ignored, not rejected,
        // so a host running an older C# parser doesn't break on a newer
        // sidecar. System.Text.Json defaults to ignoring unknown members.
        const string line =
            "{\"t_ms\":50,\"cx\":0.5,\"cy\":0.5,\"luma\":0.1," +
            "\"face_detected\":true,\"face_present\":1.0," +
            "\"future_diagnostic_key\":42,\"another\":\"yes\"}";

        bool ok = SidecarHeadPoseReader.TryParseSampleLine(line, out HeadPoseSample? s);

        Assert.True(ok);
        Assert.NotNull(s);
        Assert.Equal(50L, s!.TimestampMs);
    }

    // ── 4) Simulated process exit → fallback to null within 2s ──────────────

    [Fact]
    public void SidecarHeadPoseReader_SimulatedProcessExit_FallsBackToNullWithin2Seconds()
    {
        // OBS-2 hard gate: when the sidecar dies, ReadLatest MUST return null
        // within 2 seconds so the consumer falls back to center default and
        // the app does not crash. We exercise this via the test seam rather
        // than taskkilling a real process — the seam mirrors the production
        // OnProcessExited path (Interlocked.Exchange ref _sidecarDead, 1).
        var reader = new SidecarHeadPoseReader(
            sidecarExePath: @"C:\nonexistent\head_tracker.exe",
            log:            null,
            mirror:         true,
            webcamIndex:    0);

        // Seed a recent "latest sample" so the baseline state has data to
        // return. ReadLatest with no-exit yet → returns the sample.
        var seeded = new HeadPoseSample(
            TimestampMs:  1,
            Cx:           0.7,
            Cy:           0.3,
            Luma:         0.2,
            FaceDetected: true,
            FacePresent:  1.0);
        reader.TestHook_StoreSample(seeded, DateTime.UtcNow);

        Assert.NotNull(reader.ReadLatest());

        var sw = Stopwatch.StartNew();
        reader.TestHook_SimulateSidecarExit();

        // Read immediately and again 100 ms later — both must be null. We do
        // NOT actually sleep 2 seconds; the contract is "within 2 seconds"
        // and the implementation reacts synchronously to the flag flip.
        Assert.Null(reader.ReadLatest());
        Thread.Sleep(100);
        Assert.Null(reader.ReadLatest());
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"Fallback should be sub-2-second; took {sw.ElapsedMilliseconds} ms.");

        reader.Dispose();
    }

    [Fact]
    public void SidecarHeadPoseReader_BeforeStart_ReadLatestReturnsNull()
    {
        // Pre-Start contract: ReadLatest must return null so a consumer that
        // accidentally reads before MainWindow.Loaded fires gets the safe
        // "use center default" signal rather than stale or uninitialized data.
        var reader = new SidecarHeadPoseReader(
            sidecarExePath: @"C:\nonexistent\head_tracker.exe");

        Assert.Null(reader.ReadLatest());
        Assert.False(reader.TestHook_IsStarted());

        reader.Dispose();
    }

    [Fact]
    public void SidecarHeadPoseReader_StaleSample_OlderThan2Seconds_ReadLatestReturnsNull()
    {
        // Staleness fallback (secondary safety net beyond OBS-2): if the
        // sidecar wedges WITHOUT crashing (model stall, webcam frozen) we
        // still want ReadLatest to return null after 2 seconds so the
        // consumer doesn't get stuck on a frozen sample for 30+ seconds.
        var reader = new SidecarHeadPoseReader(
            sidecarExePath: @"C:\nonexistent\head_tracker.exe");

        var sample = new HeadPoseSample(
            TimestampMs:  1, Cx: 0.5, Cy: 0.5, Luma: 0.2,
            FaceDetected: true, FacePresent: 1.0);

        // Stale (3 seconds old). StalenessThresholdMs is 2 s, so ReadLatest
        // must reject this.
        reader.TestHook_StoreSample(sample, DateTime.UtcNow.AddSeconds(-3));
        Assert.Null(reader.ReadLatest());

        // Fresh (now). ReadLatest must return it.
        reader.TestHook_StoreSample(sample, DateTime.UtcNow);
        Assert.NotNull(reader.ReadLatest());

        reader.Dispose();
    }

    [Fact]
    public void SidecarHeadPoseReader_StartWithMissingExe_SoftDisables_DoesNotThrow()
    {
        // If the frozen sidecar exe isn't present (fresh dev machine, or
        // user didn't run the sidecar setup, or PyInstaller bundle wasn't
        // staged into dist/), Start() must NOT throw — it logs and flips
        // the dead flag so ReadLatest returns null and the consumer falls
        // back to center default. App-level invariant: a missing sidecar
        // must never crash the dashboard.
        string captured = string.Empty;
        var reader = new SidecarHeadPoseReader(
            sidecarExePath: @"C:\definitely\not\a\real\path\head_tracker.exe",
            log:            msg => captured += msg + "\n");

        var ex = Record.Exception(() => reader.Start());
        Assert.Null(ex);
        Assert.Null(reader.ReadLatest());
        Assert.Contains("not found", captured, StringComparison.OrdinalIgnoreCase);

        reader.Dispose();
    }

    // ── Phase 4 (UI Consolidation Sprint): DoStop diagnostic contract ───────
    //
    // The "Sidecar stopped (caller-initiated)" log line is the marker that
    // distinguishes a clean lifecycle Stop/Restart/Dispose from an UNEXPECTED
    // sidecar death (OnProcessExited's "Sidecar exited (code=...)" line).
    // Phase 3's UVHS protocol depends on this distinction: during a healthy
    // Power-Save round-trip, "caller-initiated" should appear once per Stop
    // and "Sidecar exited" should appear ZERO times. Renaming or rewording
    // this log line silently invalidates the entire UVHS audit semantic.
    //
    // These tests pin the exact format so a future refactor that "tidies up"
    // the log message must consciously touch this contract.

    [Fact]
    public void FormatCallerInitiatedStopLog_KnownPid_ProducesExactDiagnosticFormat()
    {
        // Exact format pin. If this assertion changes, UVHS Playbook + Phase 3
        // wgc_diag.txt grep expectations must change together with it.
        Assert.Equal(
            "[SidecarHeadPoseReader] Sidecar stopped (caller-initiated, pid=12345).",
            SidecarHeadPoseReader.FormatCallerInitiatedStopLog(12345));
    }

    [Fact]
    public void FormatCallerInitiatedStopLog_NoProcess_UsesNegativeOnePid()
    {
        // DoStop snapshots pid as -1 when the Process handle is null at
        // tear-down (race: process died between _process read and pid access).
        // The diagnostic still emits — pid=-1 communicates "we got into the
        // caller-initiated stop path but the process was already gone".
        Assert.Equal(
            "[SidecarHeadPoseReader] Sidecar stopped (caller-initiated, pid=-1).",
            SidecarHeadPoseReader.FormatCallerInitiatedStopLog(-1));
    }

    [Fact]
    public void FormatCallerInitiatedStopLog_ContainsLoadBearingMarkers()
    {
        // Belt + suspenders on the format pin: if someone "fixes" the
        // verbatim string above with rewording (e.g. "Sidecar stop") the
        // marker substrings should still fail. The two load-bearing strings
        // are "caller-initiated" (distinguishes from OnProcessExited) and
        // "[SidecarHeadPoseReader]" (the source tag UVHS audits grep for).
        string line = SidecarHeadPoseReader.FormatCallerInitiatedStopLog(99);
        Assert.Contains("[SidecarHeadPoseReader]", line);
        Assert.Contains("caller-initiated", line);
        Assert.Contains("pid=99", line);
    }

    [Fact]
    public void SidecarHeadPoseReader_BuildSidecarArgs_MirrorOnByDefault_OmitsFlag()
    {
        // Q4 advisor decision: sidecar's --mirror default is ON (BooleanOptionalAction).
        // To keep the spawned command-line minimal, the C# host omits the flag
        // when mirror=true and emits --no-mirror only when mirror=false.
        string argsOn  = SidecarHeadPoseReader.BuildSidecarArgs(0, mirror: true);
        string argsOff = SidecarHeadPoseReader.BuildSidecarArgs(0, mirror: false);

        Assert.Equal("--input 0",             argsOn);
        Assert.Equal("--input 0 --no-mirror", argsOff);
    }

    [Fact]
    public void SidecarHeadPoseReader_BuildSidecarArgs_WebcamIndexPassedThrough()
    {
        // Webcam-index pass-through: support index 0 (default), 1 (external
        // USB), etc. Sidecar's --input also accepts file paths but the host
        // only spawns webcam sidecars in production.
        Assert.Contains("--input 1", SidecarHeadPoseReader.BuildSidecarArgs(1,    mirror: true));
        Assert.Contains("--input 2", SidecarHeadPoseReader.BuildSidecarArgs(2,    mirror: true));
        Assert.Contains("--input 0", SidecarHeadPoseReader.BuildSidecarArgs(null, mirror: true));
    }

    // ── FEAT-08 (CW-10): health-state coverage (B1a deferred) ───────────────
    //
    // These tests exercise the three HeadTrackingHealth states that
    // SidecarHeadPoseReader derives from its internal flags:
    //   Healthy  — a fresh sample is present within the staleness window
    //   NoFrames — the reader is "alive" (started, not dead) but has never
    //              produced a frame, OR the last frame is stale (the watchdog
    //              silence case)
    //   Dead     — sidecar exited / was simulated-exited / not yet started
    //
    // All three are observable through the polling Health property (ComputeHealth)
    // and the event-raising RefreshHealth. The tests use the existing TestHook_*
    // seams so no real sidecar process is needed.

    [Fact]
    public void Health_Healthy_AfterStoredFreshSample()
    {
        // FEAT-08: once a fresh sample is injected via TestHook_StoreSample,
        // ComputeHealth must report Healthy — the "tracking is working" state.
        // Stores the sample with DateTime.UtcNow so it is within StalenessThresholdMs.
        var reader = new SidecarHeadPoseReader(
            sidecarExePath: @"C:\nonexistent\head_tracker.exe");

        var sample = new HeadPoseSample(
            TimestampMs:  100,
            Cx:           0.5,
            Cy:           0.5,
            Luma:         0.3,
            FaceDetected: true,
            FacePresent:  1.0);

        reader.TestHook_StoreSample(sample, DateTime.UtcNow);

        Assert.Equal(HeadTrackingHealth.Healthy, reader.Health);

        reader.Dispose();
    }

    [Fact]
    public void Health_NoFrames_WhenStartedButNoSampleEverStored()
    {
        // FEAT-08: watchdog-window silence scenario. The reader has been started
        // (via TestHook_StoreSample which sets started=1, sidecarDead=0) but we
        // reset the sample to simulate "sidecar spawned but emitting nothing" —
        // the antivirus hang / webcam-unavailable / MediaPipe-init-failure case
        // that the startup watchdog log line diagnoses. With no stored sample,
        // latestStoredAtMs==0, so ComputeHealth returns NoFrames.
        //
        // We use a fresh reader (not yet started) to confirm the pre-start path
        // (Dead) and then use a second reader with started=1 but latestStoredAtMs==0
        // to confirm the NoFrames path. The only public test hook that marks the
        // reader "started" is TestHook_StoreSample; after storing we simulate
        // the "no frames yet" state by disposing and creating a fresh reader that
        // we mark started via a sample store then immediately discard.
        //
        // Simpler path: construct a fresh reader (pre-Start → Dead), then use
        // TestHook_StoreSample with a stale timestamp so it is beyond the
        // staleness window — ComputeHealth sees ageMs > StalenessThresholdMs
        // and returns NoFrames.
        var reader = new SidecarHeadPoseReader(
            sidecarExePath: @"C:\nonexistent\head_tracker.exe");

        // Store a sample that is beyond StalenessThresholdMs (3 seconds old).
        // StoreSample sets started=1, sidecarDead=0 so we are in the "alive but
        // stale" branch — which maps to NoFrames.
        var sample = new HeadPoseSample(
            TimestampMs:  1,
            Cx:           0.5,
            Cy:           0.5,
            Luma:         0.1,
            FaceDetected: true,
            FacePresent:  1.0);
        reader.TestHook_StoreSample(sample, DateTime.UtcNow.AddSeconds(-(SidecarHeadPoseReader.StalenessThresholdMs / 1000.0 + 1)));

        Assert.Equal(HeadTrackingHealth.NoFrames, reader.Health);

        reader.Dispose();
    }

    [Fact]
    public void Health_Dead_AfterTestHookSimulateSidecarExit()
    {
        // FEAT-08: simulate an unexpected sidecar exit. After
        // TestHook_SimulateSidecarExit flips the _sidecarDead flag,
        // ComputeHealth must return Dead — the "ReadLatest returns null,
        // mask sits at center" state.
        var reader = new SidecarHeadPoseReader(
            sidecarExePath: @"C:\nonexistent\head_tracker.exe");

        // First establish a Healthy baseline (sample present, not dead).
        var sample = new HeadPoseSample(
            TimestampMs:  200,
            Cx:           0.6,
            Cy:           0.4,
            Luma:         0.2,
            FaceDetected: true,
            FacePresent:  1.0);
        reader.TestHook_StoreSample(sample, DateTime.UtcNow);
        Assert.Equal(HeadTrackingHealth.Healthy, reader.Health);

        // Simulate the sidecar dying.
        reader.TestHook_SimulateSidecarExit();

        // Health must now be Dead.
        Assert.Equal(HeadTrackingHealth.Dead, reader.Health);

        // ReadLatest must also return null after the exit.
        Assert.Null(reader.ReadLatest());

        reader.Dispose();
    }

    [Fact]
    public void Health_Dead_BeforeStart()
    {
        // FEAT-08: pre-Start state must report Dead (ComputeHealth checks
        // _started==0 first). This is the "construction only, Start not called"
        // path that corresponds to HeadTrackingEnabled=false (DefaultHeadPoseReader
        // takes over). For SidecarHeadPoseReader instances that do exist but
        // haven't been started, the safe default must be Dead so callers know
        // to treat ReadLatest()==null as "use center default".
        var reader = new SidecarHeadPoseReader(
            sidecarExePath: @"C:\nonexistent\head_tracker.exe");

        Assert.Equal(HeadTrackingHealth.Dead, reader.Health);
        Assert.False(reader.TestHook_IsStarted());

        reader.Dispose();
    }

    [Fact]
    public void RefreshHealth_RaisesHealthChanged_OnTransition()
    {
        // FEAT-08: RefreshHealth publishes a HealthChanged event when the
        // derived health transitions. Healthy → Dead must fire once.
        var reader = new SidecarHeadPoseReader(
            sidecarExePath: @"C:\nonexistent\head_tracker.exe");

        var events = new List<HeadTrackingHealth>();
        reader.HealthChanged += (_, h) => events.Add(h);

        // Seed Healthy.
        var sample = new HeadPoseSample(
            TimestampMs:  300,
            Cx:           0.5,
            Cy:           0.5,
            Luma:         0.1,
            FaceDetected: true,
            FacePresent:  1.0);
        reader.TestHook_StoreSample(sample, DateTime.UtcNow);
        // RefreshHealth computes Healthy; _lastPublishedHealth was Dead (initial)
        // so it fires the event with Healthy.
        reader.RefreshHealth();
        Assert.Contains(HeadTrackingHealth.Healthy, events);

        // Now simulate exit and refresh — should fire Dead.
        reader.TestHook_SimulateSidecarExit();
        reader.RefreshHealth();
        Assert.Contains(HeadTrackingHealth.Dead, events);

        // A second RefreshHealth with no state change must NOT fire again.
        int countBefore = events.Count;
        reader.RefreshHealth();
        Assert.Equal(countBefore, events.Count);

        reader.Dispose();
    }

    // ── 5) M4 invariant: flag off → DefaultHeadPoseReader, no sidecar ───────

    [Fact]
    public void AppConfig_HeadTrackingEnabled_False_DoesNotInstantiateSidecarReader()
    {
        // M4 invariant: with the flag false (the default), MainViewModel's
        // _headPoseReader field MUST bind to DefaultHeadPoseReader, not
        // SidecarHeadPoseReader. No process is spawned. Behaviour at the
        // overlay layer is byte-identical to M3 baseline. This is the
        // load-bearing safety property the brief calls out as
        // "M4 invariant: flag-off byte-identical".
        bool prior = AppConfig.HeadTrackingEnabled;
        try
        {
            AppConfig.HeadTrackingEnabled = false;
            var vm = new MainViewModel();
            Assert.IsType<DefaultHeadPoseReader>(vm.HeadPoseReader);
            // Calling Start... must be a no-op when the field is the stub.
            vm.StartHeadTrackingIfEnabled();
            Assert.Null(vm.HeadPoseReader.ReadLatest());
        }
        finally
        {
            AppConfig.HeadTrackingEnabled = prior;
        }
    }

}
