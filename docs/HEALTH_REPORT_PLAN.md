# Health Report Redesign — Plan (for hard review)

**Status:** DRAFT / not started. Item 8 of the 2026-06-21 settings-feedback batch.
**Goal:** turn the current de-identified **JSON** export into a *well-organized, human-readable*
**健康報告 / Health report** — key indicators + graphical reviews + an LLM-authored plain-language
narrative the user (and, optionally, their clinician) can actually read.

> **GATE for the dashboard-dismantle Stage 2 (2026-06-21):** this report is the agreed **re-home** for
> the Eye Care tab's data view (trend chart + check-in history + 7-day trend) — Eye Care is deleted only
> AFTER this lands (Kirby picked "health report first"). **Verified the re-home is sound:**
> (a) §5 indicators **2** (per-day FMS trend ← `symptom_log`) + **5** (OFI p90 trend + mask freq ←
> `behavior_log`) cover it; (b) the data is viable + persisted — the live chart's in-memory
> `OfiHistory`/`PressureHistory` are NOT needed (report uses `behavior_log`); (c) the collection paths
> SURVIVE deleting the Eye Care tab — `symptom_log` is written by `RecordFmsRating` from the **chat**
> spike check-in (`MainWindow.xaml.cs:990`) + the recorder, `behavior_log` by the UM1 daily collector;
> deleting Eye Care removes only VIEWS. ⚠️ **ONE non-report loss:** the **manual / un-prompted** FMS entry
> point lives only in Eye Care (`OnEyeCareInlineBandClicked` → `Triggers.Manual`, `:1059`). The report is
> read-only and does NOT re-home it → after Eye Care goes, ratings are spike-prompted (chat) only. Decide
> at deletion time: re-home a "log how I feel" into the popup chat, or drop un-prompted logging.
> **Tray at Eye Care deletion (Kirby):** keep the tray icon + single-click→popup; remove ONLY the
> double-click→Open-Dashboard reaction (the dashboard surface goes; the tray lives).

> This is a PLAN, not an implementation. Nothing here is built. Section 7 lists the decisions that
> must be settled before any code lands.

---

## 1. What exists today (baseline)

| Piece | Location |
|---|---|
| Trigger button `匯出健康報告… / Export health report…` | `SettingsContent.xaml:569` → `OnExportClinicalClicked` (`SettingsContent.xaml.cs:417`) |
| Save dialog (JSON only, `senseease-health-export-YYYYMMDD.json`, no fixed folder) | `SettingsContent.xaml.cs:419` |
| Export writer (atomic write, reads **only** `symptom_log.jsonl`) | `ClinicalExportService.WriteJson` (`src/SenseEase.Infrastructure/Storage/ClinicalExportService.cs:53`) |
| Pure aggregation (no I/O, unit-tested) | `ClinicalExportBuilder.Build` (`src/SenseEase.Core/Inference/ClinicalExport.cs:151`) |
| Not-a-medical-device disclaimer | `SettingsContent.xaml:580` |

Today's output is **pure numbers + category labels** (schema v2, snake_case): observation window,
measured-vs-estimated FMS aggregates (kept rigorously separate per D-17/18/19/21), ON-vs-OFF
contrast (overall + by tier + by category), per-category load, and exposure dose. **No prose, no
charts, no per-day series.** De-identification is enforced: no raw app names, no utterances, no
finer-than-date timestamps.

## 2. Data already on disk a richer report can draw on

