# Changelog

All notable changes to SenseEase are documented in this file.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning follows
the sprint-seal git tag pattern (`v0.<sprint>` and `v0.<sprint>.<hotfix>`).
The authoritative version reference is `git tag` — this file is the human-readable summary.

## [1.0.0 — Beta V1.0] — 2026-06-23

First Beta milestone. Rolls up everything below (the B0/B1a/B1b/B2 waves) plus the post-Beta-plan work:
the B5/B6 adaptation gate + bidirectional elasticity learning loop (default-OFF consent), the
dashboard-console dismantle to a headless host, the de-identified HTML health report, the popup-bar/chat +
edge-sidebar shell, the local on-device LLM chatbot on the Qwen3-4B model with the three-backend
(FastFlowLM / OpenVINO / Vulkan) autoselection router, and the late ship-hardening fixes (tray right-click
menu rebuilt as a real window, both dashboard-ghost paths closed, overlay-always-on / persistent-mask wiring).
Version stamped 1.0.0 in `Directory.Build.props` + `installer/SenseEase-v0.11.iss`. Authoritative reference: `git tag`.

## [Unreleased — Beta waves B0/B1a/B1b/B2]

Beta improvement-plan implementation. Detailed record: Improvement_plan_BETA.md (retired, see [PROJECT_HISTORY.md](docs/PROJECT_HISTORY.md)).
All 15 product-owner decisions (D-1 through D-15) were ratified 2026-06-11.

### Added
- **B0** — Habituation pause integrity: selector set moves no longer silently clear the habituation
  safety pause or `PausedAwaitingUserConfirm` state (CW-1/LOGIC-01).
- **B0** — Settings graded-exposure opt-in toggle reinstated (D-1).
- **B0** — Crisis-response panel on the pop-up card made fully legible (CW-5).
- **B0** — Test-suite isolation: `DataRootOverride` seam prevents tests from touching the developer's
  real `habituation_profile.json` / `user-preferences.json` (CW-9/TEST-01/03).
- **B0** — Packaging trust bundle: SHA-256 sidecar for the shipped installer (CW-6/PKG-01/02/03).
- **B1a** — Engine: shader replay persistence, D-8 flag-off seed (TYPICAL/L5) pinned with test,
  D-5 L5 stable-window protection, D-13 calibration-plumbing deletion, background persist queue.
- **B1a** — Sensing: `WgcSensingProvider.Start()` serialization, D-4 cursor-move OFI attenuation
  (0.25 weight multiplier, NE9), liveness contract restored.
- **B1a** — App lifecycle + PII hardening: hooks-first shutdown, `PathRedactor`, env-var API-key
  handling, manifest sweep.
- **B1a** — Sidecar resilience: camera-loss re-open, scheduler resync, face-loss grace period,
  health API (C4 unlock per D-11/SIDE-01/02/03).
- **B1a** — Storage: UTC clock unification across all timestamps, log-cap sentinel, atomic
  `behavior_log.jsonl` write.
- **B1a** — Installer lifecycle: `AppMutex` single-instance gate, silent-uninstall data guard,
  manifest scope fix (PKG-04/06/07).
- **B1a** — PII-safe support flow: `collect-logs.ps1`, handbook log-zip section, `SECURITY.md`
  scope update (SEC-02/03/PKG-09).
- **B1b** — Tray liveness honesty: Paused vs Faulted are visually distinguishable; localized
  status copy (UX-03/INTENT-05/UX-04).
- **B1b** — Notification card: full localization, keyboard navigation path, 44 px touch targets,
  12 px type floor (UX-05/UX-14/UX-10).
- **B1b** — Main shell: plain-language comfort hero, first-run card, Advanced telemetry expander,
  debris cleanup (UX-06/UX-13/INTENT-06/D-6/UX-18).
- **B1b** — Settings honesty: restart-required chip accuracy, complete "clear all data" path,
  head-tracking health state (UX-08/UX-09/FEAT-13/FEAT-08).
- **B1b** — Chat failure states: calm localized copy, no raw file paths, no raw exceptions (UX-07).
- **B1b** — Theme contrast truth + typography floor: all text meets AA contrast; body floor 14 px,
  caption floor 12 px (UX-11/UX-10/UX-17).
- **B1b** — `calibration_history.jsonl` migration delete + `LOG-PATHS.md` truth update
  (FEAT-13/DEAD-07/PKG-09).
- **B1b** — Test-gate hardening: isolation for HabituationTests, SidecarHealthTests, prefs tests
  (TEST-05/06/11).
- **B1b** — Overlay A1: one-clock motion model replacing two competing animation clocks
  (`BETA_FEATHERING_COHERENCE_PLAN.md` sA1; UVHS pending at commit time).
- **B2** — Doc archive sweep (DEAD-13): `docs/archive/` created; `docs/ARCHITECTURE.md`,
  `HANDOVER.md`, `Habituation plan.md`, `docs/M5_IMPLEMENTATION_PLAN.md` moved there with
  superseded-by banners; inbound references updated. CHANGELOG backfilled v0.11–v0.11.3.
