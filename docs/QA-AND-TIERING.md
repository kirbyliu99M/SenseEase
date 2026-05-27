# QA Methodology & Tier System

Reference for **how SenseEase ships changes** (pre-merge QA gating) and **how
the VIMS Tier system works end-to-end** (engine output → 3-band UI mapping).
Written after the overlay-bar-redesign sprint surfaced 4 categories of
"unit tests green but production failed" — the gating layers below are
the concrete answer to "why did the green suite miss that?"

---

## Part 1 · Pre-merge QA Gating

### 1.1 The four canonical failure modes (sprint-observed)

The overlay-bar-redesign sprint shipped 45 new unit tests, all green, and
still produced **five** production failures in user visual review:

| # | Failure | What the green test asserted | What production needed |
|---|---|---|---|
| 1 | Env var leak across PowerShell sessions | Process-scope mock | All three scopes (Process/User/Machine) |
| 2 | `RenderTargetBitmap` crashed `H.NotifyIcon` | Test asserted `IsType<RenderTargetBitmap>` | The library to accept the type |
| 3 | `BitmapImage` (no `UriSource`) crashed library | Test asserted `IsAssignableFrom<BitmapSource>` | Library's internal pixel-fetch path to find a URI |
| 4 | 6-pixel-tall bar click target | Tests for tier mapping + mute logic | A clickable bar |
| 5a | H.NotifyIcon silent `Shell_NotifyIcon` registration failure | Library internal state (`Visibility=Visible`, valid `Icon.Handle`) | OS-level shell registration confirmation |
| 5b | `dotnet run` vs published `.exe` behaviour divergence (preventive note) | `dotnet run` as the "real" launch path | OS treats dev-run and published exe identically for shell-integrating features |

Row 5a is this sprint's actual root cause for the tray-absent failure
(driven Patch 5's library swap to Hardcodet.NotifyIcon.Wpf). Row 5b is a
preventive note: this sprint's Amendment 3 verification *confirmed parity*
between `dotnet run` and the published `.exe` for the tray case, but the
divergence remains a real risk class for future OS-integrating features
(file associations, COM registration, custom URI schemes, AUMID handling).
Both rows together capture the gap; the test design that catches one
doesn't automatically catch the other.

Pattern: **tests asserted what the code produced, not what the consumer
needed**, and unit tests **cannot model library↔OS handshake state**.
When designing a new test, ask: "would this fail if [each failure mode]
regressed?" If no, the test is structural and not load-bearing.

### 1.2 Three test layers

**Layer 1 — Unit tests (xUnit, headless)**
- Pure logic only: `BandForTier(7)` returns `Amber`; `Mute(band)` blocks
  `ShouldAutoShow(band)` for 6 hours; `BuildDiagnosticText` includes the
  cleanup command for each non-null scope.
- Fast, deterministic, no WPF dispatcher, no real disk I/O.
- **Anti-pattern**: asserting concrete return types (`IsType<X>`) when
  the contract is "must be acceptable to library Y." Assert the contract,
  not the implementation. If the contract can't be expressed in headless
  xUnit, push to Layer 2 or 3.

**Layer 2 — Contract tests (xUnit, but exercising real third-party code)**
- Call the actual consumer library's public API with the produced value.
- If the consumer's API is internal (e.g. `H.NotifyIcon.ImageExtensions`),
  reflection is acceptable; mock-only contract tests do not count.
- Example deliverable for Habituation Phase 6: when `MistralChatbotService`
  consumes the snapshot DTO via prompt injection, the contract test
  must actually run the prompt template renderer, not just assert the
  DTO has the right fields.

**Layer 3 — Manual smoke (user, on their machine, before tag)**
- 5–7 item checklist defined per sprint.
- Cannot be automated today (STA-thread + visible-window + shell policy).
- Mandatory before `git tag`; hard stop at the end of every sprint.
- E2E test infrastructure (FlaUI / `WpfApplicationFactory` / screenshot
  diffing) is the queued forward commitment to compress this layer.

### 1.3 The sprint gating process (per-task)

```
┌─ Pre-check ──────────────────────────────────────────────────┐
│ 4-item technical inventory:                                  │
│   1. What's the current implementation type?                 │
│   2. Where does the trigger come from?                       │
│   3. What state already exists (file, service, …)?           │
│   4. What's the critical pre-check (the unknown unknown)?    │
└─ Hard stop: user signs off on inventory ────────────────────┘
                            ↓
┌─ Implementation ────────────────────────────────────────────┐
│ Single commit per task. Test added in same commit.           │
│ No batching across multiple tasks.                           │
└─ Hard stop where spec mandates (e.g. Task 1, Task 7) ───────┘
                            ↓
┌─ Visual review (user) ──────────────────────────────────────┐
│ User runs on their machine, walks checklist.                 │
│ Reports `approved` or specific failure mode.                 │
└─ Hard stop: no tag/merge without explicit approval ─────────┘
```

### 1.4 The fix-forward process (when something breaks post-ship)

This is the path used 3+ times during overlay-bar-redesign:

```
1. Diagnostic snapshot (NO code changes)
   • Read crash.log, wgc_diag.txt, source paths in question
   • Quote verbatim — no paraphrase, no speculation
   • Trace the failure to a single source line if possible

2. Root cause hypothesis surfaced to user
   • One sentence per hypothesis
   • Evidence per hypothesis (file:line or log line)
   • Fix outline as DIFF NOTES (not code)
   • Impact scope: which files, which mutex/lifecycle invariants

3. User signs off on root cause + fix shape

4. Verify-first probe (when uncertain about dependency / primitive)
   • Throwaway test file in tests/, NOT in production tree
   • Run it, three outcomes:
       A pass → proceed to step 5
       B missing dep → add it, re-probe
       C unexpected failure → re-evaluate (back to step 2)
   • Delete probe before the real-fix commit

5. Single-commit fix
   • Production code + test in same commit
   • Commit message documents WHY (incl. lifecycle invariants if any)
   • NO batching with other fixes

6. User verifies before next commit
   • If multiple fixes are queued, each gets its own user verification
   • "Connect three fixes and let user verify all at once" was the
     batched-fix mode that produced silent regressions; do NOT use it
```

### 1.5 Hard-stop rules

- After a task with a spec'd hard stop (Task 1 WGC validation, Task 7
  acceptance + docs) — wait for explicit user `approved` before next
  task starts