All under `%LOCALAPPDATA%\SenseEase\` (canonical list: `docs/LOG-PATHS.md`).

| Store | Feeds |
|---|---|
| `symptom_log.jsonl` (**only source read today**) | FMS 0–20 self-reports + NL→FMS estimates; baseline→outcome, ON-vs-OFF deltas, per-category load, **a per-day FMS trend line (not yet surfaced)** |
| `behavior_log.jsonl` (one numeric row/day, no PII) | exposure-dose chart, daily usage, on-screen-motion (OFI p50/p90) trend, mask-activation frequency, habituation-offset slope |
| `habituation_profile.json` | current posture (profile RESILIENT/TYPICAL/SENSITIVE, level, offset, vestibular flag) **+ `longitudinal_observations_v2` = LLM-written monthly prose already on disk (narrative seed)** |
| `fms_log.jsonl` | richest per-rating context: `mode` (Driver/Passenger), raw `ofi`, tier, protection state |
| `survey_log.jsonl` | satisfaction score over time — **score only; `comment` free-text is PII, must be excluded** |
| `adjustments.jsonl` | a "what you've tuned" section |

## 3. LLM plumbing for the narrative

- **Service:** `LocalLlmChatbotService` — on-device bundled `llama-server.exe` (Qwen), HTTP to
  `127.0.0.1`, OpenAI-compatible. **No cloud call, ever.**
- **Narrator seam:** `IChatbotService.CompleteShortAsync(systemPrompt, userPrompt, ct)` — history-free,
  grammar-free, one-shot; already routes output through `ReplyComplianceScrubber`. ⚠️ `max_tokens=96`
  today (tuned for 1–2 sentence rest advice) — **too small for a multi-section report**; needs a
  larger-budget variant or per-section calls.
- **Bilingual pin:** `ChatbotUiLanguage.IsEnglish` — narrative language follows the live UI toggle.
- **Proven pattern:** `LongitudinalSummaryBuilder.BuildBlock(...)` already computes trend numbers
  deterministically and hands them to the LLM only to *phrase*. **The report must follow this: numbers
  are computed deterministically; the LLM only narrates — it never invents a figure.**

## 4. Hard constraints (non-negotiable)

1. **Voice / medical-overreach guardrail.** `BannedPhraseLintTests` (CI gate) bans
   therapy/rehab/neuroplasticity/desensitization/tolerance/recovery/"reduced your"/"less sick" wording
   in EN + 繁中. The §6 red line: **describe the app's behaviour, never the user's clinical
   improvement.** The lint scans *static literals* — **LLM output is non-deterministic, so a runtime
   banned-phrase filter + a deterministic fallback narrative are required** (see Q3).
2. **On-device / privacy.** Model is local. An *exported/shared* artifact inherits D-17
   de-identification (no app names, no comment text, no sub-date timestamps). An *in-app-only* view
   could be richer (Q1).
3. **Not a medical device.** Reuse the existing disclaimer verbatim (`SettingsContent.xaml:580`).
4. **Calm-by-design / software-render.** Glass windows force software rendering (`AllowsTransparency`);
   avoid effects/animations that flash. Prefer generating a file over a new in-app render surface.
5. **Numbers stay deterministic.** LLM phrases; the builder computes. No fabricated metrics.

## 5. Proposed report shape

**Key indicators** (all derivable from existing stores — no new capture needed):
1. Observation window + exposure dose (days, check-ins, session minutes).
2. Comfort trend: measured FMS baseline → recent, **as a per-day line** (new: per-event series).
3. Protection ON-vs-OFF contrast (overall + matched-by-tier) — already computed, just surfaced.
4. Per-app-category comfort load + per-category effectiveness.
5. On-screen-motion (OFI p90) trend + mask-activation frequency.
6. Current protection posture (profile, vestibular flag, habituation offset/level).
7. (Optional) Satisfaction signal — `survey_log` **score only**.

**Charts/graphics — recommended: self-contained HTML export.**
- Render the report as one self-contained **HTML file** (inline SVG charts + LLM prose) the user opens
  in a browser and can print to PDF. Cleanest path to "a report you can look at / hand to a clinician,"
  sidesteps WPF print/pagination, keeps everything on-device, and **touches none of the glass-window
  software-render hazard.**
- *Alternative — native WPF panel:* the app already draws a two-series chart (`Polyline` on `Canvas`,
  `MainWindow.xaml.cs:2382`), so an in-app view needs no charting library. ⚠️ that live chart hardcodes
  `#024AD8` (`MainWindow.xaml.cs:2403`) — **the gamer-neon indigo the frontend-design audit flagged as
  violating the calm CARDINAL RULE; the report must use the calm palette, not copy this.**
- *PDF:* no PDF lib present; HTML→print-to-PDF likely covers the need (Q2).

**LLM-authored sections** (phrasing deterministic numbers): a plain-language opening summary; a
one-line neutral gloss per indicator; a "what you've tuned" note. Pin language to
`ChatbotUiLanguage.IsEnglish`; run **every** produced string through `ReplyComplianceScrubber` **and**
the runtime banned-phrase filter before display; deterministic fallback when the model is unavailable.

