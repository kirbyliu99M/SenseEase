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

## 9. Revision 2026-06-21 (post-review) — option (b) narration + safety hardening

A 15-agent review of the built report (how-generated / wording-provenance / improvement backlog) drove these
changes. **Kirby picked option (b)** for the LLM scope:

- **(b) ONE model-phrased paragraph.** `ClinicalReportNarrator.BuildAsync` now narrates ONLY the opening
  **Summary**; `ComfortTrend` / `Protection` / `Exposure` come straight from `ReportFallbacks` (no model call).
  The per-section prompts forbade exactly what an LLM is good at, so they reproduced the templates at the cost
  of 4× contention + an overreach surface. The dead `ComfortPrompt`/`ProtectionPrompt`/`ExposurePrompt` (+ the
  orphaned `Fmt`) were removed; `StrengthWord` stays (used by the renderer + fallbacks).
- **Number-lock (R1).** New gate in `SectionAsync`: every integer the model writes must be one the prompt
  handed it (`{DaysObserved, Measured.Count, Estimated.Count}`); any other digit rejects the section to its
  template. The Summary prompt is now counts-only (no calendar dates — they carry un-allowed digits; the
  header already prints the window). The LLM phrases, it never owns a figure.
- **Guard EN↔ZH symmetry + word-boundary (R2/R7).** `ReportClaimGuard` adds `BannedClaimWordsEn` (word-boundary
  matching) so the EN paraphrase gaps the ZH list already had (progress / better / easier / stronger /
  resilien / improv / adapt / sensiti) are caught, while `adapter`/`adaptive` no longer false-fire; ZH gains
  `好轉` / `變好`. Still a strict superset of the static lint (build-gate 4 green).
- **Measured-partition tightening (R5).** `ClinicalExportBuilder.Build` now partitions on **kind AND flag**
  (`Measured && Kind==FmsReport` / `!Measured && Kind==FmsEstimate`) — a no-op today, defensive tomorrow.

> **⚠️ Learning-loop integration constraint (for the branch adding "no-consent ⇒ FMS=0" + session-onset
> timing).** The inferred "no-engagement ⇒ comfort fine" signal MUST be recorded **`Measured=false`** — ideally
> a **new `SymptomEvent.Kind`** (e.g. `comfort_inferred`), NEVER `fms_report`/`Measured=true`. The clinical
> export's FMS-kind hard-filter (`ClinicalExport.cs:234`) then auto-excludes a new kind, and the R5 partition
> tightening backstops a mis-flagged `fms_estimate`. Writing inferred 0s as measured would corrupt the report's
> measured comfort trend + ON/OFF contrast and violate the D-18/19 measured-vs-estimated red line. Any
> session-onset *report indicator* is the learning-loop branch's lane (builder + renderer), not this revision's.

Tests: `ClinicalReportNarratorTests` (Summary-only routing + number-lock kept/rejected), `ReportClaimGuardTests`
(paraphrase claims caught, `adapter`/`adaptive` not). Full suite green (1945 passed; UI smoke flakes excluded).

---

## 10. Visual design pass 2026-06-21 (`/ui-ux-pro-max` + `/frontend-design`)

`ClinicalReportHtmlRenderer` was given a deliberate visual identity (was: white page + identical grey rounded
cards + system font + first/last-date-only charts). **Do not revert to the grey-card template.** Changes:

