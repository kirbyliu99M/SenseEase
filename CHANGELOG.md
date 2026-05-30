# Changelog

All notable changes to SenseEase are documented in this file.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning follows
the sprint-seal git tag pattern (`v0.<sprint>` and `v0.<sprint>.<hotfix>`).
The authoritative version reference is `git tag` — this file is the human-readable summary.

## [Unreleased]

UI Consolidation Sprint, in progress. Will be tagged **v0.11** at sprint seal.

### Added
- **Phase 4 / P4-3 — `WgcDiagLog` shared diagnostic-log utility**
  ([WgcDiagLog.cs](src/SenseEase.Infrastructure/Real/WgcDiagLog.cs)).
  Eliminates the duplicated `try { File.AppendAllText(...) } catch { }`
  pattern across WGC provider, sidecar lifecycle logger, tray ctor +
  watchdog, MainWindow Loaded instrumentation, and App.OnStartup. Adds
  three guarantees that pattern lacked: (1) retry on `IOException` (3
  attempts with short back-off), (2) monotonic `[N=xxxxxxx]` sequence
  prefix as orthogonal probe (gap in N reveals silent loss even after
  retries fail), (3) independent loss sink at `%TEMP%/wgc_diag.lost.log`
  recording the failed-write event itself. Addresses the advisor's WGC
  truncation flag from the head-tracking sprint G_mid review (UVHS
  Playbook clause 5).
- **Phase 4 / P4-2 — display-baseline.json delete migration**
  in `App.OnStartup` ([App.xaml.cs](src/SenseEase.App/App.xaml.cs)).
  One-time idempotent cleanup of `%LOCALAPPDATA%\SenseEase\display-baseline.json`
  orphaned by Display Comfort decommission (see Removed).
- **Phase 4 / P4-4 — `SidecarHeadPoseReader.FormatCallerInitiatedStopLog`**
  ([SidecarHeadPoseReader.cs](src/SenseEase.Infrastructure/Real/SidecarHeadPoseReader.cs))
  + three CI tests
  ([SidecarHeadPoseReaderTests.cs](tests/SenseEase.Tests/SidecarHeadPoseReaderTests.cs)).
  Pins the "Sidecar stopped (caller-initiated, pid=N)" log format that
  Phase 3's UVHS protocol depends on to distinguish clean Stop from
  unexpected death.

### Changed
- **Phase 4 / P4-3 — WGC log no longer truncates on Start.**
  `WgcSensingProvider.Start()` previously called `File.WriteAllText` on the
  shared `%TEMP%/wgc_diag.txt`, which would silently erase the full log
  history when the truncate succeeded. Now appends a `=== WGC Start ===`
  delimiter line via `WgcDiagLog.WriteHeader`, preserving cross-restart
  history. Same applies to the OFI validation trace (`wgc_ofi_trace.txt`)
  except its truncation is intentional (per-session CSV) — routed through
  `WgcDiagLog.ResetSessionFile` so silent truncation failures are surfaced.
- **Phase 4 / P4-5 — ApplyPowerSave epoch coalescing comment clarified**
  ([MainWindow.xaml.cs](src/SenseEase.App/MainWindow.xaml.cs)).
  Notes coalescing is best-effort (a stale task that loaded its `myEpoch`
  before the next click's increment still executes); the in-sidecar
  `_lifecycleGate` remains the load-bearing correctness guarantee, the
  epoch is a perf hint.

### Removed
- _(Pending P4-1 dispatch authorization)_ — **Display Comfort feature
  decommissioned at depth 2.** Removes the entire DDC/CI monitor mutation
  pipeline, ambient-light sensing, lux→comfort mapping, and the M6
  `SidecarLuxProvider` luma piggyback (just shipped in the head-tracking
  sprint as commit `d53d8bb`). Rationale: the feature was unreliable in
  field testing across Windows DDC/CI driver variations and is now removed
  rather than left as opt-in dead code. The Python sidecar continues to
  emit luma in its JSON protocol (algorithm core is locked); the C#
  consumer side disappears.

### Locked decision / baseline
- **M3 head-tracking invariant baseline → v0.11.** The "M3 invariant"
  pin (flag-off → `DefaultHeadPoseReader` → center default cx/cy=0.5,
  brush radius 0.85, byte-equivalent to v0.9 baseline) is orthogonal to
  Display Comfort and remains valid. Baseline reference label bumps with
  the sprint seal so future test deltas measure against v0.11, not v0.10.

---

## [0.10] - 2026-05-30

Head-tracking sprint sealed. See [head-tracking-sprint-state memory] for
the full 32-commit arc (M1 → M8) with advisor gates G1/G4/G6/G7 PASS and
706 tests passing / 0 failing / 1 skipped.

### Added
- **M1-M2** — Python sidecar with MediaPipe FaceLandmarker, JSON IPC
  protocol, host-side reader (`SidecarHeadPoseReader`).
- **M3** — Head-tracking opt-in via `AppConfig.HeadTrackingEnabled`
  (default false); flag-off path byte-equivalent to v0.9 overlay.
- **M5 / M5.1-B / M5.1-C** — FOV mask follows head gaze; Balanced /
  FullFollow modes; per-frame brush interpolation via
  `CompositionTarget.Rendering`.
- **M6** — `SidecarLuxProvider` piggybacks Display Comfort luma reads on
  the head-tracking sidecar's frame-mean luma (avoids second webcam
  open). _(Decommissioned at v0.11 with the Display Comfort feature
  itself.)_
- **M8** — `SettingsContent` shared UserControl; settings tab in main
  window; restart button; pop-up card opens settings non-modally.

### Changed
- **Phase 3 (UI Consolidation Sprint preview)** — `SidecarHeadPoseReader`
  lifecycle hardened: `SemaphoreSlim` gate around `Stop/Start/Restart/Dispose`,
  `volatile Process? _process`, `ReferenceEquals` identity checks at every
  cross-generation write, two-phase identity check in `OnProcessExited`,
  Power Save dispatched off the UI thread via `Task.Run` with
  `Interlocked` epoch coalescing. UVHS audit confirmed 7 clean PS round
  trips with zero stale-state leakage.

## Earlier history

See `git tag` for the full sprint-seal history: v0.6 → v0.7 (calibration)
→ v0.8 (pop-up overlay) → v0.9 (habituation chatbot integration) → v0.10
(head-tracking).