## 6. Rough build phases (once Section 7 is settled)

- **P1 — Deterministic data layer.** Extend the builder with a **de-identified per-day series** method
  (date-bucketed) + read `behavior_log` / `habituation_profile` (confirm privacy posture per source).
  Pure, unit-tested. *No UI yet.*
- **P2 — Renderer.** HTML template + inline-SVG chart helpers (calm palette), numbers only.
- **P3 — LLM narrative.** Larger-budget completion method (or per-section), runtime compliance filter,
  deterministic fallback. Golden-ish tests on the deterministic path.
- **P4 — Wire-up.** Repoint `OnExportClinicalClicked` (or add a second button) to produce the report;
  decide replace-vs-augment the JSON; success/failure toast as today.
- **P5 — UVHS + lint pass.** Live visual hard-stop on the rendered report; confirm BannedPhraseLint
  green; confirm no `#024AD8`/effects regressions.

## 7. Open questions for the hard review (decide BEFORE building)

1. **One artifact or two?** Rich *in-app* view (raw on-device data allowed) vs *exported* file (must
   stay D-17 de-identified). Does the graphical report **replace**, **augment**, or sit **beside**
   today's JSON? (`FULL_SETTINGS_REDESIGN_PLAN.md:44` currently says KEEP the JSON.)
2. **Output format:** HTML (recommended) / PDF (new dependency) / in-app WPF only? Fixed save folder?
3. **Runtime LLM copy vs the static BannedPhraseLint gate:** how strict is the runtime filter, and what
   is the deterministic fallback when the model is down or produces non-compliant text?
4. **`CompleteShortAsync` budget:** raise `max_tokens`, or call per-section?
5. **Source consolidation:** drawing on `behavior_log` + `habituation_profile` means new store reads in
   the (currently symptom-only) export service — confirm privacy posture for each; exclude `survey_log`
   comment text.
6. **Per-day series:** new de-identified builder method (date-bucketed) for the trend charts.
7. **Chart palette:** must use the calm signature, **not** the flagged `#024AD8` neon.

---

## 8. Settled resolutions — hard review 2026-06-21 (adversarially verified, wf w9azbep9k)

Kirby decided: **HTML report REPLACES the JSON** (sole artifact → must stay D-17 de-identified +
clinician-shareable). The review resolved Q3–Q7; the adversarial pass then **refuted `buildReady`** and
found 4 holes, all in the *new* surfaces. Resolutions below already fold in those fixes.

**Q4 (LLM budget):** per-section calls, each with its own bounded `max_tokens` + its own deterministic
fallback (failure isolation). Add overload `CompleteShortAsync(system, user, int maxTokens, ct)`
defaulting to 96 (preserves the existing `ChatbotViewModel` call site).

**Q5 + de-identification (the privacy wall) — the builder enforces:**
- **Category-only app:** every `ForegroundKey`/`app_context` passes through `ExtractCategory`
  (`ClinicalExport.cs:307`) before reaching ANY field, chart axis, legend, or tooltip. ⚠️**[refute 1B]**
  the *new* per-day builder MUST key on `ExtractCategory` (don't group by raw `ForegroundKey`); make
  `ExtractCategory` the only public category accessor + a unit test asserting **no exported field /
  series-key / legend equals a raw `category/app` string** (the "verified" claim rested on unwritten code).
- **Date-bucket:** every timestamp → `yyyy-MM-dd` at the builder boundary (charts render bucketed dates).
- **No-free-text (HARD-exclude, no sanitize-and-include):** `survey_log.comment`,
  habituation `CalibrationResponses`, `longitudinal_observations_v2.text`, **AND ⚠️[refute 1A] the legacy
  flat `longitudinal_observations` list** (`HabituationProfileSnapshot.cs:161` — still populated on old
  on-disk profiles). The narrative is RE-NARRATED from numbers; stored LLM prose is never copied.
- **Source-allowlist:** builder reads an explicit field allowlist per source; renderer sees only the
  typed de-identified DTO, never a raw row / `JsonSerializer.Serialize(wholeObject)`.
- **Prompt-isolation:** the LLM prompt carries ONLY the numeric DTO — never raw rows / ForegroundKey /
  observation text. ⚠️**[refute 1C]** the DTO carries **raw numbers only** — do NOT reuse
  `LongitudinalSummaryBuilder.BuildBlock`'s pre-judged strings (`improving`/`falling`/`tolerance_ceiling`,
  `LongitudinalSummaryBuilder.cs:44,62,79`); relabel strips EVERY directional adjective + `tolerance`.