- After every fix-forward commit when sequential fixes are queued —
  wait for user to confirm the previous fix works before next commit
- After any user-facing change that affects visible UI — never assume
  green tests = working feature; user must confirm

### 1.6 Lessons-learned forward commitment

Each sprint's PR description includes a section enumerating:
1. Every "tests green but production failed" instance with its
   specific failure mode (use the table format from §1.1 above)
2. Concrete forward commitments — not "add FlaUI" but the specific
   contract tests that would have caught each failure
3. E2E test infrastructure prerequisites before the next sprint can open
   (CI vs local, STA-thread strategy, library-without-source approach)
4. **Known-failure regression suite**. Each row of the §1.1 table gets a
   single regression test that fails if that exact pattern reappears.
   These tests live in `tests/SenseEase.Tests/RegressionGuards/` and
   exist to prevent repeating *these exact* failure modes — not to catch
   new ones (that's what item 1's E2E infra is for). Examples for this
   sprint:
   - Gap #1: test that probes all 3 env var scopes before any assertion
     about validation mode
   - Gap #2/3: test that calls the production library's actual conversion
     function on every `IconForTier` output, not a structural shape
     assertion
   - Gap #5a: test (manual STA smoke acceptable until E2E lands) that
     observes OS-shell-state for the tray icon after launch
   - Gap #5b: test (or runbook step) comparing `dotnet run` vs published
     `.exe` tray behaviour

The commitment is **load-bearing**: the next sprint cannot dispatch
until the prerequisites are scoped.

---

## Part 2 · VIMS Tier System

### 2.1 The 10-tier discrete scale (engine source)

`VimsTier` is a 1–10 integer maintained by `HabituationEngine` per spec
§9. It is the single source of truth for "how stressed is the user
right now."

- **Producer**: [HabituationEngine.cs](../src/SenseEase.Core/Inference/HabituationEngine.cs)
  via `Step(pressure, maskActive, vimsTier, dtSeconds)`