- **Identity / signature:** deep-teal mark + uppercase letter-spaced eyebrow ("SenseEase · On-device comfort
  summary") + larger H1; each section heading carries a 3px teal tick; airier layout — grey cards dropped for
  whitespace + hairline dividers. The one boxed anchor is the new band.
- **At-a-glance band (the hero):** 2–4 stat tiles (protection on/off mean, check-ins, triggers) lead the
  report, with a standing **guardrail caveat** ("descriptive summaries … not matched comparisons or a medical
  assessment; lower = calmer"). Tiles render only when their source has data; band omitted on an empty export.
- **Chart legibility:** y-axis unit tag (FMS / p90 / count), a direction note in the caption ("0–20, lower =
  calmer"; motion "fixed 0–100"), 5 date ticks + faint vgrid (was 2), and **value labels on every bar**.
- **Print/PDF:** `print-color-adjust:exact`, `@page{margin:14mm}`, full-width + `break-inside:avoid`.
- **Hard constraint kept:** system-font stack ONLY (the report is self-contained / never fetches a web font);
  the typographic "signature" comes from scale + weight + eyebrow + tabular figures, not a custom face. Calm
  palette unchanged; `#024AD8`/`#7B8DB0`/`#E54747` still absent (test-enforced).

`ClinicalReportHtmlRendererTests` green (palette/escaping/both-langs/empty-state preserved). Fresh EN/ZH
samples regenerated to the Desktop. STILL OPEN (presentation): bar vs line share a sparse date axis but bars
are slot-centred while the line is edge-spaced — minor #12 misalignment, not fixed this pass.

---

## 11. App-alignment + onset section 2026-06-21 (synthesis with the learning-loop "first discomfort signal" brief)

**Visual — aligned to the App, less "AI-toned", mechanical/technical.** The report was an independent teal
artifact; it now reads as the App's calm LIGHT sibling. `ClinicalReportHtmlRenderer`:
- **Palette → App family:** identity is now **navy ink `#0E1E47` + royal-blue accent `#4B6EF5`** (the calm
  sibling of the App's `#024AD8`) + faint light-blue band `#EEF2FC` (echoes the popup glass). Teal identity
  retired. Series unchanged (`#4B6EF5`/`#2F7D6E`/`#9A6B22` = calm siblings of the App accent/steady/rising);
  `#024AD8`/`#7B8DB0`/`#E54747` still absent (test-enforced).
- **Type → the App's own system:** prose in `Segoe UI Variable Text, Segoe UI` (= `Font.UI`), and **every
  number + technical label in `Consolas, Cascadia Mono`** (the App uses exactly this pairing for its
  readouts — NotificationCard, MarkdownInlines, SettingsContent). The mono-on-data is the "mechanical /
  instrument" feel; SVG chart text is mono too. System fonts only (self-contained, no web-font fetch).

**Onset section — synthesised the 7-agent brief (decisions A–F all per their recommendation).** A deterministic
**"Time to first discomfort signal"** `<section>` after Exposure: a grid table (overall + per-channel median
minutes, channels mapped to neutral labels Overlay/Check-in/Chat) + Idea #1's "overlay offered, not engaged"
line. Label verified clean against the live `ReportClaimGuard` (the natural "tolerance/adapted" framing is
banned). Deterministic ⇒ bypasses the number-lock. Renders the "Not enough data yet" placeholder / omits the
section on no data.
- **DTO:** two `[JsonIgnore]` nullable fields on `ClinicalExport` — `OnsetTiming` (`SessionFirstSignalSummary`)
  + `ConsentEngagement` (`ConsentEngagementSummary`). `[JsonIgnore]` because the JSON `WriteJson` path never
  composes them (no onset reader) — emitting null keys would mislead. (The JSON substring test still passes.)

**Coordination — INVERTED the brief's order (cleaner, no double-edit).** Since this agent owns + just
redesigned the renderer + DTO, it took the **renderer section + DTO fields + tests**; the onset agent keeps the
Core builders (`SessionFirstSignalBuilder`/`ConsentEngagementBuilder` — already shipped + tested) and the
**uncontended wire** ONLY: `SettingsContent.OnExportClinicalClicked` passes `new SessionOnsetLog()`;
`ClinicalReportService` gains `Func<IReadOnlyList<SessionOnsetRecord>>? onsetReader` and composes
`export.OnsetTiming = SessionFirstSignalBuilder.Build(...)` + `export.ConsentEngagement = ConsentEngagementBuilder.Build(events)`
in `WriteHtmlAsync`. ⚠️ **Onset agent must NOT re-add the DTO fields or a renderer section** (this agent did) —
field names/types agreed: `OnsetTiming`/`ConsentEngagement`. Until the wire lands the section simply shows the
placeholder (honest gap). All `ClinicalReportHtmlRendererTests`/`ClinicalExportTests` green.

---

## 12. Backlog batch 2026-06-21 (Kirby: A DO it · B#6/#9/#12/#16 · C all go) — full suite 1955✓

- **A onset wire — DONE end-to-end (Kirby took the wire too).** `SettingsContent` now injects
  `onsetReader: () => new SessionOnsetLog().ReadAll()` + a score-only `satisfactionReader`
  (`NpsService.ComputeCsat`); the service composes `OnsetTiming`/`ConsentEngagement`/`Satisfaction`. The
  capture side (their `SessionOnsetLog` writes + first-of-session guard) was already shipped → the section is
  now LIVE, not dormant. ⚠️ **Onset agent: the report wire is DONE — do not also add it; your remaining scope
  is only the capture you already built.**
- **B#9 "JSON = data recording, HTML = export" — wired cleanly.** `ClinicalReportService.WriteAsync(htmlPath,
  jsonPath, …)` composes the dataset ONCE and writes BOTH artifacts from it (JSON via the new
  `ClinicalExportService.Serialize`). The JSON now carries the FULL record (aggregates + `daily_series` +
  onset + consent + satisfaction) — the #9 daily-series gap is closed. `[JsonIgnore]` removed. The old partial
  `WriteJson` build-path is retired from the export flow (the separate second-write call is gone).
- **B#6** per-category effectiveness table (`OnVsOffByCategory`, was computed-never-rendered).
- **B#12** bar chart shares the line's x-scale (spikes line up; date ticks align).
- **B#16** EN plural helper in `ReportFallbacks` + renderer — no more "day(s)"/"check-in(s)".
- **C#17** measured-vs-estimated counts line · **C#19** missing token already unified to "—" (verified) ·
  **C#20** satisfaction CSAT tile (score-only, comment never read) · **C#21** export-range selector (All / last
  90 / last 30; `WriteAsync` `sinceUtc` filter) · **C#24** ZH category labels (影片/遊戲/…) + `pp`→百分點 ·
  **C#11** export tooltip + description rewritten (HTML report + JSON record, not "JSON file").
- **Tests:** new `ClinicalReportServiceTests` (#10 — both-artifact write, posture map + throw-degrade, atomic
  replace, window filter) + renderer scaling tests (#23 — OFI>assumed-max, single-day). Full suite **1955✓**.
- **Touched the shared `SettingsContent.xaml(.cs)`** (wire + tooltip + range combo) per Kirby's "DO it" —
  formerly the onset agent's lane; now done here. ⚠️ the range `ComboBox` is unstyled WPF default (functional;
  a glass-theme style is a follow-up). **Still open:** #4 cross-file write isn't transactional (HTML can
  outlive a failed JSON write) — out of this batch. NOT committed.

---

## 13. Wording / tone pass 2026-06-21 (dev copy feedback) — full suite green

Warmed the copy per dev feedback — **second-person, plain-language, friendlier labels** — while holding the
§6 medical-overreach line. **The dev examples that claimed clinical improvement were NOT adopted** (they would
fail `BannedPhraseLintTests` + the runtime `ReportClaimGuard` + the golden template test):
- ❌ "不適感正在下降 / Are your eyes feeling better? Good news—they are! / it definitely helps" → claims the
  user improved (`better`, directional "dropped"). Kept the **factual** framing: present baseline vs recent
  numbers, let the reader interpret; protection section states the two averages + the standing not-matched caveat.
- ✅ **CORRECTION (Kirby 2026-06-21): adaptation term APPROVED for this label.** The posture metric IS the
  `HabituationLevel`, so the real term is **EN "Adaptation progress" / ZH "視覺適應進度"** (Kirby: the perfect
  ZH term) — "relaxation / 放寬" was an invented euphemism we don't use; do NOT revert it. Safe because it's a
  FIXED static label: the runtime `ReportClaimGuard` (adapt/適應 still banned) only scans **LLM-generated
  prose**, and the static `BannedPhraseLintTests` list does not contain adapt/適應. The guard stays strict so
  the *model* can never claim the *user* adapted; the accurate term lives only in this one reviewed label.
- ❌ ZH "減輕視覺負擔" in the disclaimer → `減輕` is banned. Used "協助視覺舒適" instead.

**Print / Save-as-PDF button (2026-06-21):** a top-right `.no-print` button calling the browser's own
`window.print()` — no PDF dependency (the print CSS already produces a clean PDF), hidden in the printed
output, and script-free (inline handler, no `<script>` tag, works offline from `file://`). A real PDF
generator was deliberately NOT added. Tested (`Render_HasPrintButton_ScriptFree_HiddenInPrint`).

**Print-chrome fix (2026-06-21, dev report).** The PDF showed the browser's own auto header/footer (date
top-left, `<title>` top-centre, `file://` URL + page number bottom) because `@page { margin: 14mm }` left
margin space for Chrome to draw them. Fixed: **`@page { margin: 0 }`** (no margin ⇒ Chrome draws no
header/footer chrome) + margins restored via `.report { padding: 14mm }`; and our OWN running header
(`SenseEase · 視覺舒適報告`) is `position: fixed` top-right so every printed page carries one clean header.
Trade-off: inner-page top spacing now comes from the section margins (≈11mm) rather than a per-page `@page`
margin — acceptable, and the only CSS way to suppress the chrome in Chrome.

Adopted (safe): friendly meta = `FriendlyDateRange` ("June 1 – June 14, 2026" / "2026年6月1日 – 6月14日", the
generation time stays in the JSON record); warmer section headings (Your SenseEase check-in / Comfort over time
/ Protection: on vs off / Screen motion & protection); warmer second-person prose in `ReportFallbacks` (+ the
LLM Summary system prompt); plainer captions (Screen motion intensity / 畫面晃動程度; Protection activations per
day / 每日防護啟動次數); table headers (Avg. discomfort / 平均不適感; Activity / 使用情境); category labels
(觀看影片 / 遊戲 / 網頁瀏覽); posture labels (Fine-tune flexibility / 畫面微調彈性; Recent visual pressure / 近期視覺壓力,
"Level N / 第 N 級"); warmer disclaimer with a privacy reassurance. No emoji (preserves the technical identity +
clinician-appropriateness; available if Kirby wants the fully-casual consumer vibe). All EN/ZH renderer literals
pass the static lint; the warm `ReportFallbacks` prose passes the guard golden test.

---

*Cited files: `SettingsContent.xaml(.cs)`, `ClinicalExportService.cs`, `ClinicalExport.cs`,
`{SymptomEvent,DailyBehaviorAggregate,HabituationProfileSnapshot}.cs`, `FmsCheckInService.cs`,
`LongitudinalSummaryBuilder.cs`, `LocalLlmChatbotService.cs`, `MainWindow.xaml.cs` (chart),
`BannedPhraseLintTests.cs`, `docs/LOG-PATHS.md`, `docs/FULL_SETTINGS_REDESIGN_PLAN.md`.*