- **Survey score-only** (`ComputeCsat`/`ReadLoggedScores`); **don't read `fms_log`** (dev sink);
  **`adjustments.jsonl` excluded** until its row schema is audited free of app names + free text.

**Q3 (LLM medical-overreach guard — the second, independent wall):** new pure-Core
`ReportClaimGuard.ContainsBannedClaim(prose) → bool`; vocabulary single-sourced into a shared Core
constant referenced by BOTH `BannedPhraseLintTests` and the runtime guard (anti-drift). Pipeline:
`CompleteShortAsync` → `ReplyComplianceScrubber.Scrub` (kept; it is NOT a medical filter) →
`ReportClaimGuard`; any hit ⇒ reject the WHOLE section ⇒ emit that section's deterministic fallback.
Model-off (`IsModelLoaded==false`) / call-null / output-rejected all converge on the per-section
templates (report always renders full); the templates themselves pass the guard in a golden test.
⚠️**[refute 2A/2B/2C] the guard vocabulary MUST EXCEED the lint list** (the lint was for static copy, not
adversarial LLM prose): absorb the R6 bodily-state stems (`Scrub`'s R6 is OFF at `intent=""`,
`ReplyComplianceScrubber.cs:119`) → add EN stems `improve`, `feel better`, `toler` (so `tolerated`
catches), `adapt`, `sensit`, `resilien`, `recover` (bare), `getting used to`; expand ZH to
`適應/改善/進步/習慣了/減輕/減少` (the literal `Contains` set is too shallow); **run BOTH language lists
on EVERY section regardless of the UI pin** (the EN↔ZH pin is a known-unreliable failure). ⚠️**[refute
arch]** `ContainsBannedClaim` stays pure Core; the localized fallback *templates* live in the
renderer (App/Infra) taking a `bool english` — Core stays test-pure.

**Q6 (per-day series builder, P1):** new pure model `DailySeriesPoint {Date 'yyyy-MM-dd';
double? MeasuredFmsMean; int? MeasuredFmsCount; int? OfiP90; int? MaskActivations}` + pure
`ClinicalExportBuilder.BuildDailySeries(symptomEvents, behaviorDays)` beside `Build`: measured-only
(keeps D-18/19), group-by-date, outer-join on the date key, `null` where a source lacks a day (honest
gap, never a fabricated 0). Additive; schema stays v2. Golden-tested with two in-memory lists.

**Q7 (chart palette):** NOT `#024AD8`/`#7B8DB0`/`#E54747` (the flagged live-chart strokes). Inline SVG
can't use `DynamicResource` → resolve calm LIGHT-theme tokens to concrete hex at render: FMS line =
`#4B6EF5` (Accent), OFI p90 = `#2F7D6E` (ComfortSteady), mask freq = `#9A6B22` (ComfortRising),
axis/grid = TextSecondary low-opacity, headings = `#1F5249`. Pin the LIGHT palette (printed/clinician).

**Manual check-in (at Eye Care deletion) — DECIDED: DROP (Kirby 2026-06-21).** No re-home. When the
Eye Care tab is deleted, `OnEyeCareInlineBandClicked` (`Triggers.Manual`) simply goes; FMS ratings are
**spike-prompted only** (in chat). The report's comfort trend therefore shows prompted ratings only —
acceptable. (No un-prompted entry point ships.)

**Wire-up (P4) — DECIDED: KEEP the JSON beside the HTML (Kirby 2026-06-21).** The HTML **augments**, does
not replace, today's export. Both are de-identified artifacts (the JSON already is; the HTML must be).
Add an HTML export path ALONGSIDE the existing JSON (a second button or a format choice on
`OnExportClinicalClicked`); do NOT retire `WriteJson`. Match on the method body / `Filter` string,
**not line numbers** — `SettingsContent.xaml.cs` is moving under the concurrent #4 settings-閃退 fix;
land the wire-up AFTER that commits to a clean tree.