- **Carrier**: [SenseState.VimsTier](../src/SenseEase.Core/Models/SenseState.cs#L23)
  (`int`, default 1)
- **ViewModel surface**: [MainViewModel.VimsTier](../src/SenseEase.App/ViewModels/MainViewModel.cs#L383-L384)
  — `Set(ref _vimsTier, value)` raises `PropertyChanged`
- **Event surface**: [MainViewModel.StateChanged](../src/SenseEase.App/ViewModels/MainViewModel.cs#L572)
  fires after every `UpdateFromState` — this is the channel UI surfaces
  subscribe to

**Hysteresis rules** (per spec §9; implemented in HabituationEngine):
- Tier transitions require sustained boundary-crossing — single ticks
  don't shift tier
- UP debounce more lenient than DOWN debounce (we're protective by
  default)
- Tier 1 is the resting state; tier 10 is "engage all protection"

### 2.2 The 3-band UI mapping (TierColorPalette)

For ambient UI surfaces (bar + tray + card title), the 10 tiers are
bucketed into 3 perceptual bands. **All UI consuming tier-color must
go through this table — no copy-paste mappings allowed elsewhere.**

| Tier range | Band  | Hue (RGB)        | Bar alpha | Tray icon alpha |
|------------|-------|------------------|-----------|-----------------|
| 1–5        | Grey  | (102, 102, 102)  | 0.15      | 1.0             |
| 6–7        | Amber | (230, 140, 40)   | 0.30      | 1.0             |
| 8–10       | Red   | (220, 50, 40)    | 0.45      | 1.0             |

**Source of truth**: [TierColorPalette.cs](../src/SenseEase.App/Views/TierColorPalette.cs)

| Surface | API | Returns |
|---|---|---|
| Bar background | `BrushForTier(int)` | `SolidColorBrush` (ambient-overlay alpha) |
| Tray icon | `IconForTier_Win(int)` | `System.Drawing.Icon` (full alpha, HICON-backed) |
| Band lookup (no rendering) | `BandForTier(int)` | `TierBand` enum |

**Defensive clamping**: tier ≤ 0 → Grey, tier ≥ 11 → Red. Engine should
only emit 1–10, but a future contract change shouldn't crash the UI.

### 2.3 User decisions recorded (sprint history)

| Date | Decision | Rationale |
|---|---|---|
| 2026-05-26 | 3 bands not 4 | Original spec had 4 (grey/grey-warm/amber/red); user simplified — perceptual distinction between grey and grey-warm was too subtle at typical bar sizes |
| 2026-05-26 | Bar full opacity 1.0, brush carries alpha | Lets `Toggle B = OFF` force tier=1 (grey) cleanly via `SetTier(1)` without touching Window.Opacity |
| 2026-05-26 | Tray icon full alpha (RGB shared with bar) | Tray icons must be visible against any Win11 taskbar background; low-alpha would disappear on light themes |
| 2026-05-26 | Auto-expand on band crossing, not within-band | Tier 1 → 2 is sub-perceptual; band Grey → Amber is the actual escalation worth notifying |
| 2026-05-26 | Mute is band-scoped, not tier-scoped | "Don't show this kind for 6h" — kind = band; muting Amber doesn't suppress Red |

### 2.4 Cross-document references

- **Tier behavior during calibration**: [docs/CALIBRATION.md §3](CALIBRATION.md) —
  `CalibrationProcessor` uses Q3 (intensity) + Q4 (vs baseline) + Q5 (risk
  factors) to derive the active `HabituationProfile`, which sets the
  effective trigger threshold and therefore controls how easily tier
  rises in subsequent sessions.
- **Tier persistence in profile snapshot**: [docs/HABITUATION-PROFILE.md §5](HABITUATION-PROFILE.md) —
  `current_vims_tier` is persisted in `habituation_profile.json` so a
  reader (chatbot, diagnostic tool) can see the user's last-known tier
  state without re-running inference.
- **3-band UI consumption**: [docs/POPUP-OVERLAY.md §4](POPUP-OVERLAY.md) —
  Full Toggle A × Toggle B × band × user-initiated truth matrix for
  when the card auto-shows vs requires a click.

---

## Part 3 · "How do I add a feature without breaking this?"

Quick recipes for future contributors.

### 3.1 Adding a new UI surface that displays tier

1. Subscribe to `MainViewModel.StateChanged` (NOT `PropertyChanged` on
   `VimsTier` — the event carries the full `SenseState` and is the
   convention every other surface uses)
2. Call `TierColorPalette.BandForTier(state.VimsTier)` or
   `BrushForTier`/`IconForTier_Win` — never duplicate the band lookup
3. If the surface is rendered onto the captured monitor, register its
   physical-pixel rect with `WgcSensingProvider.SetMonitorExcludeRect`
   on first render (mask invariant — see [docs/POPUP-OVERLAY.md §3](POPUP-OVERLAY.md))
4. Add a contract test: "given tier X, surface renders in band Y" —
   covers regressions if the mapping changes

### 3.2 Adding a new dependency (NuGet, library, OS API)

Before the PR description's `<PackageReference>` lands:
1. Write a probe test (see §1.4) — confirm the dependency resolves AND
   the specific primitive you'll use works on the target framework
2. Delete the probe; the real test that lands in the PR exercises the
   production-shape consumer code, not the probe
3. Commit message of the real change records WHY this library + version
   over alternatives (see [overlay-bar-redesign dd30d28](https://github.com/example) — H.NotifyIcon.Wpf vs Hardcodet vs WinForms.NotifyIcon rationale)

### 3.3 Editing engine-side tier logic

`HabituationEngine` is hot — every UI surface relies on its tier output
being stable + correctly hysteretic.
1. Read the existing tests under `tests/SenseEase.Tests/HabituationTests.cs`
2. Add a failing test for the new behavior FIRST
3. Implement the change
4. Confirm no existing test changed; if any did, that's a regression
   to call out explicitly in the PR description (with reason)
5. Manual smoke: run app in Demo Mode, watch the Dashboard's tier
   indicator across the demo video — verify the transitions you
   expected actually happen on screen, not just in the test fixture

---

## Part 4 · Open work / queued before next sprint

These are blocking Habituation Phase 6/7/8 from opening:

1. **End-to-end WPF integration test infrastructure** — scoped, not
   built. Candidates: FlaUI, `WpfApplicationFactory` pattern, screenshot
   diffing. Required mandatory test cases (sprint-blocking): app launch
   without crash, tray icon visible at shell level, bar visible at
   top-center, click bar → card opens, tier rises during Demo Mode →
   bar repaints. STA-thread strategy must be decided.
2. **Library-without-source contract test approach** — H.NotifyIcon
   is the precedent; future similar dependencies (Whisper.NET,
   LLamaSharp, etc.) need a stable pattern.
3. **Manual smoke runbook** — formalize the 5-item checklist used at
   Task 7 hard-stop into a markdown file the user can copy-paste
   into their terminal and tick off as they go.

---

*This document is the closest thing to a single-page methodology
summary. If you're reading it after compaction, the file lives at
`docs/QA-AND-TIERING.md` on the `overlay-bar-redesign` branch (and
`main` after the next merge).*

---

## Part 5 · Process-discipline observations from overlay-bar-redesign

What worked, kept for next sprint:

- **Diagnostic snapshot before fix** (no speculative fix-forward) —
  caught the env-var leak, the BitmapImage `UriSource` gap, and the
  library/shell desync
- **Verify-first probes** (System.Drawing.Common, Hardcodet) — prevented
  two false starts
- **Single-commit-per-fix + user verification between** — replaced the
  failed "batched fix-forward" mode that produced silent regressions
  early in the sprint
- **Watchdog timer** as a stand-in for E2E testing — when unit tests
  can't observe library/OS state, runtime probes can
- **Push-back culture** (lead ↔ coordinator). Amendments 1-3 to Path B
  came from the coordinator pushing back on the lead's draft; the lead
  refined and approved. Neither side rubber-stamps the other. This
  should be raised from a one-off observation to a **cultural
  principle** in the next sprint's onboarding doc.

What needs to change:

1. Tests like `IsType<X>` (pinning return-type implementation) need to
   be auto-flagged in PR review — they're structurally weaker than
   contract tests.
2. Library-without-source dependencies (H.NotifyIcon was one) need a
   smoke gate at PR time, not just at sprint-end visual review.
3. **Lead-level assertion discipline**. The lead (coordinator) made a
   false-positive call mid-sprint: asserted "UseNewBarOverlay default
   = false" based on partial data (user-preferences.json missing +
   tray absent) before requiring the agent to paste source. The
   agent's paste falsified the assertion, costing one user-data
   round-trip. **Lesson**: triangulation matrices apply to the
   coordinator too. Even when evidence "looks obvious," the lead must
   require source verification before strong assertions. The cost of
   one extra paste is always lower than the cost of misdirecting the
   agent. (This entry is intentionally self-implicating — omitting it
   would have produced a `What needs to change` list that looked
   asymmetric, as if only the agent ever erred.)
