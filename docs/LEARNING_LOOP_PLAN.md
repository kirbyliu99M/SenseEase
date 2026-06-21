# SenseEase — FMS Feedback + Learning-Loop Plan

**Created 2026-06-16.** Roadmap collection (not a single-PR scope). Each Part-A/B/C item is
independently shippable and individually gated (build + full test green, adversarial review,
UVHS for visible surfaces, sign-off before commit).

## Context

This work reworked SenseEase's **user feedback** around a 0-20 FMS (Fast Motion Sickness Scale)
self-report, then turned into an architecture investigation of how SenseEase **learns** and how to
make its core motion signal (**OFI**) more accurate. Two motivations:

1. **Feedback was naive.** The old dashboard had an always-on 5-star "comfort" widget with no
   context, no cadence, and no trend — low willingness to use, low data quality. Steered to an
   **event-driven, contextual, paced** model (Teams/Trend-Micro style), organized into the **Eye
   Care tab**, with data exportable as CSV for long-term analysis.
2. **The learning loop is built-but-disconnected.** The rich signals (OFI, hand-adjustments, chatbot
   analysis, and the new FMS reports) mostly **dead-end** — they don't feed adaptation. And OFI
   calibration ("M1") is a manual, incomplete, un-applied runbook on a single global placeholder.

Intended outcome: (a) land the FMS/check-in feature cleanly; (b) connect the signals into one closed
loop that produces **two outputs — ① the personalized overlay set, ② the per-app effectiveness
analysis — where ② feeds back into ①**, bucketed per app; (c) make OFI accurate and self-calibrating.

---

## Part 0 — Done (staged, uncommitted; build 0 errors / 1432 tests pass / 0 fail / 3 skip)

- **C — cleanup:** removed orphan `OnConsoleResetClicked` + the dead MainWindow `GridViewColumnHeader`
  style. (Locked `SenseDefaults` dead constants left quarantined — see A3.)
- **D — FMS redesign:** `SymptomEvent.Sources.Slider`; `ComfortRatingScale.BuildMeasuredFmsReport`
  (0-20, fail-loud); `MainViewModel.RecordFmsRating` / `GetRecentComfortSummary` / `GetRecentCheckIns`;
  capture UI 5★ → 0-20 slider → inline → **paced dynamic** inline selector (30-min cooldown + tier-rise
  re-warrant + live update + no-flicker latch); not-a-medical-device disclaimers; `SymptomEventCsvExporter`
  + Settings "Export raw data (CSV)".
- **E — overlay topmost:** dropped `ShowInTaskbar="False"` (rely on `WS_EX_TOOLWINDOW`).
- **Phase 1 — event-driven check-in:** `UserPreferences.ProactiveCheckInsConsent` tri-state (opt-in);
  `Services/CheckInGovernor` (8h cooldown + 24h dismiss-suppression); spike → consent/governor → context
  → survey; **big centered Teams-style overlay + dismiss-on-click scrim** (console-foreground-only;
  background spikes defer to next activation; fades gate on `ReducedMotion()`).
- **Phase 2 — Eye Care tab (護眼):** "我的感受紀錄 / My check-ins" card (weekly trend, aligned recent rows,
  inline paced self-report, CSV hint).
- **Data lifecycle:** "Clear all my data" deletes `symptom_log.jsonl` (+ regression test); clear-all dialog
  lists check-in history; uninstaller offers to `DelTree` the whole `%LOCALAPPDATA%\SenseEase` dir.

### Open decisions (not blocking the above)
- **Copy tone** — sweep parked, awaiting tone (clinical-neutral / warm-plain / minimal).
- **UVHS visual pass** — the big centered overlay + scrim is the one surface that can't be triggered on demand.
- **Locked `SenseDefaults` dead constants** (`OverlayRadiusAttack/ReleaseHalfLifeMs`,
  `OverlayBrushRadiusCornerMargin`) — quarantined; deletion pending sign-off.
