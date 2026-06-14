> **[LEGACY]** Superseded by [Beta-1.md](../Beta-1.md) (2026-06-14). Retained for historical reference / back-trace; not the current plan.

# SenseEase — Alpha-1 Master Plan

**Version:** v1.1 — 2026-06-05 (cross-check audit applied)
**Status:** Planning artifact. No code is changed by this document; nothing is committed. Every PR lands through per-PR review (Constraint §3.9, ALPHA_HANDOVER).
**Owner:** Kirby · **Maintained as the single source of truth for the Alpha-1 cut.**

> **v1.1 changelog** (amendments verified against code/docs before applying): scheduled **H18** into W7/M6 — confirmed `ALPHA_SECURITY_BACKLOG.md:247` is `alpha-1` and H18 appears in **no** handover wave (only §3.4/§4.3); made the **banned-phrase lint** an explicit committed M0 deliverable; added risks **R-J** (expert-review scheduling) and **R-K** (H18 docs gap before expert engagement); flagged **W8+W9** as the top slip risk (+4 h on W9 for the **net-new English handbook** — confirmed only `HANDBOOK_zh-Hant.md` exists); folded **`HABITUATION-PROFILE.md §1.2`** citation fixes into the Alpha-1 §6 audit; carried the **G1** rationale correction into §10; flagged the `FileHabituationStore` vs `FileHabituationProfileStore` H16 target.

> **What this is.** One integrated, dependency-ordered plan that merges the three planning bodies that share the Alpha-1 cut:
> 1. **Engineering** — safety / security / persistence: [ALPHA_HANDOVER.md](ALPHA_HANDOVER.md) Waves **W1–W12**, drawing [ALPHA_BACKLOG.md](ALPHA_BACKLOG.md) (A–G), [ALPHA_SECURITY_BACKLOG.md](ALPHA_SECURITY_BACKLOG.md) (H1–H53), [CodeReview-Opus4.8.md](../CodeReview-Opus4.8.md) (EM/NE).
> 2. **Design / UX rebrand** — [docs/design/LOCKED_DIRECTION_ALPHA1.md](design/LOCKED_DIRECTION_ALPHA1.md) Waves **D0–D6**.
> 3. **Clinical / concept / retention / integrity** — [Cross_BACKLOG_AUDIT_Plan_for Alpha.md](Cross_BACKLOG_AUDIT_Plan_for%20Alpha.md) work-streams **WS-P / WS-C / WS-R / WS-M / WS-G / WS-H** (root causes R1–R4).
>
> **How to read.** §1 locked foundations · §2 the three layers + Alpha-1/Alpha-2 split · §3 the dependency graph · §4 **timeline & milestones (M0–M8)** · §5 **code-review plan** · §6 **audit plan** · §7 **user-test plan** · §8 risk register · §9 Definition of Alpha-1 Done · §10 deferred · §11 item→milestone index.
>
> **Authority order for any conflict:** **Clinical-safety > Privacy > Engineering-locks > Product-semantics > brand.**

---

## 1. Locked foundations

### 1.1 Product decisions (FIXED — signed 2026-06-05)
1. **Quiet Guardian** is the primary posture — the app disappears into the OS and protects silently; engineering telemetry is **demoted** behind an Advanced view, never deleted.
2. **Day-1 protection** — the always-on "calm field" defaults **ON** with an adjustable baseline (tier 1 = 0 % alpha; nothing visibly dims at rest).
3. **Motion policy = mask-only** — the sole routine motion is the clinical FOV cross-fade (`MaskSmoothingFactor=0.25`). The field ships static; no decorative motion.
4. **"Just Talk" deferred to Alpha-2** — blocked on `SafetyFloor.cs` (eng W1).

### 1.2 Engine sign-offs (APPROVED 2026-06-05 — final go still per-PR)
- **D3 Liveness contract** — rearming watchdog + `SensingHealthChanged` in `WgcSensingProvider`; `LivenessState` on `MainViewModel`; Faulted bar/tray. No OFI/tier math change, no persistence.
- **D5 Soften seam** — `ApplyUserFeedback(int, bool isDeliberatePreference=false)` guarding the single `NotifyDiscomfort` call (`MockInferenceEngine.cs:299`). Q4.5 preserved; the Card discomfort ramp stays a *complaint*.
- **D6 Calm-field default-ON** — **atomic** flip: `_persistentOverlayEnabled` FALSE→TRUE (`MainViewModel.cs:794`) **and** baseline tier 4→1 (`:807`) in one PR.

### 1.3 Cross-audit reconciliations folded in (from the design ↔ backlog cross-audit, 2026-06-05)
- **§6 non-medical red line is now a hard Alpha-1 constraint.** No external claim of therapy / rehab / neuroplasticity / desensitization / **tolerance** / recovery. My own retention copy is corrected: drop *"building your tolerance," "needing this less," "that Learns You";* describe the **app's behavior** (*"SenseEase is easing its protection as your screen stays calm"*), never the **user's improvement**. Enforced by the CI **banned-phrase lint** (§6.2).
- **P1 absorbed into D1** — personalize the **displayed comfort band** (`DisplayStressPercent = Pressure/EffectiveTrigger×100`), **not** the engine tier, so Quiet Guardian's hero actually reflects personalization (closes root-cause R1's "personalization invisible" gap).
- **Quiet Guardian = comfort layer** (honest, no therapy claim). The exposure/anchor narrative attaches only to the **opt-in grid layer** (WS-C), which is **Alpha-2**.
- **R3c = H16** — atomic write of the habituation snapshot is treated as a **retention-data-integrity** item at Wave-2 priority (not merely state-integrity).

### 1.4 Inviolable hard constraints (every PR asserts these — ALPHA_HANDOVER §3)
Cannot break prior works (existing tests green; `MainWindow` stays instantiable; **App.Resources stays empty** → Calm tokens merge **view-locally** via `Theme/SenseEaseTheme.xaml`) · **Q4.5** (`_offset` preserved; never `_habituation.Reset()` for transient/shader resets) · grid world-locked · AppId locked · `head_tracker.py` core untouched · no PII in non-exempt logs · **no medical claims (§6)** · no commit / no `--no-verify` until asked.

---

## 2. The three layers & Alpha-1 / Alpha-2 split

