# Health Report Redesign — Plan (for hard review)

**Status:** DRAFT / not started. Item 8 of the 2026-06-21 settings-feedback batch.
**Goal:** turn the current de-identified **JSON** export into a *well-organized, human-readable*
**健康報告 / Health report** — key indicators + graphical reviews + an LLM-authored plain-language
narrative the user (and, optionally, their clinician) can actually read.

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

*Cited files: `SettingsContent.xaml(.cs)`, `ClinicalExportService.cs`, `ClinicalExport.cs`,
`{SymptomEvent,DailyBehaviorAggregate,HabituationProfileSnapshot}.cs`, `FmsCheckInService.cs`,
`LongitudinalSummaryBuilder.cs`, `LocalLlmChatbotService.cs`, `MainWindow.xaml.cs` (chart),
`BannedPhraseLintTests.cs`, `docs/LOG-PATHS.md`, `docs/FULL_SETTINGS_REDESIGN_PLAN.md`.*