- **Commit** the arc on a branch.
- **Head-tracker Release A/B experiment** (`dotnet run -c Release`) — deferred; head-tracker paused +
  byte-identical to the working 0611 installer.

---

## Part A — Close out the FMS / check-in arc (near-term)

- **A1. Copy-tone sweep** — one pass over every check-in / Eye-Care / disclaimer string (EN + 繁中) in
  `MainWindow.xaml(.cs)` + `SettingsContent.xaml`; must pass `BannedPhraseLintTests` (§6).
- **A2. UVHS visual pass** — optional temporary "force-show overlay" dev shortcut (strip before commit).
- **A3. Locked-constant decision** — delete the 3 dead `SenseDefaults` constants (sign-off) or keep quarantined.
- **A4. Commit** the arc on a branch (on the word).

---

## Part B — Connect the learning loop (core forward work)

**Model.** Inputs (per-app OFI · hand-adjustments · chatbot analysis · FMS) → learning → **① overlay set**
(`DeterministicSetSelector` → `_activeSet` → `SetCurve` → FOV/mask) + **② effectiveness analysis**
(`ClinicalExport` ON-vs-OFF FMS delta per category). Today ② is a dead-end; goal: **② feeds ①**, per app.

- **B1. FMS → selector (keystone).** Pre-built seam: `IntensityStatsBuilder.Build` already takes an optional
  `fmsSelector` and computes `UserIntensityStats.FmsAggregate`, but it's always 0 and the selector ignores
  it. Join measured FMS from `symptom_log.jsonl` into the durable 7-day slice; populate `FmsAggregate`; add
  an FMS term to `DeterministicSetSelector.Score`; tests + effectiveness-regression guard. → overlay set
  becomes **outcome-driven**. Re-tighten the "provisional until M1" selector constants.
  Files: `src/SenseEase.Core/AdaptiveFit/IntensityStatsBuilder.cs`, `DeterministicSetSelector.cs`,
  `UserIntensityStats.cs`; `src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs` (Select sites ~1025/1046,
  durable slice ~1005-1011).
- **B2. Persist the manual nudge as a preference.** The ±1 nudge (`MainViewModel.NudgeIntensityLevel` →
  `MockInferenceEngine.NotifyManualLevelNudge`) only **brakes** the selector (`W5ManualConflict` +
  `AdaptiveFitStateMachine.OnManualAdjust`) and is **NOT logged**. Append an `AdjustmentRecord` (per
  `ForegroundApp`) so the strongest direct signal becomes a learned target (selector prior + habituation
  caution via `WeightFor`/`SetPersonalization`), not just a transient veto.
- **B3. Per-app bucketing.** Bucket `IntensityStatsBuilder` + the effectiveness analysis by `ForegroundApp`
  → per-app `UserIntensityStats` → per-app overlay set (apply seam `ApplySetMove`/`SetLevel` exists). Behind D-16.
- **B4. App-aware OFI/pressure.** Three additive seams (behind D-16): add `ForegroundKey` to
  `OpticalFlowSample` (populated in `WgcSensingProvider`); thread optional `foregroundKey` into
  `IInferenceEngine.Update`; branch `PersonalPressureScale` by category (`category → scale`).
- **B5. Promote chatbot retrospective analysis** from prompt-only (`UserModelRetrospectiveAnalyzer` →
  longitudinal observations) to a structured adaptation input. Later.

---

## Part C — OFI accuracy + automated calibration

**Background.** OFI = MAD (mean per-pixel frame diff, 160×90) × `WgcMadNormFactor` (global `4.0` placeholder)
→ clamp → median → EMA → leaky integrator → pressure → tier/FOV/mask. **M1** (`MadCalibrationReducer` +
`NormFactorRecommender`) should set that factor from a real capture, but the capture is **partial (2 of 5
scenes), the factor is still 4.0, and the M1 gate was retired**. MAD is content-blind + app-agnostic.