| Layer | Alpha-1 (this cut) | Alpha-2 / v1.0 (deferred) |
|---|---|---|
| **Engineering** | W1 SafetyFloor · W2 engine-data integrity (EM1/EM2/C5/**H16**) · W3 sensing lifecycle · W4 UX-breakers · W5 chatbot intent · W6 loopback auth · W7 sidecar hardening · W8 installer/supply-chain · W9 PII redaction | W10 governance · W11 perf (NE31 iGPU) · W12 cleanup |
| **Design** | D0 tokens · D1 Home (+P1) · D2 Card · D3 Liveness · D4 legibility (+§6 lint) · D5 soften-seam · D6 day-1-on · re-skins (Insights/Calibration/Settings/Onboarding) | Dark + high-contrast themes · grid two-layer UX (with WS-C) |
| **Clinical / integrity** | **P1** personalized displayed score · **R3c**(=H16) · **§6** claims audit (cross-cutting) | WS-C (C1/C2 + A7 grid-as-anchor) · C-DOC + G-CITE + G-DOC + G-TRAIN governance · WS-R R1c supervision-surrogate · WS-M M1/M2 measurement validity · WS-H H-CAL/H-MULTI/H-DAMP · R2c escalation (v1.0) · P2 engine personalization (v1.0) |

---

## 3. Integrated sequence (dependency graph)

```
SERIAL BACKBONE (clinical-safety → data → sensing → UX → intent):
  W1 ─▶ W2 ─▶ W3 ─▶ W4 ─▶ W5
   │     │     │     │
   │     │     │     └─▶ D2 (Card needs W4 EM5/NE27 + W5 intent)
   │     │     └─▶ D3 Liveness (same file as W3 watchdog) ─────────────┐
   │     │     └─▶ D6 day-1-on (rides hardened sensing) ── LAST ───────┤
   │     ├─▶ P1 (EffectiveTrigger read) ─▶ D1 Home ─▶ re-skins         │
   │     ├─▶ D4 legibility (reliable _offset)                          │
   │     ├─▶ D5 soften-seam (same file MockInferenceEngine)            │
   │     └─▶ R3c=H16 (offset survives crash)                           │
   └─▶ (unblocks Alpha-2 Just-Talk)                                    │
                                                                       ▼
SECURITY TRACK (parallel — different files):  W6 ─▶  ;  W7 ─▶ W8 ;  W9
  W6 needs W2+W5 · W7 needs W2+W3 · W8 needs W7 · W9 needs W8 (PathRedactor)
D0 tokens: no dependency — starts immediately.
```

**Critical path to cut:** `W1 → W2 → W3 → W4 → W5 → (P1→D1, D2, D4, D5) → D6 → M8 audits+user-test`. The **security track (W6–W9)** runs in parallel (touches sensing/IPC/installer/logging, not the UI surfaces), so it is *not* on the design critical path but **is** on the Alpha-cut gate.

---

## 4. Timeline & milestones

> Effort hours are from ALPHA_HANDOVER per-wave estimates; design/clinical estimated. Weeks are **relative & effort-based** (calendar them to your cadence). Apply the handover's **+30–50 % churn buffer** on every milestone that exits at a user-verification gate (§4.4 lesson).

| # | Milestone | Wk | Scope (items) | Runs ∥ with | Exit gate (must pass to advance) |
|---|---|---|---|---|---|
| **M0** | Freeze & scaffold | W0 | Lock this plan; **commit** the CI gates as real test files (sparse data ok): **banned-phrase lint** (`BannedPhraseLintTests`, EN+繁中), **no-orphan-hex lint**, test baseline; **D0** Calm token foundation [PRES] — **commit** `Theme/SenseEaseTheme.xaml` + `Controls.xaml`; **order H26 code-signing cert** + **book OT 葉淨維 / ophthalmology 陳韵臻 review slots** (long-lead — day 1) | cert + expert booking ∥ D0 | `dotnet test` green incl. `MainWindowSmokeTests`; **banned-phrase lint test committed & live in CI**; `Theme/*.xaml` committed + load view-locally; cert ordered + expert slots booked |
| **M1** | Clinical-safety floor 🔒 BLOCKING | W1 | **W1**: H1/H2/H3/H4/H8 + NE7 + C3 (`SafetyFloor.cs`) ≈10 h | — | `SafetyFloorParityTests`; `"我想死"`+picker → crisis reply only, **0** `ProposalReceived`; `[REDACTED: safety_floor]` in `chat_history.jsonl` |
| **M2** | Engine & retention-data integrity | W2–3 | **W2**: EM1, EM2, **C5**, **H16=R3c**, NE15/16/23, H14/15/17 ≈14 h | security prep | `_offset` survives 10× kill mid-write; ≤1 profile save/min (not 15/s); longitudinal obs survive; C5 reset preserves `_offset` |
| **M3** | Sensing lifecycle + **Liveness** | W3–4 | **W3**: EM3/EM4/NE10-12/14/25/H37-38 ≈12 h → **D3 Liveness** [ENG] | W6/W7 prep | 100× Power-Save toggle, 0 crash; **`LivenessContractTests`** incl. **Power-Save ≠ Faulted**; OnTick-fatal raises `SensingHealthChanged(false)` |
| **M4** | UX-breakers + chatbot intent | W4–5 | **W4**: EM5/NE26/27/30/H7 ≈8 h · **W5**: EM6/NE4/5/6/8/H5/6 ≈9 h | security track | auto-pop Card survives foreground poll; every shader/intent direction correct; `personal_pressure_scale` renders intact |
| **M5** | Personalization-visible + Design adoption | W5–7 | **P1** `DisplayStressPercent` → **D1 Home** (absorbs P1) → **D2 Card** (+EM5/NE27 in same change) → **D4 legibility** (+§6 lint, full copy audit) → **D5 soften-seam** [ENG] → re-skins (Insights/Calibration/Settings/Onboarding; Calibration 5×-template de-dup) | **M6 security ∥** | smoke green; **UVHS per surface**; banned-phrase lint green; **personalization-visible test** (SENSITIVE band ≠ RESILIENT at equal pressure); D5 both-path tests; all demoted bindings live under Advanced |
| **M6** | Security hardening | W4–8 (∥) | **W6** loopback auth (H9-11/29/34) ≈9 h · **W7** sidecar (H12-13/**18**/32/35-36/40) ≈13 h · **W8** installer/sign (H26-28/19-21/33/47-52) ≈17 h · **W9** PII (EM7/H22-25/41) ≈**20 h** (+4 h net-new English handbook) · **⚠ W8+W9 = top slip risk** | M3–M5 (different files) | every handover security gate; **H18** privacy-doc fix landed (`--diag` stripped, `.exe`/path names corrected); **Authenticode-signed** installer, normal SmartScreen; `strings *.pdb \| findstr kirby` empty; loopback spoof refused; integrity manifest verified |
| **M7** | Day-1 protection | W8 | **D6** calm-field default-ON **atomic two-flip** [ENG] — **LAST design wave** | — | "0.00 alpha at rest" test (`ResolveDisplayedTier(1)==1` & `VimsLevels[0].Alpha==0.00`); natural higher tier still wins; PR rejected if only one flip lands |
| **M8** | Audits · User test · **Alpha cut** | W8–10 | Full-tree code review · security re-audit · **§6 claims audit** · a11y/UVHS sweep · **user test (dGPU + iGPU)** · installer smoke on clean Win 11 VM | audits ∥, then user test | **§9 Definition of Alpha-1 Done** (all gates) |