- **B2** — D-12: `_persistentOverlayEnabled = false` Kirby override recorded in
  `docs/design/LOCKED_DIRECTION_ALPHA1.md` per INTENT-09.

### Changed
- **B0** — Chatbot honesty: profiles are auto-managed (D-3/CW-3); inert-knob operations now
  surface honest confirmations instead of silent no-ops (CW-2 interim/D-2).
- **B0** — `UM4` retrospective analysis wired to actually run at the correct window start (CW-4).
- **B0** — Stale-comment sweep: flag comments updated to reflect live feature states (DEAD-10);
  locked constants given literal-pin tests (TEST-10).
- **B1a** — `OverlayWindow` viewmodel: overlay-baseline tier persistence fixed; central dispose
  path; dispatcher relief for high-frequency state updates.
- **B1b** — Sidecar jump-filter unlatched (SIDE-01) so face re-acquisition after partial occlusion
  does not produce a spurious large-angle spike.

---

## [0.11.3] - 2026-06-09

Installer rebuild. No source changes; re-packages `0609` build artifacts
for the Pre-Alpha tester distribution channel.

### Changed
- Installer bumped to v0.11.3 (`0609 rebuild`); distributable updated.

---

## [0.11.2] - 2026-06-09

Alpha-test installer build. Bundles the UI-polish pass and the feathering
halo fix for the first external tester distribution.

### Added
- SHA-256 sidecar for the shipped installer (H27 trust chain).

### Changed
- Installer bumped to v0.11.2 (`alpha-test 0609 build`).
- Installer Run-key flag corrected; dead demo-video staging assets removed.

### Fixed
- **Hero-meter coherence** (`ComfortMeter` bound to `VimsTier`, not `DisplayStressPercent`);
  pop-up card and overlay-bar refinements; `FmsScore` display cleanup.
- **Feathering halo** (Phase 1 fix): asymmetric radius envelope for head-tracked FOV mask
  eliminates the visible halo artefact around the clear zone when head-tracking is active.
- **Autostart blank-dashboard fix**: main window no longer shows a blank dashboard
  on autostart due to a `SourceInitialized`-timing race.
- **Alpha UI/UX audit** pass: resolved 5 findings from the tester-readiness audit.

---

## [0.11.1] - 2026-06-06

Pre-Alpha packaging: portable head-tracker sidecar + Vulkan chatbot backend.

### Added
- Portable head-tracker sidecar bundled in installer (no Python install required by end user).
- Vulkan chatbot backend replaces the CUDA dll (drops the unused 148 MB CUDA dependency;
  `llama-server` now uses Vulkan for GPU inference where available, CPU otherwise).
- Inno Setup stage script updated for the new sidecar layout.

### Changed
- Installer version bumped to v0.11.1.

---

## [0.11] - 2026-05-31

UI Consolidation Sprint sealed. See git tag `v0.11` and the Pre-Alpha packaging
commits for the full arc.

### Added
- **P1/P1.5** — `SettingsContent` shared UserControl; 4th Settings tab in main console;
  non-modal settings from the pop-up card.
- **P2** — Power Save × head-tracking lifecycle hardening; sidecar correctly stops/resumes
  across Power Save cycles with zero stale-state leakage (UVHS-confirmed).
- **P3** — `WgcDiagLog` shared diagnostic-log utility: retry-on-IOException, monotonic
  sequence prefix, independent loss sink. Resolves WGC log-truncation on `Start()`.
- **P4** — Display Comfort feature decommissioned (depth 2): DDC/CI monitor mutation
  pipeline, ambient-light sensing, lux→comfort mapping, and `SidecarLuxProvider` removed.
  Rationale: unreliable across Windows DDC/CI driver variations; cleaner than opt-in dead code.
- **Phase 5** — `TrayIconService` self-subscribes to `UserPreferences.Changed`; tray state
  stays in sync with Settings without a manual refresh. Card per-Show state hygiene.
  Card live-sync on `StateChanged`. Dev hotkey table hidden (hotkeys non-functional).
- **Inno Setup installer** (v0.11): first packaged distributable for Pre-Alpha testing.

### Changed
- `SidecarHeadPoseReader` lifecycle hardened: `SemaphoreSlim` gate,
  `volatile Process? _process`, `ReferenceEquals` identity checks, two-phase identity check
  in `OnProcessExited`, Power Save dispatched via `Task.Run` + `Interlocked` epoch coalescing.
- `display-baseline.json` one-time idempotent migration-delete on startup (orphaned by
  Display Comfort decommission).
- OFI displays show raw sensor value (not the scaled internal value).
- M3 head-tracking invariant baseline pinned at v0.11 (flag-off → center (0.5, 0.5),
  brush radius 0.85, byte-equivalent to v0.9 baseline).

### Fixed
- **`ProfilePickerSelected` dead event** removed (was raising CS0067 warning in CI).
- Manual Driver/Passenger override no longer permanently suppresses auto-detection.
  The override is now one-shot; auto polling reasserts on the next threshold crossing.

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