- **C1. Finish M1 (foundational).** Complete the 5-scene capture (`SENSEEASE_MAD_CALIBRATION=1` → `baseline/`)
  on representative hardware; run `NormFactorRecommender`; apply the factor (manual edit to locked
  `SenseDefaults.WgcMadNormFactor`).
- **C2. Per-app/per-content norm-factor** instead of one global scalar (M1 already produces per-scene stats);
  same `WgcMadNormFactor` seam made category-keyed.
- **C3. Automate OFI calibration — recommended "degree ②" runtime self-calibration.** Adaptive gain control:
  accumulate **raw-MAD percentiles per app** (reuse `DailyBehaviorAccumulator` / `MadReducer`, tapped at the
  raw-MAD point the M1 capture already exposes); derive a **bounded per-user/per-app multiplier** mapping MAD
  onto the **same canonical 0-150 OFI scale**, layered as an *override* on the locked global anchor (parallel
  to `PersonalPressureScale`). Cold-start fallback + safety clamps + rate-limit; optionally validate via
  output ② (FMS effectiveness) = "degree ③". Degree ① interim: auto-collect + auto-recommend (app categories
  as scene labels), human applies.
- **C4. Upgrade the motion signal** (Farnebäck / learned flow vs MAD magnitude proxy) — Beta, higher effort.

---

## Part D — Two new signals: low-engagement + session-onset timing (2026-06-21)

Two signals Kirby asked to add to the loop (scope includes the health report). Verified against
actual code (`AdaptationEvidence.cs:57`, `AdaptationGate.cs:226-266`, `SymptomEvent.cs:76-104`,
`ClinicalExport.cs:189-234`, `MainViewModel.cs:613/1669/1852-1904`, `ChatbotViewModel.cs:221-227/452-455`).
**Headline finding:** idea #1's signal **already exists on disk** and is **already safety-partitioned**;
idea #2 is net-new capture. Nothing below contradicts the code; nothing is written until sign-off.

### D0 — The non-negotiable safety partition (three structural walls; preserve all three)

Idea #1's "no-click ⇒ no discomfort" must NEVER become a *measured ON-state report* that can RELAX
protection. The code already enforces this by construction; the plan's only job is to NOT break it:

- **Wall 1 — relax-intake filter:** `AdaptationEvidence.MeasuredOnFms` (`:57`) counts a row ONLY if
  `e.Measured && e.Kind == Kinds.FmsReport`. An `overlay_consent` row (`Measured=false, Value=0,
  Kind="overlay_consent"`) can never enter the ON-report count, the worst-case MAX, coverage, or the
  discomfort clock.
- **Wall 2 — no gate field:** `MeasuredState` (`AdaptationGate.cs:226-266`) has NO field for
  consent-non-click or time-to-first-signal. Even a *computed* metric has no path into `RelaxReject`
  unless someone adds a field there. **Mandate: add none.**
- **Wall 3 — "silence ≠ comfort" is positive:** relax requires `MeasuredOnReportCount ≥ 8/5`,
  `FmsCoverageOk`, `WorstRecentFmsOn < 6.0`, `ExposureAtOrAboveBaseline`. A non-click yields none.
- **Export wall (bonus):** `ClinicalExport.Build` (`:234`) + `BuildDailySeries` (`:189`) hard-filter
  `Kind ∈ {fms_report, fms_estimate}` (the B6 Stage-0 fix) — a consent-derived metric stays out of
  both FMS aggregates automatically.

**The exact partition rule:** the signal carries `Kind != fms_report` AND `Measured == false`. That
single combination makes it readable by the health report yet invisible to Wall 1 and the export
aggregates. **Forbidden mutation (never accept): writing it as `Kind=fms_report, Measured=true,
Value=0`** — the only mutation that would let a non-event relax protection.