**Milestone dependency note:** M1→M2→M3 is strictly serial (safety → data → sensing). M4 follows M3. M5 needs M2 (P1/D4/D5) + M3 (D3) + M4 (D1/D2). **M6 runs parallel** to M3–M5. M7 is last (needs M3). M8 gates the cut.

**Realistic envelope:** ~108 h engineering (W1–W9) + ~70 h design + ~15 h clinical/P1/§6 ≈ **~195 h focused work**, plus review/audit/user-test overhead and churn buffer → **≈ 9–10 calendar weeks** for a solo/small effort. Front-load **H26 cert procurement** (M0) — it is the only external long-lead blocker.

---

## 5. Code-review plan (meticulous)

**Principles.** Every change is a small PR (**≤500 LOC changed**, ALPHA_HANDOVER §8.4). Test-first where possible. **No commit, no `--no-verify`, until Kirby approves the PR.** CI must be **green for 3 consecutive runs on `main`** before the cut.

**Review depth scaled to blast radius:**

| Change class | Review | Tooling |
|---|---|---|
| PRES re-skins (D0/D1/D2/D4 visual, re-skins) | Standard diff review + UVHS | `/code-review` medium |
| Engine/persistence (W1, W2, **D3, D5, D6**, W6, W9) | **Deep** + adversarial verification of every finding before applying | `/code-review` high→max, **+ `/code-review ultra`** (cloud multi-agent) on W1, W2, D3, D5, D6, W6, W9 |
| Sidecar / native interop (W3, W7) | Deep + threading/lifecycle focus | `/code-review` high |

**Per-PR locked-decision checklist** (pasted into every PR description, must all be ✅):
- [ ] Existing tests green; `MainWindowSmokeTests` still constructs; no `{StaticResource}` moved to `App.Resources`.
- [ ] **Q4.5** — `_offset` untouched by any reset/shader path; no `_habituation.Reset()` for transient.
- [ ] **§6** — no therapy/neuroplasticity/tolerance/desensitization/recovery wording in any user-facing string; banned-phrase lint green.
- [ ] Edge-triggered (not level-triggered) for any "once per event" logic (EM1 lesson).
- [ ] Read-modify-write under lock for any multi-writer file (EM2 lesson).
- [ ] No new PII in any log call; `DateTime.UtcNow.ToString("o")`, `pattern_id` not raw text, redacted paths.
- [ ] If a test assertion changed, it's called out with code evidence (handover §3.8).
- [ ] Locked decisions cited: grid world-locked, AppId, `head_tracker` core, design-token view-local merge.

**W2 file-disambiguation note (prevent implementer mixup — the names are near-identical):** **H16** targets **`FileHabituationStore.cs`** (51 lines; engine state `_offset`/phase; currently bypasses `AtomicJsonWriter`). **H14/H15** target **`FileHabituationProfileStore.cs`** (130 lines; calibration + longitudinal observations + schema versioning). Every W2 PR description must state which of the two each item touches.

**Cadence.** Review each wave's PRs as they land; **after each milestone merge, re-run that milestone's exit-gate end-to-end on the dev machine** and post results. **Pre-cut (M8): one full-tree sweep** (the `CodeReview-Opus4.8.md` pattern) to catch cross-wave regressions introduced by the design/security interleave.

---

## 6. Audit plan (meticulous)

Five audit tracks; each has an owner-gate placed in the timeline.

**6.1 Security audit** — *gates M6 + M8.* Re-run the cybersecurity audit harness (provenance `w5ga7nd83` is replayable) after W9 and again pre-cut to catch anything W1–W9 introduced. Checks: Authenticode signature + normal SmartScreen; `strings SenseEase.App.pdb | findstr kirby` empty; `wgc_diag.txt` PII-free; loopback `/health` spoof refused without API key; integrity-manifest verify before any `Process.Start`; dist tree trimmed ≥500 MB; uninstall removes HKCU Run key.