### Build gates (must be true before P1 ships)
1. No-free-text rule names **both** observation lists + calibration_responses (refute 1A).
2. The per-day builder keys on `ExtractCategory`; a test asserts no exported field = raw `category/app` (refute 1B).
3. The report DTO is **raw numbers only** — no `BuildBlock` judgement strings (refute 1C).
4. `ReportClaimGuard` vocab exceeds the lint (R6 stems + EN stem-level + ZH expansion + both-lists-always) (refute 2A/2B/2C).

### Decisions — SIGNED OFF (Kirby 2026-06-21)
- ✅ **Vocabulary expansion APPROVED:** back-port `recover`(bare), `toler`, `adapt`, `sensit`, `resilien`,
  `getting used to`, R6 stems (`improve`/`feel better`), ZH `適應/改善/進步/習慣了/減輕` into the
  **shared/locked** banned vocabulary (referenced by both `BannedPhraseLintTests` and `ReportClaimGuard`).
  A P3 task; keep the two single-sourced.
- ✅ **Manual check-in: DROP** (no re-home — see above).
- ✅ **JSON: KEEP beside HTML** (HTML augments, not replaces — see Wire-up above).
- ⏳ **Audit `adjustments.jsonl`** schema before the "what you've tuned" section may read it (build-time
  gate, done during P1/P3, not a sign-off).

### Build order (P1 first — §7-independent, no conflict with the in-flight settings fix)
**P1** (start now): pure `DailySeriesPoint` model + `ClinicalExportBuilder.BuildDailySeries` (measured-only,
date-bucketed, outer-join, null-gaps) keyed on `ExtractCategory`; golden tests + the "no field = raw
`category/app`" assertion (build-gate 2). No UI, no I/O in the pure method. → P2 renderer → P3
narrative+guard (vocab expansion) → P4 wire-up (add HTML beside JSON, after the #4 fix lands) → P5 UVHS.

### Status — BUILT 2026-06-21 (P1 `6b011ad`, P2–P4 `b2c4072`)
- **P1** ✅ `ClinicalExportBuilder.BuildDailySeries` + `DailySeriesPoint` + 5 golden tests.
- **P2** ✅ `ClinicalReportHtmlRenderer` (pure HTML + inline SVG, calm LIGHT palette pinned to concrete hex,
  per-day line breaks at null gaps, all dynamic content HTML-escaped). Tests cover palette/escaping/both-langs/placeholders.
- **P3** ✅ `ReportClaimGuard` (pure Core, single-source banned vocab, stem-level, both-langs-always) +
  `ClinicalReportNarrator` (per-section, two-wall: scrub → guard → language + chat-artifact gate → deterministic
  fallback) + `ReportFallbacks` (templates pass the guard, golden). Q4 `CompleteShortAsync(…, maxTokens, …)` added.
  `BannedPhraseLintTests` now asserts guard ⊇ lint (build-gate 4); `ReportClaimGuard.cs` is the vocab-definition
  file so it is fully exempt from the lint scan (its own tests + the anti-drift test cover it).
- **P4** ✅ `ClinicalReportService` composes the stores + narrator + renderer and writes the HTML atomically;
  "Export health report" now writes the HTML report **and** the JSON beside it (JSON KEPT). Posture reads
  allowlisted numeric/coarse-enum fields only (build-gate 1). Wired via `GetChatbotService` (reuses the loaded model).
- **P5 UVHS** — markup verified line-by-line + unit-tested; representative EN/ZH samples generated to the Desktop
  for Kirby's own browser eyeball (the report is a standalone HTML artifact, not a claude.ai-native widget).
- **Unblocks** the dashboard-dismantle **Stage 2** (Eye Care deletion): the agreed re-home now exists.

---

*Cited files: `SettingsContent.xaml(.cs)`, `ClinicalExportService.cs`, `ClinicalExport.cs`,
`{SymptomEvent,DailyBehaviorAggregate,HabituationProfileSnapshot}.cs`, `FmsCheckInService.cs`,
`LongitudinalSummaryBuilder.cs`, `LocalLlmChatbotService.cs`, `MainWindow.xaml.cs` (chart),
`BannedPhraseLintTests.cs`, `docs/LOG-PATHS.md`, `docs/FULL_SETTINGS_REDESIGN_PLAN.md`.*