### D1 — Idea #1: "overlay offered, not engaged" (REPORT-ONLY; signal already captured)

The no-click event is **already** captured durably today — no new write into any FMS path:
- Linger-timeout (the canonical non-response): `MainViewModel.cs:1865-1871` →
  `RecordOverlayConsentDecision(Decisions.Ignored)` → `SymptomEvent.OverlayConsentRow`
  (`SymptomEvent.cs:91`) → `symptom_log.jsonl` with `Measured=false, Value=0, Decision="ignored",
  ContextTier, InterventionState`.
- The "shown at L6" denominator is the paired `Decisions.Asked` row at `:1852`.
- `_consentInteracting` guards against a phantom "ignored" mid-decision. Explicit `Declined` (`:1904`)
  is separable by the `Decision` field. The two no-row retract paths (`CancelPendingConsent` :1919,
  `AlwaysAllowOverlay`-on :1775) are "prompt withdrawn by other state" — correctly NOT folded in.

So idea #1 is a **READ/derivation over rows already on disk**. The work is a pure Core aggregator +
a report-only metric: *count/rate of impactful-tier prompts (`Decision="asked"`) that went untaken in
the same session with no `fms_report` and no chatbot help-seek*. This is a **low-engagement /
mechanical** indicator, not a comfort claim.

**De-id constraints (hard):** date-bucket at the builder boundary (`yyyy-MM-dd`, D-17). **Naming
hazard:** `ReportClaimGuard` BANS the stem `toler` (+ `adapt`) — do NOT label it "tolerance"/
"tolerated". Use **"overlay offered, not engaged"** (count + rate) or a "tolerance" framing would be
stripped and fail `BannedPhraseLintTests`.

**Recommend:** feed idea #1 into **neither** `AdaptationEvidence` **nor** the gate (report-only),
honoring the measured-`fms_report`-ON-only relax invariant.

### D2 — Idea #2: "typical timing of first VIMS/DES discomfort signal" (NET-NEW capture)

**Session definition (fix before building):** session = **app-launch to process exit**, anchored on
`_sessionStartUtc = DateTime.UtcNow` (`MainViewModel.cs:1669`, set at VM construction) — the only
fixed wall-clock origin. (The overlay-consent session at `:1748` is the WRONG origin — it post-dates
the discomfort we want to time; the daily window is the durable aggregation home, not the origin.)

**Three first-touch sources (take the earliest per launch):**
- (a) **Consent card** — `MainViewModel.cs:1852` (asked) / `:1894` (accepted). Already in MainViewModel.
- (b) **FMS click** — `RecordFmsRating` (`:613`). Already in MainViewModel. Do NOT funnel timing
  through this as a new FMS write — it mints a measured `fms_report`.
- (c) **Chatbot help-seek** — net-new. Recommend **first SENT message** (a typed turn = real
  help-seek), not panel-open (prewarm is speculative). `SendAsync` has the empty-text guard at
  `ChatbotViewModel.cs:455`; fire a new optional `Action<string>? onHelpSeek` callback (added to the
  ctor `:221-227`, wired at `MainViewModel.cs:1054` beside `isOverlayConsented`, **kept nullable** so
  headless test ctors still compile) immediately after the empty-text guard.

**Aggregator seam** — one method on MainViewModel (owns a+b, constructs ChatbotVm at `:1054`):
```
private DateTime? _firstSignalThisSession;            // guard, beside _sessionStartUtc (:1669)
internal void NoteFirstDiscomfortSignal(string channel)  // "consent" | "fms" | "chat"
   → if _firstSignalThisSession is null:
        stamp UtcNow (durable) + (UtcNow - _sessionStartUtc) as the offset
```
The session-scoped null guard makes only the first win.

**Time sourcing (CW-12):** durable record = `DateTime.UtcNow`; offset = `(UtcNow - _sessionStartUtc)`
— an **elapsed DURATION**, not a sub-date timestamp, so D-17-safe. Flush durably **before exit** (the
anchor is in-memory, lost on crash). Do NOT use the monotonic `ConsentTickProvider` (reserved for
linger/session-end timers).