**6.2 Clinical / §6 claims audit** — *cross-cutting; gates M5 + M8 (the project's credibility item).*
- **Banned-phrase lint (CI):** a unit test scanning the copy table; rejects `absorbed, prevented, reduced your, motion you didn't, symptoms, less sick, {N} minutes, desensitiz*, tolerance, recovery, neuroplastic*, rehab*, therap*, needing this less, since you started`. Both EN + 繁中 equivalents.
- **Manual claims review:** every user-facing string (UI, onboarding, tray, Card, any in-app presentation) read against the §6 red line — describes app behavior, never user improvement.
- **Expert review (scope-aligned, §6 #3):** OT 葉淨維 reviews mechanism-layer / habituation copy; ophthalmology 陳韵臻 reviews comfort / eye-safety copy. Neither endorses outside their scope.
- **Citation pre-flight:** any cited statistic on a user-facing surface must be correct per Appendix A of the cross-audit (full G-CITE/whitepaper correction is Alpha-2, but **no wrong citation may reach a user-facing surface in Alpha-1**). **Correct `docs/HABITUATION-PROFILE.md §1.2` now (≈30 min)** — it is the doc the OT/ophthalmology reviewers read to scope their engagement, so its Chang-Hain / Pavlou / VIMSSQ errors must be fixed *before* expert review, not deferred to the full Alpha-2 G-CITE pass (closes R-K).

**6.3 Design / accessibility audit** — *gates M5 + M8.* **UVHS** (User-Visual-Hard-Stop, 4 anti-cheat clauses, per `uvhs-playbook`) for every visible change; WCAG **AA floor / AAA body contrast**; `AutomationProperties.Name` on every interactive control + a **Narrator pass**; **Reduce-Motion** + **high-contrast** behavior; **DPI 100/125/150** reflow; colorblind-safe **status triple-coding** (icon + label + color), incl. **Faulted vs Paused vs high-strain distinguishable by outline shape, not hue alone.**

**6.4 Data-integrity audit** — *gates M2.* `_offset` survives 10× kill-mid-write; profile save ≤1/min; longitudinal observations survive a forced persist; schema-downgrade attack returns defaults; event log persists across restart and the dashboard observes it (M1 dashboard-mirror lesson).

**6.5 Supply-chain / dependency audit** — *gates M6 (W8).* GGUF + `face_landmarker.task` source URLs + SHA-256 pinned and verified; llama.cpp ≥ b9500; `opencv-python` / `pyinstaller` pinned; integrity manifest covers every bundled binary.

---

## 7. User-test plan (meticulous)

**Protocol.** **UVHS** for every visible change (no self-reported "looks done" — the user observes real behavior; 4 anti-cheat clauses). Each scenario records pass/fail per hardware class with a screenshot/inspection artifact.

**Hardware matrix (both required for the cut):**
- **dGPU** — NVIDIA dev machine (fast path).
- **iGPU** — Iris Xe slow-path tester (the calibration-stutter machine).

**Scenario suite:**
1. **Fresh install + onboarding** — tone reads calm/reassuring; webcam off by default disclosed; first-run is not the dark banner.
2. **60 s calibration demo** — plays, suggests a personalized preset; re-run accumulates (doesn't wipe).
3. **Day-1 protection (D6)** — at rest **nothing visibly dims** ("ready," not "always dark"); the mask deepens only when real motion appears; baseline raisable.
4. **Pop-up Card under real strain** — one primary action ("緩和畫面 / Ease my view now"); single calm discomfort ramp; auto-pop survives; all three proposal buttons disable on expiry.
5. **Liveness / fault (D3)** — inject a sensing fault (force `WgcSensingProvider.Stop`); confirm the **Faulted** state surfaces calmly and is **visibly distinct from Paused** (solid vs dashed outline), self-heal fires once, "Am I protected?" self-check answers honestly. Worst-case silent-death→visible ≤ 5 s.
6. **Benefit-legibility comprehension (§6 risk)** — recruit testers to read the progress/benefit copy and report what they think it claims; **fail if any tester reads it as a medical/recovery claim.**
7. **Accessibility** — Reduce-Motion honored (chart static, no typing dots); Narrator reads every control; high-contrast theme legible.
8. **5-min 1080p video soak** — no offset wipe; jank acceptable on dGPU; **iGPU stutter is a known Alpha-2 item (NE31/W11)** — record but don't block the cut on it.

**Safety-test discipline (hard rule, §4.4):** **do NOT** have testers exercise crisis / self-harm phrases against the live LLM. The safety floor (W1) is verified in the **dev environment only**, with synthetic inputs.

**Retention baseline (for WS-R, Alpha-2):** capture Alpha-1 tester engagement/retention as the *baseline* for the future supervision-surrogate work. **Do not** frame or report it as efficacy.

**Acceptance:** every milestone exit-gate passes on **both** hardware classes; each scenario has a recorded tester sign-off.

---

## 8. Risk register

| # | Risk | Trigger | Mitigation | Owner-gate |
|---|---|---|---|---|
| R-A | Breaks prior works (smoke / `App.Resources`) | Tokens in `App.Resources`; TwoWay binding to read-only | View-local token merge; new bindings `OneWay`; smoke gate every PR | M0/M5 |
| R-B | D6 non-atomic flip → everyone over-dimmed ~43 % | Flip `enabled` without baseline→1 | Single PR; **"0.00 alpha at rest" test**; reject if one-sided | M7 |
| R-C | Card chat path silently re-opens before SafetyFloor scope ready | Future change re-enables hidden input | **Canned-only test** is the regression lock; chat stays removed in Alpha-1 | M5 |
| R-D | §6 medical-claim leak in copy | "tolerance / needing this less / improving" wording | Banned-phrase lint + manual + OT/ophthalmology review | M5/M8 |
| R-E | Code-signing cert long-lead blocks installer | Procurement started late | **Order at M0/day-1** (Azure Trusted Signing or DigiCert) | M0 |
| R-F | iGPU calibration stutter | Full-res WGC readback (NE31) | Deferred to **Alpha-2/W11**; user test records as known | M8 |
| R-G | Retention data (`_offset`) wiped | Non-atomic write / over-broad reset | W2 (EM1/EM2/C5/**H16**) gates; D5 preserves Q4.5 | M2 |
| R-H | Personalization invisible in hero (root-cause R1) | Hero uses un-personalized tier | **P1** folded into D1; personalization-visible test | M5 |
| R-I | Scope/time churn at verification gates | User-verification rework (§4.4) | +30–50 % buffer per gated milestone; note actuals vs plan in PRs | all |
| **R-J** | **Expert-review scheduling latency** blocks the M8 cut gate | OT/ophthalmology unavailable when M5 copy lands | **Book slots at M0**; draft the copy review during M4; submit with D4 | M5/M8 |
| **R-K** | **H18 docs↔code gap surfaces during expert review** (PRIVACY.md ↔ `--diag`) undermines the engagement's credibility | Reviewer reads `HEAD-TRACKING-PRIVACY.md` / `HABITUATION-PROFILE.md` before H18 lands | Land **H18 in W7 before** expert engagement; correct `HABITUATION-PROFILE.md §1.2` citations first (§6.2) | M6 |

**Residual watch-item (no open P0):** the Card "canned-only" guarantee is the seam between the deferred Just-Talk surface and the safety floor — do not weaken its test until Alpha-2 builds Just-Talk on top of the landed `SafetyFloor.cs`.

---

## 9. Definition of Alpha-1 Done

All of the following, on `main`, CI green ×3:

**Engineering (ALPHA_HANDOVER §5):** W1–W9 merged with passing tests · `SafetyFloorParityTests` + bilingual safety-floor coverage exist and pass · whitepaper §13.6 roadmap updated · `docs/SECURITY.md` written · installer rebuilt, hashed, smoke-installed on a clean Win 11 VM · fresh cybersecurity audit pass.

**Design:** D0–D6 merged; smoke green; UVHS sign-off per surface; `AutomationProperties` coverage > 0 on all interactive controls; Reduce-Motion + high-contrast functional; alarm-red retired for `ComfortHigh`.

**Clinical / integrity:** **P1** personalization visible (test) · **R3c/H16** offset survives crash · **§6 claims audit clean** (banned-phrase lint green + manual + expert review) · no wrong citation on any user-facing surface.

**User test:** all §7 scenarios pass on **dGPU + iGPU**, with the benefit-legibility comprehension test confirming **no tester reads the copy as a medical claim.**

---

## 10. Deferred — Alpha-2 / v1.0

**Alpha-2:** eng **W10** (governance C/D/E/F/G), **W11** (perf — NE31 iGPU), **W12** (cleanup) · design **dark + high-contrast themes**, **grid two-layer UX** · clinical **WS-C** (C1/C2 + A7 grid-as-anchor, profile-aware defaults), **C-DOC + G-CITE + G-DOC + G-TRAIN** governance/citation corrections, **WS-R R1c** supervision-surrogate chatbot, **WS-M** M1/M2 measurement validity + brightness-confound suppression, **WS-H** H-CAL/H-MULTI/H-DAMP head-tracking robustness · **Just-Talk** conversational front door (on the landed SafetyFloor).

> **G1 rationale correction (carry into Alpha-2 — do not repeat the backlog's error):** `ALPHA_BACKLOG.md` G1 frames `/grid off` as *"unconditionally safe (additive visual aid)."* Per the cross-audit, the anchor grid is the **therapeutic** component (stationary anchoring), **not** decoration — so G1's *mechanism* is preserved but its **default policy** must become **"profile-aware default + light recommendation"** (WS-C), not always-safe-off, to avoid reducing benefit for the SENSITIVE / vestibular subgroup.

**v1.0:** **P2** engine-level personalized trigger · **R2c** escalation (offset above baseline) · eng A8 multi-monitor · any decorative/ambient motion (research-gated, behind Reduce-Motion + a UVHS luminance measurement).

---

## 11. Item → milestone index

| Source ID | Item | Milestone |
|---|---|---|
| H1-4/H8, NE7, C3 | Crisis safety floor | M1 |
| EM1, EM2, C5, **H16/R3c**, NE15/16/23, H14/15/17 | Engine + retention-data integrity | M2 |
| EM3/EM4, NE10-12/14/25, H37/38 | Sensing lifecycle | M3 |
| **D3** | Liveness contract [ENG] | M3 |
| EM5, NE26/27/30, H7 | UX-breakers | M4 |
| EM6, NE4/5/6/8, H5/6 | Chatbot intent | M4 |
| **P1** | Personalized displayed score | M5 (→D1) |
| **D0/D1/D2/D4** + re-skins | Token foundation, Home, Card, legibility, re-skins | M0/M5 |
| **D5** | Soften seam [ENG] | M5 |
| W6/W7/W8/W9 (H-series) | Security hardening | M6 (∥) |
| **H18** (alpha-1; privacy docs ↔ sidecar `--diag`) | Bundled into W7 | M6 |
| **D6** | Day-1 calm-field ON [ENG] | M7 |
| §6 audit, full review, user test | Audits + cut | M8 |

---

## 12 · W1+W2 code-review findings & hardening (2026-06-05)

A recall-mode review (81 agents, 67→52 verified→15 ranked) of the committed W1+W2 — full list in [CodeReview-W1W2.md](../CodeReview-W1W2.md). Triage + fix plan:

**Fixed in the W2-hardening commit:**
| # | Issue | Fix |
|---|---|---|
| 1 | SafetyFloor missed smart-quote phrases (NFKC doesn't fold `’` U+2019) | Fold curly apostrophes → ASCII before matching |
| 2 | EM2 fails-OPEN: a transient null `Load()` → `BuildProfileSnapshot` null-fills + `Save` clobbers the vestibular advisory | Track `_profileExistedAtLoad`; skip the persist when `Load` returns null but a profile existed at boot |
| 3 | Dashboard Feedback box logs raw self-harm text (2nd entry point, H2 gap) | Redact to `[REDACTED]` when `SafetyFloor.Trips` fires |
| 4/5 | H14 date guard rejected genuine old-build v1 files (data loss) + timezone/blank bypasses | Remove the date arm; keep the robust flag guard |
| 6 | "kms" tripped on the km abbreviation ("5 kms"); "kys" substring-matched ("whiskys") | Remove "kms"; word-boundary "kys" |
| 7 | bare "想死" tripped "想死你了" (greeting); "跳樓" tripped "跳樓大拍賣" (fire-sale) | Remove bare "想死" (keep specific forms); skip "跳樓/跳楼" when a sale marker is present |

**Safety-floor tuning decisions (for your awareness):** these trade detection breadth for fewer false-positives in a Traditional-Chinese / motion-app context. "跳樓" alone is still a trigger; only the explicit sale-idiom context is excluded. Re-tune if tester data shows misses.

**Deferred / accepted (not blocking W2):**
| # | Issue | Disposition |
|---|---|---|
| 9 | No shipped writer sets `MedicalAdvisoryShown` → vestibular intercept may never fire | **Pre-existing calibration-pipeline gap** (CalibrationApplicator should set it) — new backlog item, not a W2 regression |
| 10 | `MistralChatbotService` keeps raw crisis text in in-memory `_history` before the floor | Pre-existing; in-memory only; fixing breaks a pinned test — Wave-9 log-hygiene follow-up |
| 11 | H17 1 MB/day cap silently drops forensic lines (no rollover) | Follow-up: roll to a `.N` file instead of dropping (low risk; cap rarely hit) |
| 12 | `EventLogEntry.Timestamp` is local while files roll on `UtcNow` | **Wave 9 / H41** (UTC-everywhere migration) |
| 13 | Safety-floor VM test writes the dev's real `chat_history` with no teardown | Needs an injected `IChatHistoryStore` seam — test-hygiene follow-up |
| 14 | `FileChatbotActionLog` uncapped in-memory list + sync boot load | Follow-up: soft-cap the in-memory window (low volume) |
| 15 | EM2 adds a `Load()` on the persist path | Accepted — bounded by EM1's ~1/min save throttle |

---

## 13 · Adaptive Fit Layer (自動適配層) — work-stream, review verdict & sequencing (2026-06-06)

Source spec: [SenseEase-Adaptive-Fit-Layer-v1.md](SenseEase-Adaptive-Fit-Layer-v1.md) (user draft, committed as SSOT). A **convergence layer** that unifies one "ruler" — 50格點 (model set) → 12檔 (user `+/-`) → engine params → state readouts → **5級動態感** (the only intensity language the user sees) — with a deterministic selector that picks the best-fit set, applies adjacent moves silently, and chatbot-proposes cross-segment moves. Intensity axis only; habituation independent.

**Review (2026-06-06, 6-agent codebase-grounded):** the spec as written is **not implementable** and is **predominantly Alpha-2** — its three foundational assumptions are contradicted by the live code. The whole layer is gated on **M1 (MAD→OFI calibration)**, which is **Alpha-2 by design** (§2 table).

**Ruling (Kirby 拍板, 2026-06-06, Q1–Q8 — final):** directions confirmed. Q1 = Alpha-2-post-M1; **Q2 = B (native 12-state rebuild that *replaces* VimsTier 1–10, habituation fully separated — high-risk, knowingly accepted)**; Q3 = B (all-users no-confirmation full Disable, §3.4 retracted); Q4 = shader-only; Q5 = Disable in Alpha-1; Q6 = no dormant infra; Q7 = wording red line always-on; Q8 = three spec fixes applied before SSOT. Full decision table in **§13.3**; the Q2 code-grounded risk assessment + two-stage phasing + the one open clarification in **§13.5**. **Engine code for the Q2 core rebuild waits for Kirby's second confirm after the §13.5 risk report.**

### 13.1 Blockers (each verified against code, not opinion)
| # | Blocker | Evidence |
|---|---|---|
| **B1** | **12-level Intensity Ladder does not exist** | No `SetLevel`/`ResolveLevel`/`ResetShaderToLevelDefault` repo-wide; `IInferenceEngine` has no level API; real model is `VimsTier` 1–10 + 3-band comfort (`SenseState.cs:46`); `SenseEase-Intensity-Ladder-12-Levels.md` not in repo. → **rebase onto VimsTier+3-band OR build a 12-level subsystem + tier↔level mapper.** |
| **B2** | **50-set deterministic selector is net-new** (not reuse) | `HabituationTrajectoryBuilder` = read-only one-line narrative (not a stats pipeline); `HabituationOverrideProcessor` = safety *validator* (not a scorer). Selector + `w1..w5` + confidence gate must be designed from scratch. |
| **B3** | **M1 hard-prerequisite unmet + is Alpha-2** | `WgcMadNormFactor=4.0`/`CursorVelocityNormFactor=120.0` still carry a live "Tune after first real WGC data" TODO (`SenseDefaults.cs`); no `baseline/wgc-mad-calibration.csv`; no `WgcSensingProviderCalibrationTests`; §2 defers WS-M to Alpha-2. **Live adaptation cannot ship in Alpha-1.** |
| **B4** | **`停用`/Disable contradicts vestibular always-on protection** | `HEAD-TRACKING-PRIVACY §3.4` force-disables the escape hatch for vestibular profiles *because the mask IS their VIMS protection*. A global "all-off incl. mask" Disable reopens that hole. → **reject Disable for vestibular, or keep mask on for them.** |

### 13.2 Phased plan (dependency-ordered; reflects the 2026-06-06 ruling)
| Phase | Cut | Items | Depends on |
|---|---|---|---|
| **P0 — rebase spec + draft copy** (no engine code) | **Alpha-1 · in progress** | Rebase spec onto Q2=B native 12-state (DONE: Q8 ①②③ fixes); write the Stage-2 model spec — concrete `w1..w5` formulas/units, quantified confidence threshold + metric, run cadence, state machine, Q4/Q5 ordering; draft ALL user-facing copy (5級 labels, Disable/Resume, proposals) into one strings table + run §6 banned-phrase lint | none |
| **S1 — Native 12-state rebuild** (engine core) | **Alpha-1 · GATED on 2nd confirm** | Replace `SenseState.VimsTier` 1–10 with the native 12-state scale (rescale the pressure discretizer `1+9t → 1+11t`; extend `VimsLevels` 10→12; remap `BandForTier` 3-band → 5級 with crossing-only relabel; retune `VimsLowTierReleaseCutoff` + tier-keyed consts); **sever habituation to static-at-calibration** (per §13.5 scope clarification); migrate **~40 test assertions** + the `CurrentVimsTier` persistence schema (backward-compat load). **Two commits: (a) rescale+relabel+test-migration, (b) habituation sever** — each independently reviewable/revertible. See §13.5 risk assessment | **P0 + Kirby 2nd confirm** |
| **P2 — Disable + vestibular full-disable** | **Alpha-1** *(Q5)* | One-tap full Disable for **all** users incl. vestibular — no confirm/warning/background-nag (Q3=B); clean stop (mask/sidecar/chatbot/adaptive off, data paused-not-zeroed); visible off-state resume affordance; Resume returns to pre-disable set+level **and re-fires the 3 apply-time intercepts on any queued proposal** (never auto-apply across a Disable boundary); docs already updated (`HEAD-TRACKING-PRIVACY §3.4` + `SAFETY-PROVISIONS §3.1`) | P0 (+ S1 if Disable touches the state surface) |
| **P1 — M1 calibration** (the hard gate) | **Alpha-2** | D1 baseline CSVs across idle/scroll/video/game/scene-cut + recalibrate; D2 pin 8× ratio; D3 5τ convergence; `WgcSensingProviderCalibrationTests` (canned greyscale → OFI bands). **Q1 time-floor: real WGC capture is the binding constraint — see §13.5** | P0 |
| **P3 — intensity model + selector meaning (Stage 2)** | **Alpha-2** | the per-set intensity model that makes the 12 native levels *meaningfully distinct* (`ResolveLevel(1..12)` net-new + per-level params, shader-only response-shape per Q4); real per-user stats pipeline from the event log (net-new — NOT reused from `HabituationTrajectoryBuilder`); add `w1..w5`/cooldown/confidence consts to `SenseDefaults` | S1 + P1 (calibrated OFI) |
| **P4 — selector + grid state machine** | **Alpha-2** | 50-set grid as explicit matrix, Manhattan adjacency at candidate-generation; confidence-gated argmax + coarse-band-first; dedicated `CooldownState` enum (replacing the conflated `Paused` phase); adjacent→silent-apply, cross-segment→Processor→Card + a `ChatbotActionKind` for set-moves; adversarial tests (no-move-on-noise, adjacency, Q4-before-Q5 ordering, dual-trigger cooldown precedence, no-oscillation) | P1 + P2 + P3 |
| **P5 — propose-confirm-for-set-change UI + governance** | **gated/later** | extend Card (or new surface) for set-change proposals distinct from the 3 existing intents; final §6 audit on all live copy; document selector + safety in whitepaper/SAFETY-PROVISIONS; **only the "auto-set-change clinical-effectiveness claim" is flag-off until clinical validation — the wording red line itself is NOT gated (Q7)** | P4 + sign-off |

### 13.3 Decisions — RULED (Kirby 拍板, 2026-06-06; supersedes the pending table)
| # | Decision | Ruling |
|---|---|---|
| **Q1 · Cut** | when does the layer ship | **A — Alpha-2, post-M1.** Kirby co-drives to delivery within the window; M1 is data-bound (§13.5). |
| **Q2 · Foundation** | rebase vs rebuild | **B — native 12-state rebuild that *replaces* VimsTier 1–10** (no thin mapping layer). High-risk, knowingly accepted, to kill the "two scales" confusion and give the built-in LLM a single ruler. **+ Habituation 徹底分家:** habituation axis becomes a fully independent module, does NOT enter the auto-adaptive feedback loop, statically assigned at calibration. Risk/scope/phasing assigned to coding agent → §13.5; **engine code waits for Kirby's 2nd confirm.** |
| **Q3 · Disable×vestibular** | confirm vs clean-off | **B — all users incl. vestibular: one-tap full Disable, no confirm/warning/background-recovery-prompt.** Formally retracts `HEAD-TRACKING-PRIVACY §3.4`. Informed 拍板; vestibular VIMS risk borne by user; retained for traceability; may be challenged at ATCC/OT review. Docs updated (§3.4 + `SAFETY-PROVISIONS §3.1`). |
| **Q4 · Response shape** | shader vs integrator | **A — shader-only for v1.** Do NOT touch the global LeakyIntegrator (decay/slope refactor deferred). |
| **Q5 · Disable cut** | Alpha-1 vs Alpha-2 | **Alpha-1** (per Q3, independent safety item, not bound to the selector). |
| **Q6 · Alpha-1 dormant infra** | flags/hooks vs none | **A — none.** Keep Alpha-1 clean until M1. **P6 and any new work: plan before implement.** |
| **Q7 · Non-medical red line** | gated vs always-on | **A — wording red line applies throughout, never gated.** Only the "auto-set-change clinical-effectiveness claim" is flag-off until clinical validation; the governance package is NOT deferred wholesale. |
| **Q8 · Pre-SSOT fixes** | apply 3 corrections | **A — DONE.** ① §11 "reuse TrajectoryBuilder/OverrideProcessor" → "net-new"; ② 12-level description → "native rebuild replacing VimsTier 1–10, habituation separated"; ③ §7 Disable → "all-users no-confirmation full disable". Verified against code in §13.5. |

### 13.4 Hard constraints (carry into every phase)
Vestibular: chatbot/engine relaxation is still propose-stronger-only and the 3 apply-time intercepts re-fire on Resume (the §3 hard cap is unchanged — it governs *proposals*, NOT the user's own Disable button, Q3/`SAFETY-PROVISIONS §3.1`); mask floor 0.20; the L-floor mapping must be pinned + tested. Never touch `_offset` (C5); atomic writes (H16). §6 non-medical red line on every new string (5級 labels, Disable/Resume, proposals) — never gated (Q7). Q4-snap (felt-intensity) clears the chatbot delta **before** snapping (Q5). Fully local, no egress, not collaborative filtering.

### 13.5 Q2 native-rebuild — code-grounded risk assessment + two-stage phasing (coding-agent judgment, 2026-06-06)

Mapped against live code (3 Explore passes over `SenseState`, every `VimsTier` consumer, the Habituation subsystem). **Verdict: "native rebuild replacing VimsTier" splits cleanly into two pieces with very different risk + gating** — do them as two stages, not one.

**Key code fact that lowers the risk:** `VimsTier` is **not an arithmetic state** — it is a pure *discretizer* of the pressure signal (`levelF = 1.0 + 9.0·(Pressure/TriggerPressure)`, [`MockInferenceEngine.cs:956`]). Its only consumers are an array index (`VimsLevels[tier-1]`, 10 entries), one SSOT band switch (`BandForTier`), display labels, logging/persistence, and a **read-only** input to `HabituationEngine.Step()`. Nothing does math *on* the tier. So replacing 10 bins with 12 is mechanical, not architectural.

**Stage 1 (Alpha-1, engine core, GATED on Kirby's 2nd confirm) — the rebuild that is safe now.**
Replace `SenseState.VimsTier` 1–10 with the native 12-state; rescale the discretizer (`9t → 11t`); extend `VimsLevels` 10→12 (define the 2 new colour/alpha rungs); remap `BandForTier` 3-band → 5級 (crossing-only relabel); retune the one hardcoded tier const (`VimsLowTierReleaseCutoff = 7`); migrate **~40 test assertions** (`TierColorPaletteTests`, `TierConverterParityTests`, `HabituationTests.VimsTier_*`, `D6DayOneProtectionTests`, fixture `CurrentVimsTier`) and the persistence schema (backward-compatible load of old 1–10 `CurrentVimsTier`). **Risk = moderate-but-wide**, driven by test churn + persistence migration, NOT by logic depth. **Feasible in the window** *if* done as a single-purpose change in **two commits** — (a) rescale+relabel+test-migration, (b) habituation sever — each independently reviewable/revertible, and NOT bundled with any Stage-2 model work.

**Stage 2 (Alpha-2, M1-gated) — the part that is intrinsically deferred.** The selector + per-set intensity model + response-shape that give the 12 native levels *distinct meaning* (P3/P4). Until M1 calibration data exists, a "native 12-state machine" has nothing principled to drive the 12 levels differently from today's pressure binning — so doing a "full rebuild" *now* would just be Stage 1 wearing Stage 2's clothes: same code, more churn, zero added value before M1. Hence the split.

**⚠️ One open clarification before Stage 1-b (habituation sever).** "習慣化於校準時靜態指定" has two possible scopes, and the code reality matters:
- Habituation is **already ~90% separate** — tier is pressure-only; the habituation `_offset` couples to **FOV radius only** (`ComposeFovRadius`), never to tier. The sole cross-axis link is `Profile.GetPostSpikePauseMs(tier)`.
- **Reading A (smaller, lower-risk):** "static at calibration" = the *new adaptive-fit selector* must not read habituation, and the live **chatbot profile-switch** is removed; but habituation keeps its existing FOV-personalization job (the weeks-long `_offset` earning stays — it is habituation's *own* loop, not the intensity feedback loop). ~2 coupling points to cut.
- **Reading B (larger, behavior-changing):** also freeze the `_offset` earning loop itself at calibration — rips out the relaxation-over-time + strike-halving mechanic, breaking many `HabituationTests` and changing shipped behavior.
- **Coding-agent recommendation: Reading A.** It satisfies "habituation does not enter the auto-adaptive feedback loop" with minimal blast radius and no behavior regression. **Confirm A vs B before Stage 1-b.**

**Q1 answers (requested in the directive).**
1. **M1 real-data time-floor:** M1 is bound by *real WGC capture under real motion*, not by coding effort. It needs labelled optical-flow samples across the comfort range (idle/scroll/video/game/scene-cut) plus per-user recalibrate sessions to anchor `WgcMadNormFactor`/`CursorVelocityNormFactor` (both still carry the live "tune after first real WGC data" TODO). Realistically that is **multi-session capture over days**, not hours — it cannot be manufactured inside a 2-day window. This is the binding constraint behind Q1=A.
2. **Two-day reach without full calibration data:** P0 (spec rebase + copy + §6 lint — DONE/doable today, no data) → **Stage 1** native rebuild (no data needed; needs the 2nd confirm) → **P2** Disable + vestibular full-disable (no data) → the Q8/Q3 doc rewrites (DONE). Stage 2 (selector/intensity meaning) stays Alpha-2, M1-gated. **P0 + Stage 1 + P2 is the realistic 2-day target.**

### 13.6 5級 動態感 strings + colour ramp — plugin-grounded P0 copy (2026-06-06)

Kirby delegated both the colour ramp and the labels to the **ui-ux-pro-max** plugin. Consulted its palette DB (`cli/assets/data/colors.csv`): every anxiety-relevant product — Mental Health (#22), Meditation & Mindfulness (#98), Senior Care (#57), Medical Clinic (#58), Healthcare (#8) — uses **calm blue / teal / lavender** and reserves red **only for `Destructive`**, never for a state signal. Verdict: a calm cool ramp, **no alarm red**, harmonised with the app's Crisp-Cool Accent `#3B6FC4`. This supersedes the legacy grey→amber→**red** 3-band for the user-facing surfaces.

| 級 (動態感, spec-locked) | 檔位 | EN label (§6-safe) | proposed hue | resting α | hover α |
|---|---|---|---|---|---|
| **極低** | L1–2 | Very Calm | `#9BB4D4` airy blue-grey | 0.15 | 0.65 |
| **低** | L3–4 | Calm | `#6F93C2` light calm blue | 0.22 | 0.74 |
| **中** (default L5) | L5–7 | Moderate | `#4A77B5` mid calm blue | 0.30 | 0.83 |
| **高** | L8–10 | Dynamic | `#345E97` deeper blue | 0.38 | 0.92 |
| **極高** | L11–12 | Very Dynamic | `#243F6B` deep calm slate | 0.45 | 1.00 |

**Polarity — CORRECTED 2026-06-07 (unified-measurement directive).** "動態感" describes the **screen's motion**: **極低 (Very Calm) = L1–2 = most stable screen; 極高 (Very Dynamic) = L11–12 = most motion** — low tier = calm, high tier = dynamic. This SUPERSEDES the earlier reversed "極高 = most natural (L1–2)" framing. User-facing zh label is "動態感 · 〔極低 / 低 / 中 / 高 / 極高〕"; EN uses the Calm⇄Dynamic root (Very Calm / Calm / Moderate / Dynamic / Very Dynamic) so the direction reads at a glance. The **5級 動態感 status is the ONLY status shown to end users**; VimsTier / pressure / the 12-檔 scale are internal/advanced only (one axis, three granularities). **Colours + α are UNCHANGED** (term + polarity only) — the ramp now reads light = calm, deep = dynamic, matching the corrected polarity. Implemented in code 2026-06-07 (`TierColorPalette.FeelLabel/FeelLabelEn`); pending Kirby's UVHS sign-off on a rendered screenshot. §6-safe (no diagnosis/treatment/optimisation claims).

> **Confirm-chain RECORD (2026-06-06 — closes the traceability gap):** Kirby's 2nd-confirm for the Stage-1 native rebuild AND the Reading-A habituation-sever choice (§13.5 Stage 1 + the open clarification at §13.5 "⚠️ One open clarification") were both **given and are realized in code**: dd8f458 (1-a-i, native 12-level scale), 3907a60 (1-a-ii, 3-band → 5級 + §6 lint), d86a9a8 (1-b, Reading-A habituation sever).
>
> **Status (2026-06-06):** P0 docs committed (dd3e35d). **Stage 1-a-i shipped green** (dd8f458 — native 12-level scale replaces VimsTier 1–10; 848✓). Next: **1-a-ii** (3-band → 5級 per §13.6, plugin-grounded, visual hard-stop) → **1-b** (habituation sever, Reading A — remove live chatbot profile-switch, keep FOV offset earning). Stage 2 (P3/P4) is Alpha-2, gated on M1 (P1). P5 is gated/later.

---

*Companion source-of-truth docs: [ALPHA_HANDOVER.md](ALPHA_HANDOVER.md) · [design/LOCKED_DIRECTION_ALPHA1.md](design/LOCKED_DIRECTION_ALPHA1.md) · [Cross_BACKLOG_AUDIT_Plan_for Alpha.md](Cross_BACKLOG_AUDIT_Plan_for%20Alpha.md) · [design/README.md](design/README.md). This master plan supersedes none of them — it sequences them. Nothing here is built or committed until each PR is approved.*