**Durable store (the anchor is in-memory and must persist):** the store must NOT be the measured-FMS
partition. Candidates: a NEW independent `session_onset_log.jsonl` (recommended — fully decoupled from
the hot report build), `fms_log.jsonl` (dev sink, also low-collision), or a `behavior_log`/
`DailyBehaviorAggregate` field (COLLISION-HOT — the report agent reads it; avoid in data-capture stage).

**Report wiring (deferred):** aggregate = **median minutes to first signal** (a duration; D-17-safe),
an additive numeric `ClinicalExport` DTO field. Defer to Stage B (coordinate with the report agent).

**Non-relax fence (optional, later):** the timing MAY safely inform check-in/prompt *cadence*
(`OverlayConsentGovernor` cooldowns) — that sets WHEN to ask, never the protection delta. It must
NEVER be assembled into `BuildMeasuredState` or appear as a `MeasuredState` field. Recommend
report-only first; cadence-driving later behind its own UVHS.

**✅ STAGE A DONE (2026-06-21) — data-capture + pure Core aggregators, build clean / full suite 1945-0-3.**
Idea #1: `ConsentEngagementBuilder` (+`ConsentEngagementSummary`) — pure report-only "offered, not engaged"
counts + Ignored/Asked rate, reads existing `overlay_consent` rows ONLY, zero capture change, feeds neither
gate nor FMS partition. Idea #2: `SessionFirstSignalBuilder` (+`SessionOnsetRecord`/`SessionFirstSignalSummary`,
median-minutes duration-only) + App `SessionOnsetLog` (new `session_onset_log.jsonl`, best-effort, mirrors
`FmsCheckInService`) + `AppConfig.SessionOnsetLogPath` + clear-data delete; `MainViewModel.NoteFirstDiscomfortSignal`
(first-of-session guard, writes the offset immediately so a crash can't lose it) wired on all three channels —
consent-ACCEPT (`GrantOverlayConsent`; decline excluded as ambiguous), FMS (`RecordFmsRating`), and a new
host-wired nullable `ChatbotViewModel.DiscomfortReportedCallback` fired on the `ReportDiscomfort` intent
classification (no ctor change → test ctors untouched). New tests: `ConsentEngagementTests`,
`SessionFirstSignalTests` (+ clear-data guard). **Stage B (report DTO/HTML) NOT started — deferred to dodge
the hot health-report agent.**

### D3 — Staged sequence (data-capture first, to dodge the HOT health-report agent)

The health report is HOT: P1-P4 built (commits `6b011ad`/`b2c4072`/`fb8a586`); the concurrent agent
owns `ClinicalExport.cs (BuildDailySeries)`, `ClinicalReportHtmlRenderer`, `ClinicalReportNarrator`,
`ReportClaimGuard`, `ClinicalReportService`. Editing those now races them.

- **Stage A — DATA-CAPTURE (land now, this branch):** idea #1 = NO new write (optionally add a
  distinct non-FMS `Kind`/`Decision` — touches only `SymptomEvent.cs`); idea #2 =
  `NoteFirstDiscomfortSignal` + `_firstSignalThisSession` + the optional `onHelpSeek` callback + the
  chosen durable store; a **PURE Core aggregator** (propose `ConsentEngagementMetrics` +
  `SessionFirstSignal` beside `ClinicalExport.cs`) reading existing rows → date-bucketed /
  duration-only outputs, unit-tested in isolation, decoupled from rendering.
- **Stage B — REPORT RENDER (deferred, coordinated):** additive `ClinicalExport` DTO fields + HTML.
  Touches the agent's hot files → do AFTER they land, or hand off to them. Agree field NAMES first.
- **Stage C — optional cadence use of idea #2 timing:** separate feature, own UVHS (Task #7 is the
  pending UVHS gate for this branch).

### D4 — Coordination flags

- `ClinicalExport.cs` / `ClinicalReport*.{Service,Narrator,HtmlRenderer}` / `ReportClaimGuard` owned by
  the concurrent agent (`6b011ad`). Do NOT edit in Stage A; coordinate additive DTO field NAMES before
  Stage B.
- `behavior_log.jsonl` / `DailyBehaviorAggregate` are read by the report's per-day series — prefer the
  independent `session_onset_log.jsonl` for idea #2.
- Confirm the concurrent agent does NOT already plan an onset/timing series (avoid double-authoring).
- `ReportClaimGuard` banned-stem list (`toler`/`adapt`/…) constrains BOTH labels — agree neutral copy.
- The `onHelpSeek` ctor param must stay optional/nullable so existing headless test ctors compile.

### D5 — Decisions LOCKED (Kirby, 2026-06-21)

1. **Idea #1 = 1A — report-only over existing rows.** Zero new capture; a pure Core aggregator reads
   the existing `Decisions.Ignored`/`Asked` `overlay_consent` rows. NO synthetic FMS=0 ever.
2. **Idea #1 scope = α** — literal "card shown + ignored" (existing rows only); no session non-engagement
   tracker for #1.
3. **Idea #1 corroboration = feeds the gate NOWHERE** — strictly a health-report metric.
4. **Idea #1 denominator = per impactful-tier prompt** (`Decision="asked"` at L6).
5. **Idea #2 store = NEW `session_onset_log.jsonl`** — independent of the hot report files.
6. **Idea #2 clause (c) = ReportDiscomfort-intent NLU turns ONLY** (not first-sent-message). ⇒ the
   chatbot hook moves from the raw `SendAsync` site to the NLU intent-classification site; all three
   channels (consent / FMS / chat) are then genuine discomfort signals. Depends on NLU classification.
7. **Idea #2 granularity = first-of-any per app-launch.**
8. **Idea #2 timing use = report-only now** → median-minutes-to-first-signal in the health report.
   **Stated purpose (Kirby): capture the discomfort *timing* and report it to the health report, and
   feed the learning loop IF it can be done safely.** ⇒ design the capture reusable for the learning
   loop's *cadence* path later (the fenced governor input, behind its own UVHS); it must NEVER become a
   `MeasuredState` field or touch the relax gate.

---

## Cross-cutting constraints (B & C)

- Keep the downstream OFI scale (0-150) + trigger (85) stable, or co-tune; auto-calibration emits the canonical scale.
- Safety: OFI drives protection. Never auto-edit the **locked** clinical default — use bounded per-user
  overrides with clamps + rate-limiting + cold-start fallback; validate via FMS effectiveness.
- Privacy: everything per-app behind the existing **D-16 opt-in consent**; on-device; category-only.
- Each learning/calibration change re-tightens the "provisional until M1" selector constants.
- UVHS-gate every visible change; build + full test green each step; nothing committed without sign-off.

---

## Suggested sequencing

1. **A1–A4** — land the FMS/check-in arc.
2. **B1** — FMS → selector (keystone).
3. **C1** — finish M1.
4. **B2 + B3 + B4** — manual-nudge persistence; per-app bucketing; app-aware OFI.
5. **C3** — automate OFI calibration (degree ②).
6. **B5 + C4** — chatbot-analysis promotion; motion-signal upgrade (later).

---

## Verification (each step)

- `dotnet build SenseEase.Windows.sln -c Debug` → 0 errors; `dotnet test tests/SenseEase.Tests` → green
  (currently 1432/0/3) with new unit tests per step.
- Adversarial diff review per change; fix confirmed findings.
- UVHS visual confirmation for any visible surface; live `--diag` for OFI/calibration behavior.
- App must be closed before a clean build (the running dev build locks `SenseEase.App.exe`).
