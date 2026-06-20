# SenseEase — Proof-of-Concept Technical Whitepaper
# SenseEase — 概念驗證技術白皮書

**Version:** Pre-Alpha v0.11.1 — Alpha-2 *engine-integrity* revision (2026-06-08)
**Date:** 2026-06-08
**Status:** POC complete; Alpha-2 core **live** (Adaptive-Fit selector + auto-profiling both ON) — G_final GO, UVHS PASS, ~1058 / 0 / 3 tests (latest sealed count, commit `f381a43`; HEAD green save one known `UserPreferences` test-isolation flake)
**Document Class:** Internal engineering reference / 內部工程參考文件

> **Versioning note.** The authoritative version reference is `git tag`; the latest seal is **`v0.11`**, packaged as **v0.11.1** (PyInstaller one-dir + cross-vendor Vulkan hotfix). The work documented in §2.5, §6.4–§6.6, §6a, §9a, §11a and the feathering fix (§8.5) has landed on the `alpha1-w2-engine-integrity` branch **beyond** the v0.11 seal but is **not yet tagged**. This revision therefore describes the *current code state* (branch HEAD `3bb2a7e`, 2026-06-08), which is ahead of the v0.11.1 installer. Where the shipped installer and the current code differ, the difference is flagged inline.

---

## Glossary of Frequently-Used Terms / 常用術語表

| Term | Definition / 定義 |
|---|---|
| **VIMS** (視覺誘發性動暈症) | Visually-Induced Motion Sickness. Sopite, oculomotor and disorientation symptoms produced by visual stimuli that imply self-motion the vestibular system does not corroborate. |
| **OFI** (光流指數) | Optical Flow Index. SenseEase's scalar proxy for the magnitude of screen-relative motion in the most recent sample window. |
| **MAD** (平均絕對差) | Mean Absolute Difference. Per-pixel L1 distance between two consecutive frames, the numerical basis of OFI in the WGC sensing path. |
| **WGC** (Windows.Graphics.Capture) | The Windows screen-capture API the default sensing provider uses. |
| **Leaky integrator** (漏積分器) | First-order linear time-invariant discrete-time accumulator with exponential decay. Maps OFI(t) to the continuous VIMS pressure scalar P(t). |
| **Pressure scalar** (壓力純量) | Continuous nonnegative scalar P(t) produced by the leaky integrator. Engine source of truth for level projection. |
| **VimsTier / Level** (等級 / 強度等級) | Integer **1–12** (the *native 12-level scale*, Adaptive-Fit Q2=B) derived from P(t). Engine-internal granularity; UI surfaces a 5-band「動態感」label. |
| **動態感 band / Feel band** (動態感帶位) | The five perceptual buckets the user sees (極低 / 低 / 中 / 高 / 極高 motion-feel), mapped from level ranges 1–2 / 3–4 / 5–7 / 8–10 / 11–12. A **calm cool-blue ramp** — deliberately no alarm-red (see §2.8). |
| **Driver / Passenger mode** (駕駛模式 / 乘客模式) | User-activity classification from `GetLastInputInfo`. Driver = actively producing input; Passenger = idle. |
| **Efference copy** (傳出複本) | The motor system's prediction of the sensory consequences of its own commands. The neurophysiological basis for the Driver / Passenger weighting asymmetry. |
| **Habituation** (習慣化) | A code-identifier name retained for the `Habituation*` engine types. The clinically accurate mechanism term for SenseEase's long-term effect is **graded exposure → sensory reweighting** (視覺再加權); for visually-induced vertigo, recovery is driven mainly by sensory reweighting / adaptation (see §2.5). The graded-exposure offset is an `_offset` **added to the FOV radius percent** over time (NOT subtracted from the trigger pressure — §6.5). |
| **Auto-profiling** (自動體質推估) | The Alpha-2 replacement for explicit Q1–Q5 calibration: a brand-new user is seeded at **level 5 / TYPICAL profile** and the engine adapts from real behaviour. Vestibular protection is offered via a single opt-in prompt, not a questionnaire (§2.5, §9a). |
| **Adaptive-Fit selector** (自適應擬合選擇器) | The Alpha-2 deterministic engine that nudges the user across a **10 × 5 = 50-set grid** of `(Band, Shape)` response curves based on their own 7-day behaviour, with a confidence gate, cooldown state machine and 24-hour propose-confirm for cross-band moves (§6a). |
| **Set / Band / Shape** (組 / 強度軸 / 形狀軸) | A **set** is one `(Band, Shape)` cell. **Band** ∈ 0–9 = intensity axis (deeper mask + tighter FOV floor). **Shape** ∈ 0–4 = response-shape tag (render-only in this foundation). Set0 = (Band 5, Shape 2) reproduces the pre-selector engine byte-for-byte. |
| **EffectiveTrigger** (有效觸發值) | `TriggerPressure / personal_pressure_scale` (+ optional absolute override). The personalised trigger consumed by the habituation spike/calm checks, the Adaptive-Fit stats, and the `DisplayStressPercent` hero metric — **NOT** by the mask hysteresis, FOV-radius projection, or level projection, which still read raw `TriggerPressure` (§6.5, §7.4). |
| **DisplayStressPercent** (壓力百分比) | The personalised 0–100 % hero readout = `clamp(100 · P / EffectiveTrigger, 0, 100)`. Engine-internal `Pressure` stays raw; only this display scalar is per-user (§6.5). |
| **behavior_log** (行為日誌) | The User-Model UM1 daily-aggregate JSONL (`behavior_log.jsonl`). Numeric-only, no PII — feeds the chatbot's `[behavior_trend]` block and the Adaptive-Fit stats (§11a). |
| **FOV mask** (視野遮罩) | The radial **peripheral attenuation mask** (周邊衰減遮罩) that dims peripheral content while preserving the foveal viewport — the *comfort / symptom-relief* layer, distinct from the **Chang–Hain anchor grid** (錨定格) — the stationary anchoring layer that carries the graded-exposure / therapy framing (§2.3). |
| **Notification Card** (提案確認卡片) | The propose-confirm UI surface where chatbot proposals and Adaptive-Fit set-move proposals reach the user. |
| **Ambient bar** (環境式狀態列) | The small always-on-top window that paints the current band as a coloured pill. |
| **OBS-3** | The three privacy hard-rules that govern what the head-tracker sidecar is allowed to publish across the IPC boundary. |
| **SafetyFloor** (安全底線) | The self-harm crisis interceptor, **extracted to `SenseEase.Core/Safety/SafetyFloor.cs`** in Alpha-1 (was duplicated across two services). Runs before persistence and before any engine mutation (§11.1). |
| **UVHS** (使用者可視硬停協議) | User-Visual-Hard-Stop Playbook — a cross-sprint reusable anti-cheat protocol for any sprint with visible output. |
| **Load-bearing** (關鍵承載) | A design choice, parameter value or invariant whose change would break a user-visible contract, a safety claim or a test. |
| **Working hypothesis (WH)** (內部工作假設) | A SenseEase-internal claim not yet externally validated. Marks places where the document is candid that a number or relationship is an engineering choice awaiting future calibration evidence. |

---

## 1. Executive Overview / 執行摘要

SenseEase is a Windows desktop application whose Proof-of-Concept (POC, 概念驗證) phase set out to answer one question: **can a non-medical, client-side software intervention measurably reduce Visually-Induced Motion Sickness (VIMS, 視覺誘發性動暈症) provocation on a conventional desktop monitor without degrading the user's primary task?** This whitepaper documents the architecture, theoretical grounding, parameter calibration, validation evidence, and process discipline that took the project from initial sensing prototypes to the current Alpha-2 *engine-integrity* code state on top of the v0.11.1 Pre-Alpha installer.

The system delivers four integrated subsystems. A **sensing layer** (感測層) derives an Optical Flow Index (OFI, 光流指數) from real-time Windows.Graphics.Capture (WGC, 像素差分擷取) pixel-difference measurement, augmented by cursor velocity and user-activity classification. An **inference layer** (推論層) integrates OFI through a noise-gated first-order leaky integrator (漏積分器) producing a continuous VIMS pressure scalar (壓力純量), then projects that scalar onto a **native 1–12 level** classification with hysteresis (遲滯). An **adaptation layer** (適應層) — new in Alpha-2 — replaces the old explicit Q1–Q5 calibration with **auto-profiling** plus a deterministic **Adaptive-Fit selector** that personalises the user's response curve from their own behaviour, and a **User-Model** subsystem that records numeric daily behaviour and surfaces a longitudinal trend to the chatbot. A **rendering layer** (渲染層) applies a radial peripheral attenuation mask (周邊衰減遮罩) plus a Chang–Hain stationary anchor grid (錨定格, see §2.3), an ambient bar (環境式狀態列), a tray icon (系統匣圖示), and a propose-confirm Notification Card (提案確認卡片). A locally-hosted **Ministral-3B** chatbot — `Ministral-3-3B-Instruct-2512-Q4_K_M.gguf`, served via a side-loaded Vulkan `llama-server` over an OpenAI-compatible HTTP surface and **lazily loaded on first chat** — provides a natural-language adjustment channel gated by an extracted-to-Core self-harm SafetyFloor, a five-rule structured-override Processor, a defensive intercept Applicator, and a pre-LLM **slash-command lexicon** (`/grid off`, `/mask N`, …). A MediaPipe FaceLandmarker sidecar — portable, vendored, Vulkan-accelerated — drives the foveal viewport centre from head pose without ever permitting raw landmark data to cross the IPC boundary.

The headline outcomes are:

- **G_final GO** — the final go/no-go gate (G1–G7 advisor gates plus the M8 safety review) cleared with no locked-decision violations across the head-tracking sprint arc.
- **UVHS PASS** — the User-Visual-Hard-Stop protocol (4 anti-cheat clauses, see §13.2) passed verification, including the clause-5 orthogonal-probe requirement satisfied by the `WgcDiagLog` monotonic-sequence sink. The feathering fix (§8.5) was UVHS-confirmed against the WebGL reference.
- **~1058 / 0 / 3 tests** — last sealed count (commit `f381a43`); the count dropped from ~1120 when the explicit-calibration test files were removed and rose again with the feathering and selector tests. HEAD is green save one known `UserPreferences` test-isolation flake (passes 16/16 in isolation). Per-layer breakdown in §13.5.
- **Alpha-2 core live** — `AppConfig.AdaptiveFitSelectorEnabled = true` ([AppConfig.cs:268](src/SenseEase.App/Configuration/AppConfig.cs)) and `AppConfig.AutoProfilingEnabled = true` ([AppConfig.cs:294](src/SenseEase.App/Configuration/AppConfig.cs)) since commit `c1461a9`.
- **M1 OFI calibration data is real** — `baseline/wgc-mad-calibration.csv` now carries reduced rows from a real capture on the developer's hardware (idle + 1080p video), partially closing the old audit-D1 gap (§5.2.6).

**Revision note (2026-06-08).** This revision supersedes the 2026-06-04 audit edition. The largest changes since: (a) the engine tier scale moved from **1–10 to a native 1–12 scale** with a 5-band「動態感」cool-blue label, retiring the Grey/Amber/Red model (§2.8, §6.4); (b) the **explicit calibration subsystem was deleted** (CalibrationWindow, Q1–Q5 cards, the calibration chatbot service, the demo-video loop) and replaced by **auto-profiling** (§2.5, §9a); (c) the **Adaptive-Fit selector** (50-set grid) and (d) the **User-Model** subsystem (UM1–UM5) landed (§6a, §11a); (e) the **SafetyFloor was extracted to Core** and hardened (H1–H3, §11.1); (f) the **feathering "halo/comet-tail"** artefact was root-caused and fixed with an asymmetric radius envelope (§8.5); (g) the chatbot **model now lazy-loads** and the unused CUDA backend (~148 MB) was dropped (§10.2). Several 2026-06-04 audit items are now resolved (D1 partial, F1, C1, C7/F2, A4 grid-off, C3 safety parity, E3 schema test) and are marked CLOSED in §13.6.

The remainder is organised as follows. §2 grounds each architectural commitment in theory. §3–§4 cover system and project structure. §5–§9 walk the data path from camera and screen to painted pixel, with **§5 (OFI)** and **§6 (VIMS pressure)** carrying the document's primary technical weight. §6a covers the Adaptive-Fit selector. §10–§11 cover the LLM integration and §11a the User-Model. §12 enumerates every load-bearing parameter. §13 records validation and roadmap. §14 is the bibliography; §15 the file index.

---

## 2. Theoretical Foundation / 理論基礎

This section grounds each architectural commitment against an externally recognisable concept from vestibular neuroscience, signal processing, or operant exposure psychology. Where SenseEase departs from established literature, the departure is marked an **internal working hypothesis (WH)**.

### 2.1 VIMS Pathophysiology, Triggers, and Desktop Relevance / VIMS 病理生理學、觸發因子與桌面相關性

Visually-Induced Motion Sickness, VIMS (視覺誘發性動暈症), is the family of sopite, oculomotor, and disorientation symptoms elicited when the visual scene implies self-motion that the vestibular and proprioceptive systems do not corroborate. The dominant mechanistic account remains **sensory-conflict theory (感覺衝突理論)**: when the optic-flow channel signals translational or rotational ego-motion while the vestibular semicircular canals and otoliths report a stationary head, the central nervous system has no consistent model of self-state and generates symptoms ranging from mild eyestrain to acute nausea. A complementary account, **postural-instability theory (姿勢不穩理論)**, proposes that VIMS arises when the visual environment perturbs the postural-control loop in ways the body cannot pre-empt.

For SenseEase the two theories converge on the same operational antecedent: **large-field, low-predictability optical flow without a matching vestibular signal**. Classical triggers include head-mounted VR, IMAX cinema, simulator sickness, and the cybersickness (網路暈動症) literature spanning the past three decades.

**Desktop relevance (WH).** Desktop monitor content has been studied less than VR but is not field-free: high-frame-rate scroll on a 27-inch monitor at typical viewing distance subtends roughly 40 to 50 degrees of visual angle, which is within the regime where **vection (自我運動錯覺)** can be elicited in susceptible individuals. SenseEase's *internal working hypothesis* is that for the VIMS-sensitive desktop user, three recurring stimulus classes — autoscroll on dense feeds, video playback containing camera dolly or shaky-cam, and animated UI carousels — produce sub-acute exposure that accumulates over a session.

### 2.2 Optical Flow and the MAD Measurement Estimator / 光流與 MAD 量測估計器

SenseEase uses a **frame-difference proxy (像素差分代理)** for optical-flow magnitude, computed as the Mean Absolute Difference (MAD, 平均絕對差) between consecutive downsampled greyscale frames captured by the Windows.Graphics.Capture (WGC) provider:

```
MAD(t) = (1 / N) · Σ_i | I_t[i] − I_{t-1}[i] |          // N = 160 × 90 = 14 400 bytes
OFI_raw(t) = MAD(t) × WgcMadNormFactor                 // 4.0, SenseDefaults.cs:102
```

This is implemented in `WgcSensingProvider.ComputeMad` ([WgcSensingProvider.cs:989](src/SenseEase.Infrastructure/Real/WgcSensingProvider.cs)) — a pure `Σ|a[i]−b[i]| / a.Length`. It is a deliberate engineering simplification: the per-pixel L1 difference is a scalar magnitude proxy, not a flow vector field.

**Calibration status (was audit D1 — now PARTIAL real data).** The narrative "idle desktop ≈ MAD 0-1, scrolling video ≈ MAD 15-40" was, in the prior edition, only an inline-comment hypothesis. As of commit `a98cebd` a **real capture** on the developer's hardware (`SENSEEASE_MAD_CALIBRATION=1`, ~457 s) was reduced into `baseline/wgc-mad-calibration.csv`. The measured rows (at the live `normFactor = 4.0`) are:

| scene | mean MAD | p95 MAD | derived OFI mean | derived OFI p95 |
|---|---|---|---|---|
| `idle_desktop` | 0.027 | 0.000 | 0.1 | 0.0 |
| `video_1080p` | 8.528 | 24.668 | 34.1 | 98.7 |

This confirms (a) idle desktop produces essentially zero MAD — well under the noise gate; (b) sustained 1080p video motion lands in the mid OFI band (mean ~34, p95 ~99). **The capture is still partial** — `word_scroll`, `fullscreen_game`, and `scene_cut` were not yet recorded, so the *upper* band and the factor's behaviour against hard cuts remain unvalidated, and `WgcMadNormFactor` stays at `4.0` until a fuller capture is reviewed. The offline reduction/recommendation pipeline (`MadCalibrationReducer`, `NormFactorRecommender` in `src/SenseEase.Core/Sensing/`) is complete, tested, and **advisory only** — it never mutates `SenseDefaults` (see `baseline/M1-CALIBRATION-README.md`). The cursor-velocity companion (`baseline/cursor-velocity-calibration.csv`) remains header-only.

**Why MAD rather than dense flow (WH).** The hypothesis is that for VIMS prediction the *magnitude and duration* of screen-relative motion matters more than its *direction field*. A directional-flow estimator (Farnebäck / learned-flow) is a Beta swap candidate if MAD proves insufficient (§13.6).

### 2.3 Peripheral Attenuation and the Radial Gradient Rationale / 周邊衰減與徑向梯度的設計依據

A central anatomical fact underpins SenseEase's mitigation strategy: peripheral retina (周邊視網膜) is dominated by rod photoreceptors and large-receptive-field magnocellular ganglion cells, which are sensitive to motion and low spatial frequency. The vection literature consistently reports that **peripheral motion is a stronger elicitor of self-motion illusion than foveal motion** at matched stimulus energy.

SenseEase inverts the simulator's trick: if the peripheral field is the disproportionate VIMS-driver, then **attenuating peripheral content while preserving the foveal reading region** should reduce the provocative stimulus without destroying task utility. The implementation is the radial **peripheral attenuation mask** (周邊衰減遮罩) rendered by `OverlayWindow.xaml` (§8).

**Two layers, two clinical roles — load-bearing distinction.**

- **Peripheral attenuation mask** (周邊衰減遮罩) — the continuous, pressure-driven radial dimming. Its role is **comfort / avoidance**: it reduces the provocative peripheral stimulus and relieves symptoms in the moment. Every user gets it; it makes no therapy claim.
- **Chang–Hain anchor grid** (錨定格) — the static stationary-anchoring grid layer (`ApplyGridIntensity`, §8.3.2). Its role is the **graded-exposure / therapy** component: a fixed reference frame against which residual scene motion is judged, consistent with the optokinetic-with-stationary-anchor approach Chang & Hain (2020) describe. It carries the long-term *sensory-reweighting* framing (§2.5).

The mask centre `(cx, cy)` is normalised to `[0, 1]` and produced either by the head-tracking sidecar or hardcoded to `(0.5, 0.5)`.

### 2.4 Leaky Integrator Theory — 1st-Order LTI System / 漏積分器理論（一階 LTI 系統）

The inference layer wraps OFI in a **first-order linear time-invariant (一階線性非時變) discrete leaky integrator (漏積分器) with an upstream noise gate (雜訊閘)**, implemented in `src/SenseEase.Core/Inference/LeakyIntegrator.cs` (the full §6 treatment follows).

The update rule, after gating ([LeakyIntegrator.cs:40-54](src/SenseEase.Core/Inference/LeakyIntegrator.cs)):

```
gated(t)    = OFI_eff(t) ≥ NoiseGateThreshold ? OFI_eff(t) : 0
stimulus(t) = (gated(t) · weight + QuadraticTerm · gated(t)²) · dt
α(dt)       = BaseDecayAlpha ^ (dt / 0.1)        // BaseDecayAlpha = 0.965, SenseDefaults.cs:67
P(t)        = α(dt) · P(t−1) + stimulus(t)       // clamped ≥ 0
```

Three points warrant emphasis:

- **Noise gate.** Sub-threshold flow is mapped to zero, not merely down-weighted. `NoiseGateThreshold = 8` ([SenseDefaults.cs:16](src/SenseEase.Core/Configuration/SenseDefaults.cs)).
- **Quadratic term.** A small `QuadraticTerm · gated²` extra stimulus encodes the *WH* that high-magnitude flow is disproportionately provocative. `QuadraticTerm = 0.0008` ([SenseDefaults.cs:65](src/SenseEase.Core/Configuration/SenseDefaults.cs)).
- **Time-corrected decay.** Decay is re-exponentiated by the inter-sample interval `dt`. The 100 ms anchor `BaseDecayAlpha = 0.965` corresponds to a time constant τ ≈ 2.81 s.

### 2.5 Graded-Exposure Theory, Auto-Profiling, the Strike Model, and the Three Profiles / 漸進暴露理論、自動體質推估、累計次數與三檔體質

**Terminology note (mechanism naming).** The engine type is named `HabituationEngine` and its state fields carry `Habituation*` identifiers, retained to avoid a large code rename. The clinically accurate description of SenseEase's *long-term* mechanism — for the more-susceptible subgroup — is **graded exposure → sensory reweighting** (漸進暴露 → 感覺再加權), not classical habituation. For visually-induced vertigo, recovery is driven mainly by sensory reweighting / adaptation; classical VOR habituation contributes comparatively little.

In the vestibular-rehabilitation tradition, Chang & Hain (2020) describe an optokinetic-stimulation-with-stationary-anchoring approach — the conceptual basis for SenseEase's *anchor grid* (§2.3) — in which a fixed reference frame supports a graded increase in tolerated scene motion, with explicit *protective backoff* when over-exposure produces severe symptoms. (Chang & Hain 2020 is a short theoretical proposal, not a clinical trial — see §14.) The empirical motivation for *not* using an aggressive single-shot exposure protocol comes from Pavlou, Bronstein & Davies (2013): in their randomized trial, the **unsupervised** arm showed roughly 55 % dropout while the supervised arm dropped out at about 10 %. Because SenseEase is an *unsupervised* desktop tool, its design deliberately keeps each exposure step small and protectively gated.

SenseEase encodes these principles as the **graded-exposure offset (習慣化偏移, `_offset`)** + **severe-strike count (重度峰值累計次數)** dual-state system implemented in `HabituationEngine`.

**Auto-profiling replaces explicit calibration (Alpha-2, load-bearing).** The prior edition described a multi-stage Q1–Q5 questionnaire (`CalibrationWindow`), an LLM calibration dialog (`CalibrationChatbotService`), and a Stage-5c demo-video loop (`DemoVideoWindow`). **All of these were deleted** in commit `3131387` ("remove explicit-calibration subsystem"); there is no `CalibrationWindow` type and no `Q1`…`Q5` literals anywhere in `src/`. They are replaced by **auto-profiling** ([MockInferenceEngine.ApplyAutoProfilingStartDefaults](src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs), `:682-698`):

- A genuinely fresh user (guard: `!_profileExistedAtLoad && _loadedCalibrationCount == 0 && !_vestibularProtection`) is seeded at **level 5** (`SenseDefaults.AutoProfilingStartLevel = 5` — the Set0 median「中」) on the **ADAPTING** profile (`SetProfile(HabituationProfile.Adapting, "auto_profiling_default")`; formerly TYPICAL — see the B6 Stage 4a rename in `HABITUATION-PROFILE.md`).
- The engine then adapts the user's curve from their own behaviour via the Adaptive-Fit selector (§6a).
- **Vestibular safety is re-rooted** onto a single opt-in prompt (`SetVestibularProtection`, [MockInferenceEngine.cs:708-735](src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs)) rather than a calibration branch: when enabled it applies `EffectiveTrigger = 50` + strike-limit 1, persisted in `HabituationProfileSnapshot.VestibularProtectionEnabled` and surfaced as both a one-time non-blocking prompt and a permanent Settings toggle (§9a).

`CalibrationProcessor.cs` / `CalibrationTypes.cs` survive only as a **constants holder + type definitions** (`VestibularTriggerPressureOverride = 50`, `PersonalPressureScaleMin/Max`) reused by the vestibular path; `CalibrationProcessor.Process` and `ApplyCalibrationResult` have **no live caller** in `src/`.

**Three profiles** still stratify the habituation *cadence*. Verified against [HabituationProfile.cs:138-175](src/SenseEase.Core/Inference/HabituationProfile.cs):

| Profile | Clinical analogue | StableWindowMs | StepIntervalMs | StepMagnitudePp | HabOffsetCapPp | MinSamplesForFullWeight | SevereStrikeLimit |
|---|---|---|---|---|---|---|---|
| **RESILIENT** (耐受型) | normative-tolerance user | 120 000 | 30 000 | 2.0 | 30 | 3 | 2 |
| **TYPICAL** (一般型) | population-median user | 180 000 | 45 000 | 1.5 | 25 | 4 | 2 |
| **SENSITIVE** (敏感型) | clinically reactive user | 360 000 | 60 000 | 1.0 | 15 | 6 | 1 |

The progression is monotonic and reflects the **WH** that more sensitive users need (a) longer evidence of comfort before relaxing protection, (b) longer cooldown between relaxation steps, and (c) smaller per-step relaxations. **The auto-profiling default is now TYPICAL** (the population median), not RESILIENT — a fresh user starts at the middle of the population rather than the most-tolerant end.

**Top-of-scale behaviour is NOT profile-differentiated.** `GetPostSpikePauseMs` returns `requireUserConfirm = true` for **any** profile at the top band; all three profiles enter `PausedAwaitingUserConfirm` and stay there until `AcknowledgeAndResume`.

**Audit gap (A7) — shader axes are now PARTIALLY profile-differentiated via the Adaptive-Fit Band axis.** Per-profile *shader defaults* (`MaskDepth`, `GridVisibility`, `FeatherWidth`) remain global, BUT the Adaptive-Fit **Band** axis (§6a) now deforms the per-set mask-alpha envelope and FOV floor, so a user the selector has moved to a higher Band reaches a deeper peak mask than a lower-Band user. The original A7 concern (a SENSITIVE user having an identical *peak mask* to a RESILIENT user) is therefore mitigated *through the selector* rather than through per-profile shader constants. Direct per-profile shader defaults remain a candidate (§13.6).

### 2.6 Head-Pose Viewport Rationale / 頭部姿勢視口設計依據

The head-tracking module addresses foveal targeting by driving `(cx, cy)` from a derived head-pose estimate produced by the MediaPipe FaceLandmarker running in a Python sidecar. It is opt-in (`AppConfig.HeadTrackingEnabled`, default false).

**OBS-3 hard rules — quoted in full.**

> **OBS-1.** No raw camera frame, no encoded video, no still image and no per-frame thumbnail crosses the IPC boundary.
>
> **OBS-2.** No raw FaceLandmarker output (478 landmark coordinates, blendshape coefficients, transformation matrices) crosses the IPC boundary. Only the *derived scalars* are published.
>
> **OBS-3.** No camera frame, landmark or scalar is ever written to disk by the sidecar except a single rolling text log that records sidecar lifecycle.

The IPC schema is the eight-field `SidecarHeadPoseReader.SidecarFrameDto` (`t_ms`, `cx`, `cy`, `luma`, `face_detected`, `face_present`, plus debug-only `yaw_deg`, `pitch_deg`) — all scalar / boolean; no landmarks, blendshapes, or pixel data. The OBS-3 allowlist is now pinned by a reflection-based schema test (was audit E3 — CLOSED).

There are two follow-mode policies the user can choose: **Balanced** (mask centre clamped to `[0.10, 0.90]`) and **Full follow** (1:1). The **feathering halo fix** (§8.5) is the load-bearing companion to head-follow: without it, head motion makes the mask's soft edge "breathe" and produces a halo/comet-tail.

### 2.7 Efference-Copy and the Driver–Passenger Asymmetry / 傳出複本與駕駛–乘客不對稱

SenseEase carries the driver-passenger asymmetry into desktop interaction. The `Mode` field on `SenseState` is either `"Driver"` or `"Passenger"`.

The concrete weights, verified at [MockInferenceEngine.cs:366](src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs) and `:377`:

- Driver mode: `effectiveOfi = opticalFlowIndex × DriverFlowAttenuation (0.25)`, `weight = 1.0` → net stimulus contribution multiplier **0.25**.
- Passenger mode: `effectiveOfi = opticalFlowIndex × 1.0`, `weight = PassiveFlowWeight = 2.0` ([SenseDefaults.cs:64](src/SenseEase.Core/Configuration/SenseDefaults.cs)) → net stimulus contribution multiplier **2.0**.

The Passenger / Driver ratio is therefore **8×** (2.0 / 0.25). `DriverFlowAttenuation` is now a named `SenseDefaults` constant ([:77](src/SenseEase.Core/Configuration/SenseDefaults.cs)) (was audit D2 — promoted). The 8× ratio remains an **internal working hypothesis (WH)** asserted qualitatively by `InferenceTests.PassengerMode_BuildsPressureFasterThanDriverMode`; a magnitude assertion is still open (§13.6).

### 2.8 Comfort-Level Classification Rationale — the 12-level Native Scale and the 動態感 Bands / 舒適度分級：12 級原生量表與動態感帶位

**The engine source of truth is now a native 1–12 integer level (Adaptive-Fit Q2=B)**, not 1–10. It is derived from `Pressure(t)` with hysteresis (§6.4). The 12-level scale is the engine granularity but is *not* the UI surface.

**The Grey/Amber/Red 3-band model has been retired.** The user-facing surface is a **5-band「動態感 (motion-feel)」label** ([TierColorPalette.BandForTier](src/SenseEase.App/Views/TierColorPalette.cs), `:280-287`):

| Level | `TierBand` enum | User label (動態感) |
|---|---|---|
| 1–2 | `VeryHigh` | 極低 / Very Calm |
| 3–4 | `High` | 低 / Calm |
| 5–7 (default 5) | `Medium` | 中 / Moderate |
| 8–10 | `Low` | 高 / Active |
| 11–12 | `VeryLow` | 極高 / Very Active |

**Note the deliberate enum-polarity inversion:** enum `VeryHigh` (L1–2) is the *calmest* screen state (most natural, least masking), surfaced to the user as「動態感·極低」. The palette is a **calm cool-blue ramp** (`#9BB4D4`→`#243F6B`, [TierColorPalette.cs:56-60](src/SenseEase.App/Views/TierColorPalette.cs)) — there is intentionally **no alarm-red**, because an anxiety-coloured "you are getting sick" signal is itself provocative for this population.

A **legacy 3-band token** survives only as a colour-DataTrigger token (never a user label), rescaled to the 12-level boundaries ([ComfortBands.For](src/SenseEase.App/Converters/TierComfortConverters.cs), `:34-46`): `Idle` (≤0) / `Steady` (L1–4) / `Rising` (L5–7) / `High` (L8–12). This is the closest descendant of the old Grey/Amber/Red.

**小結（中文）：** 本節將各架構承諾錨定於外部概念。重大變更：(1) 引擎原生量表由 1–10 改為 **1–12**，UI 改以五段「動態感」冷藍色階呈現、刻意不用警示紅；(2) **取消顯式 Q1–Q5 校準**，改為自動體質推估（新用戶 L5／一般型起步）＋前庭保護單一同意提示；(3) Adaptive-Fit 的 Band 軸現已部分實現分檔的遮罩深度，部分緩解原 A7 缺口。

---

## 3. System Architecture / 系統架構

SenseEase is structured as a layered Windows desktop application in C# (.NET) with two out-of-process auxiliaries.

### 3.1 End-to-End Data Flow / 端到端資料流

1. **Capture.** `WgcSensingProvider` at ~15 Hz captures a 160×90 greyscale downsample. `CursorVelocityProvider` at ~30 Hz polls `GetCursorPos`. `UserActivityModeDetector` at 1 Hz polls `GetLastInputInfo`. An optional head-tracking sidecar publishes its scalar tuple.
2. **Sample emission.** Each sensing provider emits `OpticalFlowSample(OFI, sampledAtUtc)` events.
3. **Inference.** `MockInferenceEngine.Update(opticalFlowIndex, mode, sampledAt)` applies driver attenuation (×0.25), the Passenger-mode mask-attenuation feedback (×0.45→0.15), the leaky-integrator step, level hysteresis, FOV-radius composition, mask hysteresis, and habituation strike/offset bookkeeping. With the selector flag ON it also runs the Adaptive-Fit tick (§6a) and writes a per-set mask-depth override.
4. **State propagation.** `SenseState` is exposed via `MainViewModel` to all visible UI surfaces.
5. **User-Model collection.** `MainViewModel.SampleBehavior` folds each tick into a `DailyBehaviorAccumulator`, flushed every 60 s to `behavior_log.jsonl` (§11a).
6. **Auxiliary channels.** The chatbot subscribes to `SenseState` for the `[habituation_state]`, `[engine_state]` and `[behavior_trend]` context blocks; a background retrospective analyzer mines chat history into the user model 2 minutes after launch.

### 3.2 Process Topology / 程序拓撲

- **`SenseEase.exe`** (main WPF process) hosts all in-process layers.
- **`llama-server.exe`** (side-loaded Vulkan backend, llama.cpp b9294) listens on **`127.0.0.1:8181`**, **lazily started on first chat** (§10.2).
- **Head-tracking sidecar** (`head_tracker.exe`, PyInstaller one-dir, portable) wraps MediaPipe FaceLandmarker; JSON-over-stdout at ~15 Hz.

Typical sidecar IPC line (eight-field DTO):

```
{"t_ms":1717459200123,"cx":0.512,"cy":0.487,"luma":0.41,"face_detected":true,"face_present":true,"yaw_deg":-2.1,"pitch_deg":1.4}
```

Heartbeat semantics: one line per FaceLandmarker tick. If no line arrives within 2 seconds, the source is marked stale; a watchdog detects the "sidecar spawns but emits 0 frames" failure (commit `1fee12d`).

### 3.3 Layer Responsibility Summary / 各層職責摘要

| Layer | Project | Owns | Forbidden (禁止項) |
|---|---|---|---|
| Core | `SenseEase.Core` | abstractions, pure-logic processors, parameter defaults, **Adaptive-Fit selector**, **SafetyFloor**, **User-Model DTOs** | I/O, Win32, WPF, HTTP |
| Infrastructure | `SenseEase.Infrastructure` | sensing providers, persistence (engine/profile/user-model stores), chatbot HTTP, sidecar IPC, mock engine | UI, ViewModels |
| App | `SenseEase.App` | composition root, ViewModels, applicators, event log, behaviour collector | direct Win32 |
| Views/UI | `SenseEase.App` (Views folder) | WPF windows, XAML, code-behind for input plumbing only | engine writes |

---

## 4. Project Structure & Dependency Graph / 專案結構與相依關係

### 4.1 Solution Layout / 方案佈局

```
SenseEase APP/
├── src/
│   ├── SenseEase.Core/                    # abstractions, pure logic
│   │   ├── AdaptiveFit/                    # NEW (Alpha-2): 50-set selector
│   │   ├── Safety/                         # NEW (Alpha-1): SafetyFloor.cs
│   │   ├── Sensing/                        # NEW (M1): MAD calibration pipeline
│   │   ├── Inference/                      # leaky integrator, habituation, lexicons
│   │   └── Models/                         # SenseState, User-Model DTOs
│   ├── SenseEase.Infrastructure/
│   │   └── Storage/                        # NEW: user-model + profile stores
│   ├── SenseEase.App/
│   └── tests/SenseEase.Tests/             # ~1058 / 0 / 3
├── docs/
├── tools/head-tracker/                    # vendored portable Python sidecar
├── installer/                             # Inno Setup .iss (deleted in working tree; see §13.1)
├── baseline/                              # perf + diag + M1 OFI calibration data
└── face_landmarker.task                   # MediaPipe model asset
```

### 4.2 Dependency Graph / 相依關係圖

```
                       ┌──────────────────────┐
                       │   SenseEase.Tests    │
                       └─────────┬────────────┘
                                 │ (references all three)
        ┌────────────────────────┼────────────────────────┐
        ▼                        ▼                        ▼
┌───────────────┐        ┌──────────────────┐       ┌──────────────────────┐
│ SenseEase.App │───────▶│  SenseEase       │──────▶│  SenseEase.Core      │
│ (Views + VMs) │        │  .Infrastructure │       │  (abstractions +     │
└───────┬───────┘        └────────┬─────────┘       │  AdaptiveFit + Safety)│
        │                         │                 └──────────────────────┘
        └─────────────────────────┴──────────────────────────┘
                            (App → Core direct)
```

### 4.3 External Dependencies / 外部相依

| Package | Version | Consumer | Purpose |
|---|---|---|---|
| `Microsoft.Windows.SDK.Contracts` / `CsWin32` | 10.0.x / 0.3.275 | Infrastructure | WGC / WinRT / Win32 P/Invoke |
| `LLamaSharp` | 0.27.0 | `MistralChatbotService` | managed API (CUDA backend native assets **excluded**, §10.2) |
| `Hardcodet.NotifyIcon.Wpf` | 2.0.1 | App | tray icon |
| `xUnit` | 2.9.2 | Tests | test runner |

### 4.4 Test Project Layout / 測試專案佈局

The ~1058 / 0 / 3 split is reproducible by `dotnet test` from the solution root (one known `UserPreferences` test-isolation flake — passes in isolation).

---

## 5. Sensing Layer Deep-Dive — the OFI Path / 感測層深度說明 — OFI 路徑

This section and §6 carry the document's primary technical weight.

### 5.1 Overview / 概述

`WgcSensingProvider` is the sole `ISensingProvider` implementation — WGC is the only sensing source (the Phase 2.1 `CursorVelocityProvider` and the `MockSensingProvider` demo path were retired in D-9). A helper — `UserActivityModeDetector` — runs orthogonally to classify Driver/Passenger.

### 5.2 WgcSensingProvider — WGC 螢幕擷取感知器

`WgcSensingProvider` is the default sensor since Path Z UX iter 3 (2026-05-27).

#### 5.2.1 Pipeline / 管線

1. WGC session captures primary monitor at native resolution.
2. Each frame is copied into a CPU-readable D3D11 staging texture.
3. Step-sampled into a **160 × 90 greyscale buffer** (`N = 14 400`).
4. Greyscale conversion uses integer BT.601: `(19·B + 150·G + 77·R) >> 8` ([WgcSensingProvider.cs:908](src/SenseEase.Infrastructure/Real/WgcSensingProvider.cs)).
5. `rawMad = ComputeMad(current, prev)` (`:725`); `rawOfi = rawMad × WgcMadNormFactor (4.0)` (`:726`).
6. Raw OFI passed through a **3-tap running median** (`PushAndMedian`, `:1002`) to reject 1–2-frame outliers (scene cuts, cursor flicks).
7. Clamped to `[0, 150]` (`:751`) and **EMA-smoothed** with `α = CursorEmaAlpha = 0.55` (`:753-754`).

The exact OFI emission is therefore:

```
OFI(t) = EMA_0.55( clamp( median₃( MAD(t) × 4.0 ), 0, 150 ) )
```

#### 5.2.2 Cadence / 取樣頻率

`WgcCaptureIntervalMs = 66 ms` ≈ 15 Hz — one capture per two display frames at 60 Hz ([SenseDefaults.cs:96](src/SenseEase.Core/Configuration/SenseDefaults.cs)).

#### 5.2.3 Exclude-rect Mask / 排除矩形遮罩

`SetMonitorExcludeRect(left, top, width, height)` accepts a monitor-coordinate rectangle. During the downsample pass, cells corresponding to that rect are zeroed in **both** the current and previous frame buffers, so the always-on-top ambient bar's own pixels never register as motion.

#### 5.2.4 Env-gated Tracing / 環境變數開關的追蹤紀錄

`SENSEEASE_WGC_BAR_VALIDATION=1` opens `%TEMP%/wgc_ofi_trace.txt`. `SENSEEASE_MAD_CALIBRATION=1` opens a per-tick **raw-MAD** trace (`%TEMP%/wgc_mad_calibration_trace.csv`, before the norm-factor multiply) — the M1 capture path (§5.2.6). Both are read once at `Start()`; toggling requires a restart.

#### 5.2.5 Clause-5 Diagnostic Writes / clause-5 診斷寫入

All diagnostic output goes through `WgcDiagLog` (§5.9). WGC timestamps are UTC ISO-8601 (H41).

#### 5.2.6 M1 OFI Calibration — real data / M1 OFI 校準（實測資料）

The offline calibration pipeline lives in `src/SenseEase.Core/Sensing/` (`MadCalibrationReducer`, `NormFactorRecommender`) and mirrors the live formula exactly (`mad × normFactor`, clamp `[0,150]`). A real ~457 s capture (idle + 1080p video) was reduced into `baseline/wgc-mad-calibration.csv` (table in §2.2). The recommender is **advisory only**; the factor stays at `4.0` until a fuller capture (`word_scroll` / `fullscreen_game` / `scene_cut`) is reviewed and the constant edited by hand. This partially closes audit D1 (the reducible half is done with real data; the upper-band validation remains open).

### 5.3 Retired sensing paths — Cursor / Demo（D-9 已退役）

There is no fallback sensor: WGC is the sole sensing source. The Phase 2.1 `CursorVelocityProvider` (Win32 `GetCursorPos` at 30 Hz) and the `MockSensingProvider` demo path (externally `Push`-driven OFI) were **retired in D-9** (FEAT-11/DEAD-03) and their source files deleted. When WGC sensing fails permanently there is no provider to fall back to — the failure surfaces through the **Faulted** liveness state (§D3 liveness contract), which the bar/tray/card fan-out reflects to the user, rather than silently degrading to a secondary sensor.

### 5.5 Sensing-mode Selection / 感知模式選擇

```csharp
public enum SensingMode { Wgc, }
```

D-9 collapsed the enum to its single `Wgc` member; `AppConfig.Mode` stays pinned to `Wgc` (no runtime switch remains). (The Phase 2.1 → 2.2 default-flip from `Cursor` to `Wgc` is now moot — `Wgc` is the only option.)

### 5.6 OFI Value Semantics / OFI 值語意

- **`SenseState.OpticalFlowIndex`** — post-attenuation effective value (after Driver ×0.25 / Passenger feedback).
- **`SenseState.RawOpticalFlowIndex`** — raw sensor OFI before any engine processing (this is what the User-Model `behavior_log` records).

### 5.7 UserActivityModeDetector / 使用者活動模式偵測器

Classifies the user as **Driver** or **Passenger**.

- **Mechanism.** Polls Win32 `GetLastInputInfo` at 1 Hz.
- **Asymmetric hysteresis.** Passenger → Driver when `idleMs ≤ DriverThresholdMs = 2500` (fast attack); Driver → Passenger when `idleMs ≥ PassengerThresholdMs = 5000` (slow release).
- **Typing override.** Consults `WindowsActivityFilter.Instance.SuppressionReason`; if `"typing"`, force `"Driver"`.
- **Auto-mode master switch.** `AppConfig.AutoDriverPassengerModeEnabled` (default `true`).

### 5.8 Efference-Copy Weighting / 傳出複本權重

```csharp
double effectiveOfi = isDriverMode ? (opticalFlowIndex * SenseDefaults.DriverFlowAttenuation) : opticalFlowIndex; // :366
double weight       = isDriverMode ? 1.0 : SenseDefaults.PassiveFlowWeight;                                       // :377
```

#### 5.8.1 Mask-Attenuation Feedback Loop / 遮罩衰減回饋環路

[MockInferenceEngine.cs:369-374](src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs), **only when the user is in Passenger mode AND the mask is already active**:

```csharp
if (!isDriverMode && Current.MaskActive)
{
    double tightness = ComputeMaskTightness(Current.FovRadiusPercent);
    double attenuation = 0.45 - (tightness * 0.30); // 0.45 -> 0.15
    effectiveOfi *= Math.Clamp(attenuation, 0.15, 0.45);
}
```

`ComputeMaskTightness` ([:1790-1794](src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs)):

```
span      = max(1, OuterTransitionRadiusPercent − DefaultFovRadiusPercent)   // max(1, 45−35) = 10
tightness = clamp((OuterTransitionRadiusPercent − fovRadiusPercent) / span, 0, 1)
```

This is a closed-loop damping that reduces ping-pong oscillation around the trigger; it has **no direct visual effect** (the gradient shape is fixed in `OverlayWindow`). The `!isDriverMode` gate is load-bearing — in Driver mode the ×0.25 already dominates and stacking mask attenuation would drive effective OFI to `0.25 × 0.15 = 0.0375×` and make the engine unresponsive.

### 5.9 WgcDiagLog — clause-5 設計

Shared sink for `%TEMP%/wgc_diag.txt`: (1) `Append(msg)` retries up to 3 attempts on `IOException` with 5 / 10 ms back-off; (2) every line is prefixed with a monotonic `[N=xxxxxxx]` sequence (gap reveals silent loss); (3) when all retries fail, the failure event is recorded to a **separate** `%TEMP%/wgc_diag.lost.log`. (Option 2, a single-writer queue, deferred to v1.0.)

### 5.10 Sensing-layer Summary / 感測層小結

| Component | Cadence | Output | Key constant |
|---|---|---|---|
| `WgcSensingProvider` | 15 Hz | `EMA₀.₅₅(clamp(median₃(MAD×4.0),0,150))` | `SenseDefaults.cs:96-102` |
| `CursorVelocityProvider` | 30 Hz | `EMA₀.₅₅(clamp(\|Δp\|/Δt / 120.0,0,150))` | `SenseDefaults.cs:89-93` |
| `MockSensingProvider` | external push | OFI as provided | — |
| `UserActivityModeDetector` | 1 Hz | Driver / Passenger | `UserActivityModeDetector.cs:51-53` |

**小結（中文）：** OFI 經 MAD ×4.0 → 3-tap 中位數 → clamp[0,150] → α=0.55 EMA。Driver ×0.25、Passenger ×2.0 達成 8× 不對稱。MAD→OFI 比例已有首批實測資料（idle 與 1080p 影片）部分驗證 normFactor=4.0，上段（遊戲／硬切）尚未擷取。

---

## 6. Inference Layer Mathematics — the VIMS Pressure Formula / 推論層數學 — VIMS 壓力公式

This is the engine's mathematical core.

### 6.1 The Update Rule / 更新規則

The pressure scalar `P(t)` is produced by `LeakyIntegrator.Step(effectiveOfi, weight, dt)` ([MockInferenceEngine.cs:378](src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs); [LeakyIntegrator.cs:40-54](src/SenseEase.Core/Inference/LeakyIntegrator.cs)):

```
gated(t)    = OFI_eff(t) ≥ NoiseGateThreshold ? OFI_eff(t) : 0
stimulus(t) = (gated(t) · weight  +  QuadraticTerm · gated(t)²) · dt
α(dt)       = BaseDecayAlpha ^ (dt / 0.1)
P(t)        = max(0,  α(dt) · P(t−1) + stimulus(t))
```

where `OFI_eff` is the mode-attenuated OFI from §5.8 and `dt` is the real inter-sample interval, clamped `[0.001, 2.0]` s (or `1.0` for unit-step/test usage).

| Symbol | Constant | Value | Source |
|---|---|---|---|
| `NoiseGateThreshold` | `NoiseGateThreshold` | **8** | [SenseDefaults.cs:16](src/SenseEase.Core/Configuration/SenseDefaults.cs) |
| `BaseDecayAlpha` | `BaseDecayAlpha` | **0.965** (τ ≈ 2.81 s) | [SenseDefaults.cs:67](src/SenseEase.Core/Configuration/SenseDefaults.cs) |
| `QuadraticTerm` | `QuadraticTerm` | **0.0008** | [SenseDefaults.cs:65](src/SenseEase.Core/Configuration/SenseDefaults.cs) |
| `PassiveFlowWeight` | `PassiveFlowWeight` | **2.0** (Passenger only) | [SenseDefaults.cs:64](src/SenseEase.Core/Configuration/SenseDefaults.cs) |
| `DriverFlowAttenuation` | `DriverFlowAttenuation` | **0.25** (Driver only) | [SenseDefaults.cs:77](src/SenseEase.Core/Configuration/SenseDefaults.cs) |
| `TriggerPressure` | `TriggerPressure` | **85** | [SenseDefaults.cs:17](src/SenseEase.Core/Configuration/SenseDefaults.cs) |

### 6.2 Time-Corrected Decay Derivation / 時間校正衰減推導

τ = −0.1 / ln(0.965) ≈ **2.81 s**. A 30 Hz sample (`dt ≈ 0.033`) decays by `0.965^0.33 ≈ 0.988` per tick; a 15 Hz sample (`dt ≈ 0.066`) decays by `0.965^0.66 ≈ 0.977` per tick. The `^(dt/0.1)` re-exponentiation keeps the time constant invariant across the variable WGC/cursor sample rates — a 15 Hz and a 30 Hz provider accumulate to the **same equilibrium**.

### 6.3 Equilibrium Pressure — Worked Example / 平衡壓力範例

For a sustained gated input `g` at weight `w`, the fixed point of the update is:

```
P*  =  (g·w + q·g²) · dt  /  (1 − α(dt))
```

Worked example, Passenger mode, `g = 30`, `w = 2`, `q = 0.0008`, `dt = 0.066`:

- `g·w = 60`; `q·g² = 0.72`
- Stimulus/tick: `(60 + 0.72) · 0.066 ≈ 4.008`
- `1 − α(0.066) ≈ 0.0231`
- **P\* ≈ 173.5** — comfortably above `TriggerPressure = 85`; the transient to equilibrium takes roughly `5τ ≈ 14 s`.

Compare Driver mode at the same `g = 30`: `effectiveOfi = 30 · 0.25 = 7.5`, **below the noise gate of 8**, so `gated(t) = 0` and `P* = 0`. This is the efference-copy asymmetry in action: identical screen motion builds pressure for an idle viewer but not for the person driving it.

*(This derivation lives as prose; a `LeakyIntegratorConvergenceTests` fixture asserting the analytic equilibrium, rate-invariance, and the 5τ rule of thumb remains an open coverage item — §13.6 D3.)*

### 6.4 Level Projection — the Native 12-Level Scale / 等級投影 — 12 級原生量表

**The level projection is now a closed-form affine map onto 1–12** (was 1–10), in `UpdateVimsTier` ([MockInferenceEngine.cs:1823-1834](src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs)):

```
t      = clamp(P / TriggerPressure, 0, 1)
scale  = adaptiveFitEnabled ? SetGrid.Curve(activeSet).ForLevel(level).Scale
                            : (SetCurve.LevelCount − 1)            // 12 − 1 = 11.0
levelF = 1.0 + scale · t                                          // 1 .. 12
VimsTier = round(levelF)   // with hysteresis from §7.1, clamped [1, 12]
```

`SetCurve.LevelCount = 12`. With the default discretizer gain `scale = 11.0`, the map is **`levelF = 1 + 11·t`**:

- `P = 0` → level 1.
- `P = TriggerPressure = 85` → level 12 (saturates).
- `P > 85` → clamp pins `t = 1.0`; the integrator can sit far above 85 (FOV keeps tightening up to `1.6 × TriggerPressure = 136`, §8.2) but the **level** signal saturates at 12 the moment pressure crosses 85.
- Mid-points: `P ≈ 42.5` → level ~6.5; `P ≈ 17` → level ~3.2.

**Per-set discretizer `Scale` (commit `bdf193b`).** When the selector flag is ON, `scale` is read from the active set's curve (`LevelParams.Scale`) instead of the hardcoded `11.0`. **Today the value is a uniform `11.0` for all 50 sets** (`SetGrid.DiscretizerScale = 11.0`) — the 12-rung span is deliberately locked; higher Bands scale the *envelope* (mask alpha + FOV floor), not the discretizer gain. The wiring is plumbed so a future calibration *could* deform the pressure→level mapping per set without code change.

There is **no separate "level → FOV radius" lookup table**: the discrete `VimsTier` drives the visual mask colour/alpha lookup in `OverlayWindow.VimsLevels[]`, while FOV radius is a continuous pressure-driven mapping (§8.2).

### 6.5 EffectiveTrigger, Personal Pressure Scale, and the DisplayStressPercent Hero Metric / 有效觸發、個人壓力比例與壓力百分比

**Personalisation is applied to a *threshold*, not to the raw pressure** — sensor readings stay pristine. `EffectiveTrigger` ([HabituationEngine.cs](src/SenseEase.Core/Inference/HabituationEngine.cs)) is:

```
EffectiveTrigger = TriggerPressure / personal_pressure_scale          // (+ absolute override when set)
```

with `personal_pressure_scale ∈ [0.7, 1.5]` (`SenseDefaults.PersonalPressureScaleMin/Max`):
- `scale = 1.0` → `EffectiveTrigger = 85` (default).
- `scale = 0.7` → `EffectiveTrigger ≈ 121.4` (less sensitive user — must cross more pressure).
- `scale = 1.5` → `EffectiveTrigger ≈ 56.7` (more sensitive user — triggers sooner).

**Vestibular override precedence.** When vestibular protection is active, the absolute override wins: `EffectiveTrigger = VestibularTriggerPressureOverride = 50` and strike-limit hard-set to 1 ([MockInferenceEngine.cs:708-735, 271-276](src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs)). This is the strictest envelope (41 % lower trigger than default).

**Scope qualifier (load-bearing).** `EffectiveTrigger` is consumed by **four** sites only:
1. the habituation spike-recurrence check (`pressure ≥ EffectiveTrigger × 0.60`),
2. the habituation calm-fraction check (`pressure < EffectiveTrigger × 0.25 && !maskActive`),
3. the Adaptive-Fit stats builder (`PressureP90 = pct(Pressure / EffectiveTrigger)`),
4. the **`DisplayStressPercent` hero metric** — the personalised 0–100 % readout shown on the dashboard ([MockInferenceEngine.cs:391-394](src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs)):
   ```
   DisplayStressPercent = clamp(100 · Pressure / EffectiveTrigger, 0, 100)
   ```

It is **NOT** used by the mask hysteresis (§7.3), `ComputeFovRadiusPercent` (§8.2), or `UpdateVimsTier` (§6.4) — those still read **raw `TriggerPressure` (85)**. So under `scale ≠ 1.0`, mask engagement / FOV radius / engine level use the raw 85 threshold, while the user's *displayed* stress %, the habituation strike/calm logic, and the selector's stats are personalised. This asymmetry is design intent ("the mask is a fixed physical protection; only the user's *experience of urgency* is personalised") but is load-bearing — wiring `EffectiveTrigger` into the mask hysteresis would silently change every `scale ≠ 1.0` user's masking behaviour.

### 6.6 Habituation Offset Arithmetic / 習慣化偏移算術

**`_offset` is added to the FOV radius percent**, NOT subtracted from the trigger pressure. `MockInferenceEngine.ComposeFovRadius` ([:1743-1745](src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs)):

```
FOV_radius = clamp(ComputeFovRadiusPercent(P) + _userFovOffset + _habituation.HabituationOffset, 15, 100)
```

`_offset ∈ [0, HabOffsetCapPp]` percent-points of FOV radius. A positive `_offset` **widens** the clear zone (re-habituation = exposing the user to more visual stimulus). It never touches the spike trigger.

**Personalisation confidence.** `MinSamplesForFullWeight` is used **only** as the denominator of a linear confidence ramp in `HabituationEngine.SetPersonalization`:

```
confidence = clamp(fovHistory.Count / MinSamplesForFullWeight, 0, 1)
caution    = clamp(−fovHistory.MeanDelta / 15.0, 0, 1) × confidence       // 15.0 = HabituationCautionMeanDeltaDivisor
_effectiveWindow       = floor + caution × (2 × floor − floor)
_effectiveStepInterval = stepFloor + caution × (HabituationMaxStepIntervalSeconds − stepFloor)   // ceiling 75 s
```

Only *tightening* history (negative MeanDelta) drives caution up; relaxing users get caution = 0.

---

## 6a. The Adaptive-Fit Selector / 自適應擬合選擇器 (Alpha-2)

The Adaptive-Fit selector is the Alpha-2 mechanism that personalises a user's response curve from *their own* behaviour — a deterministic, single-user, on-device estimator (no collaborative filtering, no telemetry upload). It lives in `src/SenseEase.Core/AdaptiveFit/` (pure Core). Enabled by `AppConfig.AdaptiveFitSelectorEnabled = true`.

### 6a.1 The 50-set grid / 50 組擬合格

A **set** is one cell in a **10 × 5 = 50-set grid** of `(Band, Shape)` coordinates ([GridCoord.cs](src/SenseEase.Core/AdaptiveFit/GridCoord.cs)):

- **Band** ∈ 0–9 (intensity axis). Higher Band ⇒ deeper mask + tighter FOV floor.
- **Shape** ∈ 0–4 (response-shape tag). Per resolved decision Q4, Shape is **render-only** in this foundation — it does **not** modulate the LeakyIntegrator decay and carries no numeric curve effect yet; it is the axis the selector moves along.
- **Set0 = (Band 5, Shape 2)** is the median set whose 12-level curve reproduces the pre-selector engine **byte-for-byte** (`PeakMaskAlpha == VimsLevels[L−1].Alpha`, `FovFloorPercent == 15`, `Scale == 11.0`, `GridVisibility == 0.75`).

Adjacency is **von-Neumann (4-connected, no diagonals)** — every candidate is Manhattan distance exactly 1 from the current set.

Each set carries a 12-rung `LevelParams` curve `{ PeakMaskAlpha, FovFloorPercent, Scale, GridVisibility }` ([SetGrid.cs](src/SenseEase.Core/AdaptiveFit/SetGrid.cs)). Provisional Band deformation (until M1 anchors the OFI norms): `alphaMultiplier = 1.0 + 0.06·(band−5)` clamped to `[0.20, 0.92]` with **L1 always pinned to 0.00**; FOV floor tightens 1 pp per Band above 5, never below 15. Shape currently deforms nothing.

### 6a.2 The features (UserIntensityStats) / 特徵向量

Built by `IntensityStatsBuilder.Build` from the user's own **7-day durable event log** ([UserIntensityStats.cs](src/SenseEase.Core/AdaptiveFit/UserIntensityStats.cs)):

- `PressureP90` = 90th percentile of `(Pressure / EffectiveTrigger)`.
- `SpikeRecurrencePerHour` = spike-category log entries ÷ window hours.
- `CalmRatio` = fraction of normalised-pressure samples below the stable-pressure fraction.
- (`FmsAggregate`, `PressureP50`, `SampleCount` also computed; `FmsAggregate` reserved.)

### 6a.3 The score function — confirmed against code / 評分函數

[DeterministicSetSelector.Score](src/SenseEase.Core/AdaptiveFit/DeterministicSetSelector.cs) (`:144-148`):

```
score(candidate) = W1·fit(pressureNorm, bandTarget) + W2·fit(spikeNorm, shapeTarget) + W3·fit(calmNorm, 1−bandTarget)
                 − W4·ManhattanDistance(current, candidate) − W5·manualConflict

fit(x, target) = 1 − clamp(|x − target| / FitTolerance, 0, 1)
```

- `pressureNorm = clamp01(PressureP90 / 2.0)`; `spikeNorm = clamp01(SpikeRecurrencePerHour / 6.0)`; `calmNorm = clamp01(CalmRatio)`.
- Cell targets: `BandToNorm = band / 9`; `ShapeToNorm = shape / 4`.
- More calm pulls toward a *lower* Band (the `1 − bandTarget` term).
- `manualConflict ∈ {0, 1}` = 1 when the candidate's Band move opposes the sign of a recent manual level nudge (user authority defers the auto-move).

**Confidence gate.** A `MoveTo` fires only when *all* hold: (1) `SampleCount ≥ SelectorMinSamplesBeforeAnyMove (12)`, (2) argmax ≠ current cell, (3) `bestScore − holdScore ≥ SelectorConfidenceMargin (0.15)`. Otherwise `Hold`.

| Weight / gate | Value | Source |
|---|---|---|
| `SelectorW1Pressure` | 1.0 | [SenseDefaults.cs:188](src/SenseEase.Core/Configuration/SenseDefaults.cs) |
| `SelectorW2Spike` | 0.6 | `:191` |
| `SelectorW3Calm` | 0.6 | `:194` |
| `SelectorW4Distance` | 0.1 | `:199` |
| `SelectorW5ManualConflict` | 2.0 | `:204` |
| `SelectorConfidenceMargin` | 0.15 | `:208` |
| `SelectorFitTolerance` | 0.7 | `:217` |
| `SelectorMinSamplesBeforeAnyMove` | 12 | `:221` |

The whole region is documented as **provisional / deliberately conservative** until M1 anchors the OFI norm-factors — the selector should prefer Hold over chasing noise.

### 6a.4 State machine + 24-hour propose-confirm / 狀態機與 24 小時提案確認

[AdaptiveFitStateMachine.cs](src/SenseEase.Core/AdaptiveFit/AdaptiveFitStateMachine.cs) governs *when* a move may fire: `Default → Settling → Active ⇄ Cooldown`.

| Transition | Guard |
|---|---|
| Default → Settling | first tick |
| Settling → Active | `settleElapsed ≥ SettlingMinSeconds (60)` AND `sampleCount ≥ 12` AND a confident pick exists |
| Active → Cooldown | on any set change; `SetCooldownSeconds (300)` timer |
| Cooldown → Active | timer elapsed |
| `OnManualAdjust` | resets cooldown to full, no state change (defers next eval) |
| `OnProposalDeclined` | **doubles** remaining cooldown (`CooldownDeclineMultiplier = 2.0`) — repeated rejection backs off geometrically |

**Propose-confirm (commit `0327680`).** The selector *silently* auto-moves only **within the same 5-band「動態感」segment**. A **cross-segment / upward** move is **queued** as an in-memory `_pendingSetMove` with a `now + SetMoveProposalTtlSeconds (86 400 = 24 h)` expiry — never auto-applied, never persisted. It is surfaced **only on a genuine user-open** of the pop-up (the `SetMoveProposalPanel`); Accept → `ConfirmPendingSetMove` (offset-preserving), Decline → `OnProposalDeclined`, expiry → cleared on the next tick. A vestibular guard hoisted above the queue clamps such users to the L9 floor and never proposes a *weaker* Band.

### 6a.5 Per-set renderer wiring / 每組對渲染器的連動

Three wiring points reach the renderer when the flag is ON:

1. **Mask DEPTH (commit `7593d87`).** The active set's resolved `PeakMaskAlpha` is written to `SenseState.MaskPeakAlphaOverride` (default `−1.0` = no override). The overlay consumes it: `if (state.MaskPeakAlphaOverride >= 0) tAlpha = state.MaskPeakAlphaOverride` — **depth is set-driven; hue stays level-driven**. Flag-OFF / Set0 is byte-identical.
2. **Discretizer Scale (commit `bdf193b`).** `UpdateVimsTier` reads the active set's `Scale` (uniform 11.0 today — §6.4).
3. **Grid visibility (commit `bdf193b`).** On a set move, the new set's `GridVisibility` re-seeds `_shader.GridVisibility`. (Related grid-off fix: `GridCeiling(0) = 0` so `/grid off` fully hides the grid — §8.3.2.)

`ApplySetMove` is the single offset-preserving apply path: it clears the transient chatbot delta while **preserving habituation `_offset` (Q4.5)**, then re-seeds level / FOV-floor / mask-depth / grid from the new curve. The state machine never touches `_offset` by design.

---

## 7. Inference Layer State Machine / 推論層狀態機

### 7.1 Level Hysteresis / 等級遲滯

Level transitions are gated by asymmetric debounce plus a fast-onset/fast-release bypass, in `UpdateVimsTier` ([MockInferenceEngine.cs:1811-1912](src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs)).

| Constant | Value | Source |
|---|---|---|
| `VimsTierDeadband` | 0.15 | [SenseDefaults.cs:147](src/SenseEase.Core/Configuration/SenseDefaults.cs) |
| `VimsTierUpDebounceMs` | **200** | `:148` |
| `VimsTierDownDebounceMs` | **500** (was 1200) | `:149` |
| `VimsTierDownDebounceLowMs` | **250** | `:154` |
| `VimsLowTierReleaseCutoff` | **9** (rescaled 7→9 for the 12-level scale) | `:158` |
| `VimsTierFastOnsetGap` | 1.5 | `:162` |
| `VimsTierFastReleaseGap` | 1.5 | `:169` |

State table (`level` clamped `[1, 12]`):

| From | Guard | To | Action |
|---|---|---|---|
| `level` | `levelF ≥ level + 0.5 + deadband` for 200 ms | `min(12, level+1)` | commit |
| `level` | `levelF ≥ level + FastOnsetGap (1.5)` | snap to `clamp(round(levelF), level+1, 12)` | bypass debounce |
| `level` | `levelF ≤ level − 0.5 − deadband` for 500 ms (or 250 ms if target ≤ 9) | `max(1, level−1)` | commit |
| `level` | `levelF ≤ level − FastReleaseGap (1.5)` | snap to `clamp(round(levelF), 1, level−1)` | bypass debounce |

The cutoff rescale keeps the same top-3 proportion: the top three levels (10, 11, 12) keep the slow "dangerous top" release; levels ≤ 9 clear briskly — mirroring the old 1..10 scale's 8, 9, 10.

### 7.2 Band Mapping / 帶位對映

The 12 levels map to the 5「動態感」bands per §2.8 (L1–2 / L3–4 / L5–7 / L8–10 / L11–12). The legacy 3-band colour token (L1–4 / L5–7 / L8–12) is render-only.

### 7.3 Mask Engagement Hysteresis / 遮罩啟用遲滯

There is no named four-phase machine in the engine; mask engagement is a two-line boolean hysteresis reading **raw `TriggerPressure` (NOT `EffectiveTrigger`)** ([MockInferenceEngine.cs:399-413](src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs)):

| Transition | Guard | Action |
|---|---|---|
| OFF → ON | `pressure ≥ TriggerPressure (85) ∧ now − _lastMaskChange ≥ CooldownMs (8000)` | `MaskActive = true` |
| ON → OFF | `pressure < TriggerPressure × 0.85 = 72.25` | `MaskActive = false` |

The 12.75-unit gap between engage (85) and release (72.25) prevents oscillation; the 8 s cooldown covers a transient calm-then-spike. (The named `Active/Settling/Cooldown` machine is the *Adaptive-Fit* `AdaptiveFitStateMachine`, governing set moves — §6a.4 — not mask engagement.)

### 7.4 Severe-Strike State and the Habituation State Machine / 累計次數與習慣化狀態機

Severe strikes are **not** time-debounced at registration; the de-facto debounce is the post-spike pause.

**Strike registration.** A strike is registered when `pressure ≥ EffectiveTrigger × 0.60`. On strike: `_offset := _offset / 2`, `_severeStrikeCount += 1`. If `_severeStrikeCount ≥ SevereStrikeLimit`: `_offset := 0`, `_severeStrikeCount := 0`.

**Post-spike pause as de-facto debounce.** After a strike, `EnterPause` sets `_pauseCooldown` from `GetPostSpikePauseMs(vimsTier)`:

| Profile | low band (`PostSpikePauseLowMs`) | mid band (`PostSpikePauseMidMs`) | top band |
|---|---|---|---|
| Resilient | 90 s | 120 s | ∞ (awaits user confirm) |
| Typical | 90 s | 180 s | ∞ |
| Sensitive | 180 s | 300 s | ∞ |

During the pause the next Step hits an early-return and cannot register another strike. **Effective per-profile severe debounce equals these pause durations.**

**Top-band uniform `PausedAwaitingUserConfirm`.** All three profiles enter `PausedAwaitingUserConfirm` at the top band. During it: `_pauseCooldown = +∞` (never ticks down); `_stableElapsed` / `_stepAccumulator` frozen at 0; `_offset` frozen (already halved/zeroed by the strike); `_severeStrikeCount` can still increase (a sensor spike during the await window re-triggers the strike handler); the **sensor side keeps running** (OFI accumulates, pressure / mask hysteresis / level projection / FOV update normally using raw `TriggerPressure`). Exit is **only** via `AcknowledgeAndResume` — no timer-based exit.

### 7.5 Habituation Relaxation State Machine (Two-Stage Gated) / 習慣化放鬆狀態機（兩階段閘控）

**Stage 1 — Stability accumulator (`_stableElapsed`).**

```csharp
bool calm = pressure < EffectiveTrigger * HabituationStablePressureFraction (0.25) && !maskActive;
if (calm) _stableElapsed = min(_effectiveWindow, _stableElapsed + dt);
else      _stableElapsed = max(0, _stableElapsed - dt * HabituationMidBandDecayRate (0.5));
```

Decay (not instant reset) is the load-bearing detail — non-contiguous calm cannot ratchet, but a brief jitter doesn't erase prior progress.

**Stage 2 — Step gate.**

```csharp
if (_stableElapsed >= _effectiveWindow)
{
    _phase = Relaxing;
    _severeStrikeCount = 0;                 // "earn a clean slate"
    _stepAccumulator += dt;
    while (_stepAccumulator >= _effectiveStepInterval && _offset < HabOffsetCapPp)
    {
        _stepAccumulator -= _effectiveStepInterval;
        _offset = min(HabOffsetCapPp, _offset + StepMagnitudePp);
    }
}
```

A full window pass zeros `_severeStrikeCount` (the user earns a clean slate), coupling §7.5 to §7.4.

### 7.6 Severe-Strike State Summary

A **strike** registers on `pressure ≥ EffectiveTrigger × 0.60`; on strike `_offset := _offset / 2`, `_severeStrikeCount += 1`. Reaching `SevereStrikeLimit` zeroes `_offset` and resets the counter. The blanket user-confirm gate at the top band is the load-bearing safety floor.

---

## 8. Rendering Layer — Overlay / 渲染層 — Overlay

### 8.1 OverlayWindow / OverlayWindow 視窗

`OverlayWindow.xaml` is a click-through transparent topmost WPF window (extended styles `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_TOPMOST`, set in code-behind).

### 8.2 FOV Radius — Composition, Range, and Pressure Mapping / FOV 半徑 — 組合、範圍與壓力對映

`ComposeFovRadius` ([MockInferenceEngine.cs:1743-1745](src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs)) sums three contributors, clamped `[15, 100]`:

```
FOV_radius = clamp(ComputeFovRadiusPercent(P) + _userFovOffset + _habituation.HabituationOffset, 15, 100)
```

- **Pressure-driven base** — `ComputeFovRadiusPercent(P)` (below).
- **Negative-only user feedback offset** — `_userFovOffset ∈ [−60, 0]` (chatbot tightening, `MaxUserFovOffsetAccumulated = −60`).
- **Non-negative habituation offset** — `_habituation.HabituationOffset ∈ [0, HabOffsetCapPp]`.
- **Hard clamp `[15, 100]`** — `MinComposedFovRadius = 15`.

**Engine default (rest state) is `100`** (fully open). **Pressure-to-radius mapping** ([:1769-1788](src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs)), two-segment piecewise-linear:

- **Pre-trigger** (`P ≤ 85`): `radius = 100 − (100 − 35) × (P / 85)` — 100 % at rest to `DefaultFovRadiusPercent = 35 %` at the trigger.
- **Post-trigger** (`P > 85`): `radius = 35 − (35 − 15) × clamp((P − 85) / (0.6 × 85), 0, 1)`. **The denominator is `0.6 × TriggerPressure = 51`** (a tuning iteration steepened the post-trigger slope), so the radius saturates at `TightestNaturalFovRadiusPercent = 15 %` when `P − 85 ≥ 51`, i.e. at **`P = 136 = 1.6 × TriggerPressure`**.

(Audit nuance: the *saturation point* is 1.6×Trigger = 136, but the *denominator literal* is `0.6 × Trigger = 51`. The prior edition's "denominator 136" phrasing was imprecise.)

The discrete `VimsTier` is computed in parallel from the same pressure scalar but drives the visual mask colour/alpha lookup in `OverlayWindow.VimsLevels[]`, NOT the FOV radius.

### 8.3 Shader Knobs — `ShaderParams` / 著色器參數

Three normalised `[0, 1]` shader knobs, all clamped `[0, 1]` in `ShaderParams.Clamp()`:

| Knob | Default | Hard clamp | Source |
|---|---|---|---|
| `MaskDepth` | 0.74 | `[0, 1]` | `ShaderParams.cs:11, 30` |
| `GridVisibility` | 0.75 | `[0, 1]` | `ShaderParams.cs:12, 31` |
| `FeatherWidth` | 0.50 | `[0, 1]` | `ShaderParams.cs:13, 32` |

`FeatherWidth`'s `0.50` is the *default initialiser*, not an upper bound (the bound is `[0, 1]`).

#### 8.3.1 `MaskDepth`
Drives the peripheral attenuation depth in the radial gradient, encoded as `Color.FromScRgb(MaskDepth, 0, 0, 0)`. When the Adaptive-Fit selector is ON, the per-set `PeakMaskAlpha` override (§6a.5) supersedes the static level alpha.

#### 8.3.2 `GridVisibility` — Chang–Hain anchor grid
Scales the *opacity ceiling* of the static anchor-grid layer in `OverlayWindow.ApplyGridIntensity`:

```
ceiling = GridVisibility > 0 ? min(1.0, 0.40 + 0.80 × GridVisibility) : 0.0     // grid-off fix
target  = clamp(tierFraction × ceiling × (0.55 + 0.45 × smoothedIntensity), 0, ceiling)
tierFraction = (VimsTier − 1) / 11      // 0 at L1, 1 at L12
```

The grid never appears at L1; it ramps in linearly with level and is amplified by current pressure-driven intensity. **Grid-off fix (was audit A4):** `GridVisibility == 0` now floors the ceiling to a true `0.0` (was `0.40`), so `/grid off` (§11a) fully hides the grid.

#### 8.3.3 `FeatherWidth`
Drives the inner edge offset of the radial gradient: `InnerEdgeOffset = 1.0 − FeatherWidth`.

### 8.4 Radial Gradient Brush / 徑向漸層筆刷

```xml
<RadialGradientBrush GradientOrigin="{Binding Cx},{Binding Cy}" Center="{Binding Cx},{Binding Cy}"
                     RadiusX="{Binding FovRadiusFraction}" RadiusY="{Binding FovRadiusFraction}">
  <GradientStop Offset="0.0" Color="#00000000" />
  <GradientStop Offset="{Binding InnerEdgeOffset}" Color="#00000000" />
  <GradientStop Offset="1.0" Color="{Binding MaskDepthColor}" />
</RadialGradientBrush>
```

The mask centre `(cx, cy)` is fed either from head-tracking or hardcoded to `(0.5, 0.5)`.

### 8.5 Feathering Halo Fix — Asymmetric Radius Envelope / 羽化光暈修正 — 不對稱半徑包絡 (commit `f381a43`)

**Root cause.** The FOV mask brush uses `MappingMode.RelativeToBoundingBox`. Its *dynamic radius* (corner reach) is recomputed raw every render frame from head position ([OverlayWindow.xaml.cs:635-638](src/SenseEase.App/Views/OverlayWindow.xaml.cs)):

```
farthestCorner = sqrt(max(cx,1−cx)² + max(cy,1−cy)²)
targetRadius   = max(BrushRadius (0.85), farthestCorner × BrushRadiusCornerMargin (1.10))
```

Because the brush is relative-to-bounding-box, applying this raw per frame makes the `0.35→0.95` smoothstep feather band **translate AND scale in screen pixels** as the head moves — the soft edge "breathes," perceived as a halo / comet-tail. (In Gestalt terms: a common-fate violation + perceptual scission from independently-timed translucent strata.)

**The fix — asymmetric half-life envelope on the radius only** ([OverlayWindow.xaml.cs:662-669](src/SenseEase.App/Views/OverlayWindow.xaml.cs)):

```
halfLifeMs = (targetRadius > _smoothedRadius) ? OverlayRadiusAttackHalfLifeMs (25)   // grow → near-instant
                                              : OverlayRadiusReleaseHalfLifeMs (600); // shrink → slow
factor = 1 − exp(−dtMs · ln2 / halfLifeMs)
_smoothedRadius += factor · (targetRadius − _smoothedRadius)
```

Fast **attack** (≈25 ms) means a newly-fixated corner is covered almost instantly (preserves the M5 UX-iter-3 corner reach); slow **release** (≈600 ms) means the edge never pumps while the head moves. **Center follow stays fully responsive** — only the radius is smoothed. The ~165 Hz `CompositionTarget.Rendering` pump owns the envelope; the ~3.3 Hz engine-tick path snaps to target only when no pump ran within `PumpOwnershipWindowMs = 250` (preserves pre-envelope unit-test behaviour). Inactive snaps to the v0.9 baseline `0.85` immediately. The grid OpacityMask mirrors the same eased radius. Constants at [SenseDefaults.cs:61-62](src/SenseEase.Core/Configuration/SenseDefaults.cs).

**Status.** This is **Phase 1** (P1) — shipped, UVHS-confirmed ("visual fatigue greatly reduced, no lag"). The fuller redesign — unifying all strata onto one motion clock (A1) and a single-pass per-pixel field matching the WebGL reference (A2) — is **designed-not-done**, gated behind a 165 Hz perf spike, and documented in `docs/BETA_FEATHERING_COHERENCE_PLAN.md`.

### 8.6 Animations — Actual Durations / 動畫實際時長

| Animation | Duration | Location |
|---|---|---|
| Window opacity fade-in | 380 ms | `OverlayWindow.xaml.cs` |
| Grid opacity per-tick anim | 280 ms | `OverlayWindow.xaml.cs` |
| Escape-hatch fade-down / up | 250 / 500 ms | `OverlayWindow.xaml.cs` |

The FOV mask colour/alpha cross-fade between levels is a per-tick exponential ease with `MaskSmoothingFactor = 0.25` ([SenseDefaults.cs:47](src/SenseEase.Core/Configuration/SenseDefaults.cs)), ~250 ms convergence at the ~15 Hz tick rate. (These four ms literals remain inline — promotion to `SenseDefaults` is a Beta polish item.)

### 8.7 Exclude-rect Coupling / 排除矩形耦合

The overlay bar's monitor coordinates are pushed back into `WgcSensingProvider.SetMonitorExcludeRect` whenever the bar repositions or resizes.

### 8.8 DPI and Multi-monitor — Known Limitation (A8) / DPI 與多螢幕

`OverlayWindow` is constructed once with `WindowState="Maximized"` and maximises onto whichever monitor it instantiates on (typically primary). Grid spacing is DPI-scaled against `SystemParameters.PrimaryScreenWidth` only. There is no per-monitor enumeration, no Display-Changed handler, no second-overlay management. Multi-monitor users get protection on a single display. **Full multi-monitor coverage** is a v1.0 scope item (§13.6).

---

## 9. Rendering Layer — Bar, Tray, Card, Settings / 渲染層

### 9.1 OverlayBarWindow — 環境式狀態列
The ambient bar displays the current band as a coloured pill using the 5-band「動態感」cool-blue palette (§2.8).

### 9.2 TrayIconHost — 系統匣
16×16 ARGB bitmap rendered to match the current band colour, swapped on band crossings.

### 9.3 NotificationCardWindow — 提案確認卡片
Sections:
1. **Header** — current band colour + short status text (band-only motion label — commit `9c407de`).
2. **Quick-feedback row** — buttons backed by `QuickDiscomfortPhrases`.
3. **Chatbot proposal section** — visible when an `_activeProposal` is pending (15-second countdown).
4. **Adaptive-Fit set-move proposal panel** (`SetMoveProposalPanel`) — visible only when the selector has a `_pendingSetMove` and the user opened the card (§6a.4).
5. **Resume section** — visible when the engine is in the top-band await-confirm pause.

The Card's 15 s proposal-expiry timer is distinct from the 60 s VM watchdog in `MainViewModel.StartProposalExpiryWatchdog` (`CardCountdown ≤ Watchdog` invariant — §12). A seamless pop-up flip affordance was added (commit `65fdc3b`); the card auto-dismisses when the user clicks another app.

### 9.4 SettingsWindow / 設定視窗
Exposes: head-tracking enable + follow-mode picker, chatbot enable, the **vestibular-protection toggle** (§9a), and the **"clear all my data"** button (H24 — §11a), plus factory reset. (The calibration entry is **gone** — §9a.)

### 9.5 MainWindow / Dashboard / 主視窗
Hosts the chatbot surface, live OFI / **DisplayStressPercent** / level readouts, Demo-mode controls, the auto-profiling vestibular prompt bar, and the Event-Log window. The pop-up's `+/−` buttons adjust the 12-level intensity scale directly (commit `5b86696`).

---

## 9a. Auto-Profiling and the Vestibular Prompt / 自動體質推估與前庭保護提示 (Alpha-2)

This section documents the replacement for the deleted explicit-calibration subsystem (§2.5).

**What was removed (commit `3131387`).** `CalibrationApplicator`, `CalibrationCardViewModel` (the Q1–Q5 card), `CalibrationConfirmCardViewModel`, `CalibrationCardView`, `CalibrationConfirmCardView`, `DemoVideoWindow` (the Stage-5c vection-clip loop), `CalibrationLlmDeltaParser`, `CalibrationChatbotService`, `CalibrationPrompts`, and six calibration test files. The demo/calibration video resolution was removed from `AppConfig` (tombstone comment at `AppConfig.cs:132-134`).

**What replaced it.**
- **Start defaults.** A genuinely fresh user is seeded at **level 5 / TYPICAL** (`ApplyAutoProfilingStartDefaults`, §2.5).
- **Vestibular prompt.** Instead of a Q5 risk-factor branch, vestibular users are caught by a single opt-in prompt: `MainViewModel.ShowVestibularPrompt` (gated `AutoProfilingEnabled && !UserPreferences.VestibularPromptAnswered`) surfaces a non-blocking bottom bar ([MainWindow.xaml:162-186](src/SenseEase.App/MainWindow.xaml)); `AnswerVestibularPrompt(true)` calls `SetVestibularProtection` (EffectiveTrigger 50, strike-limit 1). A permanent Settings toggle (`SettingsContent.xaml.cs:442`) lets the user change it later.
- **Flag mirroring.** `MainViewModel` mirrors both `AppConfig` flags into the engine at startup, then calls `ApplyAutoProfilingStartDefaults()` ([MainViewModel.cs:696-708](src/SenseEase.App/ViewModels/MainViewModel.cs)). *(Doc-accuracy note: a stale `MainViewModel.cs:693` comment says "defaults to false … no-op today" — the authoritative ground truth is the `AppConfig` `= true` values at lines 268 / 294.)*

---

## 10. LLM Integration — Core / LLM 整合 — 核心

### 10.1 Transport / 傳輸
`MistralChatbotService` speaks the OpenAI-compatible `POST /v1/chat/completions` surface against `127.0.0.1:8181`, with loopback `api-key` / `--no-webui` hardening (H9/H29).

### 10.2 Model and Backend — Lazy Load / 模型與後端 — 延遲載入
`Ministral-3-3B-Instruct-2512` GGUF, `Q4_K_M` quantization, served by a **side-loaded Vulkan `llama-server.exe`** subprocess (llama.cpp **b9294**); `LLamaSharp` is the managed API only.

**Lazy load (commit `55e510b`).** The 3B model no longer loads at startup. `EnsureModelLoadedAsync()` (idempotent, `Interlocked`) fires on the **first chat message that reaches the LLM**, awaited just before `SendAsync`. App launch + WGC sensing are immediate; the safety-floor, slash-command (§11a), and profile-picker short-circuits never need the model, so they stay instant. Status text: "Ministral 3B 待命 — 首次對話時載入 / ready — loads on first chat".

**CUDA backend dropped.** `LLamaSharp.Backend.Cuda12.Windows` now carries `ExcludeAssets="native;runtime"`, removing the unused ~148 MB `ggml-cuda.dll`; a `DropUnusedCudaNative` MSBuild target deletes any cuda12 native folder that slips through (H28).

Sampling parameters: `--ctx-size 4096`, `--temp 0.6`, `--top-p 0.95`, `--repeat-penalty 1.1`.

### 10.3 System Prompt Assembly / 系統提示組裝
Per-turn assembly from blocks: safety preamble, `[RecentAdjustments]`, `[RecentEvents]`, `[habituation_state]` (now carrying a cross-session `trajectory` line — §11a), `[behavior_trend]` (UM5 — §11a), `[engine_state]`/`[SenseEaseState]`, `[intent_schema]`, `[exemplars]`.

### 10.4 NLU JSON Envelope / NLU JSON 信封
```json
{
  "Intent": "AdjustParameters | ReportDiscomfort | ResetRequest | ProfileOverride | ObservationAdd | ResumeAcknowledge | Unknown",
  "Response": "<reply text>",
  "FovDelta": <int, optional, clamped [-30, 0]>,
  "MaskDepthDelta": <double, optional, clamped [-0.3, 0.3]>,
  "GridVisibilityDelta": <double, optional, clamped [-0.3, 0.3]>,
  "FeatherWidthDelta": <double, optional, clamped [-0.3, 0.3]>
}
```
Per-turn clamps match `MistralChatbotService` and the deterministic `FeedbackLexicon` (single source: `SenseDefaults.FovStepBySeverity`, `MaskDepthStepBySeverity`, `MaxFovDeltaPerCall = −30`, `MaxShaderDeltaPerCall = 0.3` — was audit C7, CLOSED).

### 10.5 Structured Override Blocks / 結構化覆寫區塊
Up to three structured blocks may accompany the JSON envelope: `[PROFILE_OVERRIDE]`, `[OBSERVATION_ADD]`, `[RESUME_ACKNOWLEDGE]`.

### 10.6 Stateless Grounding / 無狀態 grounding
The chatbot conversation is **stateless across app restarts** (Q4.6) for *dialogue*, but the User-Model now provides durable *longitudinal* grounding via `[behavior_trend]` and the monthly observation buckets (§11a) — a deliberate split between transient chat and durable, numeric/observational memory.

---

## 11. LLM Integration — Safety + Override Flow / LLM 整合 — 安全與覆寫流程

### 11.1 SafetyFloor — extracted to Core, hardened / 安全底線（已抽取至 Core 並強化）

The self-harm crisis interceptor was **extracted from the two duplicated services into `src/SenseEase.Core/Safety/SafetyFloor.cs`** (Alpha-1), closing the copy-paste drift (was audit C3) and the critical ordering bugs (H1/H2/H3).

#### 11.1.1 Decision logic
`SafetyFloor.Trips(userText)`:
1. **Normalise** `FormKC` + `ToLowerInvariant()` + curly-apostrophe folding — so full-width Latin and Unicode variants are caught (H3).
2. Scan the bilingual phrase tables (`PhrasesHan` adds Simplified `自杀/自残/结束生命/跳楼`; `PhrasesEn` adds `kys/unalive/end myself/off myself/no reason to live/better off dead/wish i was dead`), with `kys` word-boundary matched and a 跳樓 sale-idiom guard to reduce false positives.
3. Match → emit hard-coded localized crisis response (TW 1925 for zh, US 988 for en, plus `findahelpline.com`); no LLM call, no NLU.

#### 11.1.2 Ordering — the H1/H2 fixes
- **H1 (CLOSED).** The SafetyFloor runs **before** the in-chat profile-picker short-circuit, so a user-in-crisis typing a profile phrase still sees the crisis response.
- **H2 (CLOSED).** The floor check precedes persistence; matched user text is stored as `SafetyFloor.RedactedPlaceholder = "[REDACTED: safety_floor]"`, not verbatim, in `chat_history.jsonl`.

The general chatbot keeps the session alive after the crisis response; the (now-removed) calibration service used to end its session — that differential is moot post-calibration-removal.

### 11.2 `HabituationOverrideParser` — 結構化覆寫區塊解析
Parses `[PROFILE_OVERRIDE]` / `[OBSERVATION_ADD]` / `[RESUME_ACKNOWLEDGE]`. `OverrideIntent` enumerates only `adjust_sensitivity` / `switch_profile` / `adjust_strike_limit`; `ResetOffset` is **deliberately absent** (Q4.5). Rationale text is capped at `RationaleMaxLength = 120` (H7).

### 11.3 `HabituationOverrideProcessor` — 五條驗證規則
1. **Expiry window** — `expires_at_iso` in the future, ≤ `ExpiresAtMaxFutureDays = 7` out.
2. **Value range** — sensitivity ∈ [0.7, 1.5], known profile, strike-limit ∈ [1, 5].
3. **Vestibular hard cap** — vestibular advisory + relaxation → REJECT.
4. **One proposal at a time.**
5. **7-day decline cooldown** — `RecentDeclineCooldown = 7 days`.

(The cosmetic rejection-message bug referencing the wrong constant was fixed in the F1 migration — CLOSED.)

### 11.4 `HabituationOverrideApplicator` — 防禦性攔截與引擎寫入
`ApplyAccepted` re-runs the defensive intercept **before** the engine write (even though the Processor already cleared the proposal), in most-specific-first order:
1. **Vestibular strike-limit hard cap** — vestibular + strike-limit-1 + `AdjustStrikeLimit` → reject.
2. **Vestibular + relaxation** — vestibular + any relaxation → reject.
3. **HighRiskCombo + significant relaxation** — `SignificantRelaxationThreshold = 0.15` → reject.

Engine surface: `SwitchProfile`, `AdjustSensitivity` (writes `personal_pressure_scale`), `AdjustStrikeLimit` (writes `severe_strike_limit`, now durably persisted — §11a). `ChatbotActionLog` states: `Proposed / Accepted / Declined / Expired / Intercepted`.

### 11.5 `ProposalReceived` Flow / 提案接收流程
Snapshot + validate → reject branch logs `Habituation.SafetyIntercept` → accept branch clamps expiry to `min(model expiry, now + 7 days)`, stores `_activeProposal`, records `Proposed`, starts the **60 s VM watchdog**, raises `ProposalSurfaceRequested`. Card timer 15 s = "respond now" budget; VM watchdog 60 s = headless safety net; both idempotent.

### 11.6 ResetPersonalization — Q4.5 fix / 重設個人化 — Q4.5 修正
`Intent == "ResetRequest"` and `/reset shader` now **preserve the habituation `_offset`** (was audit gap C5). `HabituationEngine.Reset()` was split into `ResetPostCalibration()` and `ResetTransientState()`; the chatbot reset routes to the transient path so weeks of earned relaxation survive a "reset" utterance.

### 11.7 Lexicon Backfill — `QuickDiscomfortPhrases`
```csharp
private static readonly string[] QuickDiscomfortPhrases = {
    string.Empty, "我有點不舒服", "我不舒服", "我很不舒服", "我非常不舒服，請大力調整",
};
```

### 11.8 End-to-End Summary / 端到端摘要
A user message traverses: **slash-command lexicon (§11a) → SafetyFloor → LLM round-trip (lazy-loaded) → parser → 5-rule Processor → if accepted, `_activeProposal` → Card → user clicks → Applicator re-runs defensive intercepts → engine write → `ChatbotActionLog`**.

---

## 11a. User-Model and the Slash-Command Lexicon / 使用者模型與斜線指令詞庫 (Alpha-2)

### 11a.1 G1 slash-command shader control / 斜線指令著色器控制 (commit `3b45947`)

`ShaderCommandLexicon` ([src/SenseEase.Core/Inference/ShaderCommandLexicon.cs](src/SenseEase.Core/Inference/ShaderCommandLexicon.cs)) is a pure, static, **pre-LLM** interceptor that resolves high-confidence shader requests locally with no model round-trip.

- **Slash commands:** `/grid off|on|<0-100>`, `/mask <20-100>`, `/feather <0-100>`, `/fov <15-100>`, `/reset shader`, `/help`, `/show`.
- **Bilingual NL whitelist (`StrictPhrases`):** whole-utterance match (trim + lowercase, **not** substring — "I don't want the grid off" does not match). EN + ZH: `grid off`/`關掉格線`/`隱藏格線`, `grid on`/`打開格線`, reset/`重設遮罩`, show/`目前設定`, etc.
- **Absolute-set, bypasses the `[-0.3, +0.3]` per-turn cap** (these are `SetParam`, not deltas).
- **Mask hard-floored at 20 %** (`MaskDepthSafetyFloor = 0.20`); `/mask 0..19` and `mask off`/`拿掉遮罩`/`關掉遮罩` are rejected with a suggestion to use `/mask 20` — the mask is core protection.
- **`/reset shader` preserves the habituation offset** (Q4.5) — resets shader knobs + FOV offset to profile defaults only; reply text "習慣化進度保留 / habituation progress kept".

### 11a.2 UM1 — behavior_log daily telemetry / 每日行為遙測
`DailyBehaviorAccumulator` ([src/SenseEase.Core/Inference/DailyBehaviorAccumulator.cs](src/SenseEase.Core/Inference/DailyBehaviorAccumulator.cs)) is a pure, in-memory, memory-bounded single-day accumulator (OFI/level folded into frequency histograms, not per-sample). `MainViewModel.SampleBehavior` folds each inference tick; `FlushBehaviorAggregate` writes every 60 s, on local-date rollover, and at shutdown. **Demo runs are excluded** (the synthetic vection ramp is a stress test, not real use). Fully fail-soft.

Persisted append-only, **idempotent by date**, snake_case, to **`%LOCALAPPDATA%\SenseEase\behavior_log.jsonl`** (`DailyBehaviorAggregateWriter`). Schema v1 (`DailyBehaviorAggregate`), `Exportable = false`, `DeletableOnClear = true`, **numeric-only — no utterances, no OFI traces, no window titles**:

| JSON key | Meaning |
|---|---|
| `date` | local YYYY-MM-DD (idempotency key) |
| `session_minutes` | app-foreground minutes (exposure duration), 0–1440 |
| `ofi_p50` / `ofi_p90` | median / 90th-pct optical-flow |
| `tier_modal` | most-frequent level (mode) |
| `spike_count` | severe-event / symptom-proxy count |
| `stable_windows_completed` | successful habituation steps |
| `offset_delta_pp` | net habituation progress (offset end − start), pp |
| `mask_activations` | FOV-mask activation count |

### 11a.3 UM2 — schema v3 monthly observation buckets / 月度觀察分桶
`habituation_profile.json` is now **schema v3** (`HabituationProfileSnapshot.SchemaVersion = 3`). The flat v2 `longitudinal_observations` list is superseded by `longitudinal_observations_v2`: a `Dictionary<"YYYY-MM", List<ObservationEntry{ts, text}>>`, capped `MaxObservationsPerBucket = 30` / `MaxBucketMonths = 12`, with month-scoped Jaccard dedup (`LongitudinalObservationWriter`, sanitized for prompt-injection — H5). `FileHabituationProfileStore.MigrateV2ToV3` moves flat entries into the current month's bucket, strips the legacy date stamp, and bumps the version; an H14 guard refuses a hand-edited v1 downgrade.

### 11a.4 UM3 — index store / 索引存儲
`UserModelIndex` (`%LOCALAPPDATA%\SenseEase\user_model_index.json`, atomic via `AtomicJsonWriter`) tracks `LastAnalyzedChatTurnIndex` (exclusive upper bound of analysed chat turns) and a per-month `BucketManifest{EntryCount, LastUpdated}`. `UserModelIndexStore : IUserModelStore` owns the whole user-model file surface for H24.

### 11a.5 UM4 — retrospective analysis pipeline / 回溯分析管線
`UserModelRetrospectiveAnalyzer` (started 2 min after launch, background) reads the un-analysed tail of `chat_history.jsonl` from `LastAnalyzedChatTurnIndex`, sends a slice (≤ `MaxTurnsPerRun = 100`) to the LLM to extract durable VIMS `[OBSERVATION_ADD]` blocks, persists them via the same dedup + monthly-bucket path, and advances the index. Resource-guarded: a >100-turn backlog drains across runs; cancellation does not advance the index; transient LLM error retries the slice. `HabituationTrajectoryBuilder.BuildSummary` reads `IEventLogStore.ReadDurable(cutoff)` (commit `b49f15a`) so the `[habituation_state]` trajectory line spans **prior sessions**, not just the current one.

### 11a.6 UM5 — `[behavior_trend]` context block / 行為趨勢區塊
`LongitudinalSummaryBuilder.BuildBlock` (30-day window, ≥ 7 days required, ~80-token budget) emits, injected after `[habituation_state]`:

```
[behavior_trend]
window: 30d
exposure_days: <n>/30
ofi_p90_trend: <±Δ> (rising|steady|falling)        // early vs late half, ±2 dead-band
adaptation_slope: <±slope>pp/week                  // Σ offset_delta_pp / weeks
spike_rate: <e>/day → <l>/day (improving|steady|worsening)
tolerance_ceiling: tier <n> [(stable)]             // max tier_modal; "stable" if last 7d all ≥ ceiling−1
[/behavior_trend]
```

### 11a.7 H24 / H25 — data clearing & privacy / 資料清除與隱私
- **H24 (CLOSED).** `UserModelIndexStore.ClearUserModel()` deletes `behavior_log.jsonl`, resets the index, and truncates the observation buckets (Q4.5-safe — never touches `_offset` or live engine). The Settings **"clear all my data"** button (`SettingsContent.xaml.cs:468-511`) pre-confirms with a bilingual destructive warning, then clears the user model + `model_trace.jsonl`; the calibration profile / strength is **kept**.
- **H25 (CLOSED in committed installer).** On uninstall, the Inno Setup script offers a bilingual opt-in MsgBox to also delete `%LOCALAPPDATA%\SenseEase\`; `[UninstallDelete]` is intentionally empty so a reinstall preserves data, and nothing is deleted without consent. *(The .iss is deleted in the current working tree but present in committed HEAD — §13.1.)*

### 11a.8 Cross-session habituation persistence / 跨工作階段習慣化持久化
Two files split durable engine state by design:
- **`habituation.json`** — the authoritative earned `_offset` (`FileHabituationStore`); saved only when the offset changes; restored at engine ctor.
- **`habituation_profile.json` (v3)** — profile, strikes, engine state, `personal_pressure_scale`, `severe_strike_limit_override`, vestibular flag (atomic `Update`, dirty-checked). At boot the engine **re-derives `EffectiveTrigger = TriggerPressure / scale`** (commit `429f8a2`, so personalised sensitivity isn't silently lost), restores the strike-limit override (commit `55d8b6a`), and re-applies vestibular protection (which wins over the strike override). The strike *count* is deliberately not restored (within-session safety counter).

---

## 12. Design Decisions + Complete Parameter Catalog / 設計決策與完整參數目錄

### 12.1 SenseDefaults — 集中參數來源
All load-bearing parameters live in [src/SenseEase.Core/Configuration/SenseDefaults.cs](src/SenseEase.Core/Configuration/SenseDefaults.cs). The SSOT rule is now enforced by doc-comment: *any tuning constant referenced by 2+ files MUST live here* (was audit F2 — `ChartTriggerLine`, `MaskSmoothingFactor`, `OverlayBrushRadius` etc. migrated — CLOSED).

#### 12.1.1 Sensing-Layer Parameters / 感測層參數

| Constant | Value | Purpose | Source |
|---|---|---|---|
| `WgcCaptureIntervalMs` | 66 | WGC capture period (~15 Hz) | `:96` |
| `WgcDownsampleWidth / Height` | 160 / 90 | Greyscale buffer (N = 14 400) | `:97-98` |
| `WgcMadNormFactor` | 4.0 | MAD → OFI scale (real data partially validates — §5.2.6) | `:102` |
| `CursorVelocityPollIntervalMs` | 33 | Cursor poll (~30 Hz) | `:90` |
| `CursorVelocityNormFactor` | 120.0 | px/s → OFI scale | `:89` |
| `CursorEmaAlpha` | 0.55 | Shared EMA alpha (cursor + WGC) | `:93` |
| `VimsMedianTaps` | 3 | Running-median window | `:141` |
| `NoiseGateThreshold` | 8 | Sub-threshold OFI → 0 | `:16` |

#### 12.1.2 Inference-Layer Parameters / 推論層參數

| Constant | Value | Purpose | Source |
|---|---|---|---|
| `PassiveFlowWeight` | 2.0 | Passenger-mode flow multiplier (8× asymmetry, WH) | `:64` |
| `DriverFlowAttenuation` | 0.25 | Driver-mode OFI multiplier | `:77` |
| `QuadraticTerm` | 0.0008 | Quadratic stimulus coefficient | `:65` |
| `BaseDecayAlpha` | 0.965 | Per-100 ms decay anchor (τ ≈ 2.81 s) | `:67` |
| `TriggerPressure` | 85 | Mask-engagement + level/FOV threshold (raw) | `:17` |
| `CooldownMs` | 8000 | Min interval between mask engagements | `:69` |
| `TightestNaturalFovRadiusPercent` / `MinComposedFovRadius` | 15 | Tightness denom + clamp floor | `:12, :39` |
| `DefaultFovRadiusPercent` | 35 | Pre-trigger pressure-curve target | `:9` |
| `OuterTransitionRadiusPercent` | 45 | Tightness numerator window edge | `:13` |
| `HabituationStablePressureFraction` | 0.25 | Calm-fraction threshold | `:106` |
| `HabituationRecurrencePressureFraction` | 0.60 | Spike-recurrence threshold | `:107` |
| `HabituationMidBandDecayRate` | 0.5 | `_stableElapsed` decay rate | `:129` |
| `HabituationCautionMeanDeltaDivisor` | 15.0 | rawCaution saturation divisor | `:133` |
| `HabituationMaxStepIntervalSeconds` | 75 | Cautious-user step ceiling | `:128` |
| `PersonalPressureScaleMin / Max` | 0.7 / 1.5 | Sensitivity clamp (SSOT) | `:83-84` |
| `VimsTierDeadband` | 0.15 | Half-band width in level units | `:147` |
| `VimsTierUpDebounceMs` | 200 | Level-up debounce | `:148` |
| `VimsTierDownDebounceMs` | 500 | Level-down debounce (was 1200) | `:149` |
| `VimsTierDownDebounceLowMs` | 250 | Level-down debounce for levels ≤ cutoff | `:154` |
| `VimsLowTierReleaseCutoff` | 9 | Fast-release threshold (rescaled 7→9) | `:158` |
| `VimsTierFastOnsetGap / FastReleaseGap` | 1.5 / 1.5 | Snap bypass thresholds | `:162, :169` |
| `AutoProfilingStartLevel` | 5 | Fresh-user seed level (Set0 median) | `:22` |

#### 12.1.3 Adaptive-Fit Selector Parameters / 自適應擬合選擇器參數

| Constant | Value | Source |
|---|---|---|
| `SelectorW1Pressure / W2Spike / W3Calm` | 1.0 / 0.6 / 0.6 | `:188-194` |
| `SelectorW4Distance / W5ManualConflict` | 0.1 / 2.0 | `:199-204` |
| `SelectorConfidenceMargin` | 0.15 | `:208` |
| `SelectorFitTolerance` | 0.7 | `:217` |
| `SelectorMinSamplesBeforeAnyMove` | 12 | `:221` |
| `SetCooldownSeconds` | 300.0 | `:225` |
| `SettlingMinSeconds` | 60.0 | `:229` |
| `CooldownDeclineMultiplier` | 2.0 | `:233` |
| `SetMoveProposalTtlSeconds` | 86400 (24 h) | `:239` |

#### 12.1.4 Overlay / Feathering Constants / Overlay 與羽化常數

| Constant | Value | Purpose | Source |
|---|---|---|---|
| `MaskSmoothingFactor` | 0.25 | Overlay cross-fade EMA | `:47` |
| `OverlayBrushRadius` | 0.85 | Baseline radial brush radius | `:49` |
| `OverlayBrushRadiusCornerMargin` | 1.10 | Dynamic-radius corner margin | `:51` |
| `OverlayRadiusAttackHalfLifeMs` | 25.0 | Feathering attack (grow) half-life | `:61` |
| `OverlayRadiusReleaseHalfLifeMs` | 600.0 | Feathering release (shrink) half-life | `:62` |
| `ChartTriggerLine` | = `TriggerPressure` | Dashboard chart line (SSOT) | `:45` |

#### 12.1.5 Chatbot Delta Bounds — Single Source of Truth / 聊天增減量界限

| Knob | Per-call clamp | Accumulated clamp |
|---|---|---|
| FovDelta | `[-30, 0]` (`MaxFovDeltaPerCall`) | `_userFovOffset ∈ [-60, 0]` (`MaxUserFovOffsetAccumulated`) |
| MaskDepth / Grid / Feather Delta | `[-0.3, +0.3]` (`MaxShaderDeltaPerCall`) | `[0, 1]` each (`ShaderParams.Clamp`) |
| Composed FOV radius | — | `[15, 100]` (`MinComposedFovRadius`) |

Per-severity step magnitudes are the single-source arrays `FovStepBySeverity = {−6,−10,−15,−22,−30}` and `MaskDepthStepBySeverity = {0.05,0.08,0.12,0.18,0.25}` ([SenseDefaults.cs:27-29](src/SenseEase.Core/Configuration/SenseDefaults.cs)).

#### 12.1.6 Safety-Floor / Vestibular Constants / 安全與前庭常數

| Constant | Value | Source |
|---|---|---|
| `VestibularTriggerPressureOverride` | 50 | `CalibrationProcessor.cs:34` (named — was audit C1, CLOSED) |
| Vestibular strike-limit override | 1 | `MockInferenceEngine.SetVestibularProtection` |
| `MaskDepthSafetyFloor` | 0.20 | `ShaderCommandLexicon.cs:79` |
| `RationaleMaxLength` | 120 | `HabituationOverrideParser.cs` (H7) |

### 12.2 Locked Decisions / 鎖定決策
- **Q4.5** — Chatbot cannot reset the habituation offset. **NOW HONORED** — `ResetPersonalization` / `/reset shader` / `ApplySetMove` all preserve `_offset` (was violated; C5 CLOSED).
- **Q4.6** — Chatbot dialogue is stateless across restarts (longitudinal grounding is separate — §11a).
- **Q4.7 = B** — `ChatbotActionLog` is in-memory; durable audit lives in `FileEventLogStore` / `FileChatbotActionLog` (H17).
- **Path Z default** — `AppConfig.Mode = Wgc`.
- **Auto-profiling default** — level 5 / TYPICAL (was: explicit calibration / RESILIENT).
- **Native 12-level engine / 5-band UI** — engine source-of-truth is 12 levels; UI surfaces 5「動態感」bands, no alarm-red.
- **Top-band user-confirm gate is profile-independent.**
- **Adaptive-Fit + auto-profiling flags ON** by default.
- **OBS-3 head-tracking** — 8-field `SidecarFrameDto`, pinned by a reflection schema test (E3 CLOSED).

---

## 13. POC Validation + Process Discipline + Roadmap / POC 驗證、流程紀律與未來路線圖

### 13.1 Headline Validation Results / 驗證結果摘要

| Gate | Status | Evidence pointer |
|---|---|---|
| G_final | **GO** | `head-tracking-sprint-state.md` |
| UVHS | **PASS** (incl. feathering P1) | `uvhs-playbook.md`, `BETA_FEATHERING_COHERENCE_PLAN.md` |
| Locked-decision violations | **0** (Q4.5 + safety-parity now closed) | sprint-state register |
| `dotnet test` | **~1058 / 0 / 3** (commit `f381a43`; HEAD green save one `UserPreferences` isolation flake) | `SenseEase.Tests` |
| Adaptive-Fit + auto-profiling | **live** (both flags ON) | commit `c1461a9` |
| M1 OFI calibration | **real data (partial)** | `baseline/wgc-mad-calibration.csv`, commit `a98cebd` |
| Vulkan cross-vendor backend + lazy load | shipping | commits `2679e51`, `55e510b` |
| Installer | v0.11.1 dist | committed HEAD `installer/SenseEase-v0.11.iss` (**deleted in working tree** — uncommitted local change) |

### 13.2 UVHS Playbook — 4 Anti-cheat Clauses
1. Visual evidence required. 2. No same-process trust. 3. No self-attestation. 4. No silent fallbacks. (Clause-5 orthogonal probe = `WgcDiagLog` monotonic sequence.)

### 13.3 Advisor Gates G1–G7 / 顧問門 G1–G7

| Gate | Reviews | Date passed |
|---|---|---|
| G1–G7 | Architecture / Privacy / Performance / Safety / UX / Test coverage / Documentation | 2026-05-22 … 2026-05-30 |
| M8 | Safety milestone | 2026-05-30 |

### 13.4 Sprint History / Sprint 歷史
- **v0.8** Pop-up Overlay (2026-05-27) · **v0.10** Head-tracking, 32 commits (2026-05-30) · **v0.11** Inno Setup installer · **v0.11.1** portable sidecar + cross-vendor Vulkan.
- **Post-seal (untagged, this branch):** Alpha-1 audit fixes → User-Model sprint (UM1–UM5, 2026-06-07) → Alpha-2 orchestration + core activation (Adaptive-Fit + auto-profiling, 2026-06-07) → explicit-calibration removal (2026-06-08) → feathering halo fix (2026-06-08).

### 13.5 Test-suite Topology / 測試套件拓撲

| Layer | Tests (approx.) | Coverage focus |
|---|---|---|
| Core | ~330 | LeakyIntegrator, HabituationOverride{Parser,Processor}, HabituationProfile, **AdaptiveFit selector + state machine**, **SafetyFloor**, **MadCalibration**, locked-decision structural tests |
| Infrastructure | ~340 | MockInferenceEngine, WGC `ComputeMad`/calibration, `WgcDiagLog`, **user-model + profile stores + migration**, sidecar schema |
| App | ~388 | MainViewModel proposal handshake, Applicator intercepts, ChatbotViewModel NLU dispatch, **behaviour collector**, **set-move proposal** |

*(Totals approximate; the explicit-calibration removal dropped ~6 test files. The exact count was last stated at 1058/0/3 in commit `f381a43`; this revision did not re-run the suite to avoid the known AV build-hang on the dev machine.)*

### 13.6 Roadmap and Audit-Item Status / 路線圖與審計項目狀態

**Resolved since the 2026-06-04 audit (CLOSED):**
- **C1** vestibular `50` extracted to `VestibularTriggerPressureOverride`.
- **C3** safety-floor extracted to Core (`SafetyFloor.cs`); colon drift gone; parity guaranteed by single source.
- **C5** `ResetPersonalization` / `/reset shader` preserve `_offset` (Q4.5).
- **C7 / F2** delta bounds + step magnitudes single-sourced; `ChartTriggerLine`/`OFI norm` literals migrated.
- **A4** `/grid off` now floors grid ceiling to true 0.
- **A5** `FeatherWidth` clamp documented as `[0, 1]`.
- **E3** OBS-3 `SidecarFrameDto` reflection schema test added.
- **F1** §12.1.4-era policy values migrated to `SenseDefaults`; cosmetic message bug fixed.
- **D1 (partial)** real MAD→OFI capture (idle + 1080p) committed; upper band still open.
- **A7 (partial)** Band-axis now deforms per-set mask depth / FOV floor.

**Still open:**
- **D1 (upper band)** capture `word_scroll` / `fullscreen_game` / `scene_cut`; review and possibly retune `WgcMadNormFactor`.
- **D2** magnitude assertion for the 8× Driver/Passenger ratio.
- **D3** `LeakyIntegratorConvergenceTests` (analytic equilibrium / rate-invariance / 5τ).
- **A7 (full)** per-profile shader defaults independent of the selector Band.
- **A8** multi-monitor coverage (v1.0).
- **Shape axis** — Adaptive-Fit Shape currently render-only; numeric response-shape deformation deferred until M1 anchors OFI norms.
- **Feathering A1/A2** — single-clock motion model + single-pass per-pixel field (`BETA_FEATHERING_COHERENCE_PLAN.md`).
- **Animation constant extraction** — 380/280/250/500 ms overlay literals.

**Alpha security backlog (H1–H53, [docs/ALPHA_SECURITY_BACKLOG.md](docs/ALPHA_SECURITY_BACKLOG.md)).** The Alpha-1 must-fix wave is substantially implemented and code-verified: **H1** (profile-picker after SafetyFloor), **H2** (redact-before-persist), **H3** (NFKC normalisation + Simplified/youth-shorthand phrases), **H5** (observation sanitisation), **H7** (rationale cap), **H9/H29** (loopback auth), **H14** (schema-downgrade guard), **H17** (durable audit log), **H19** (deterministic PDB paths), **H24/H25** (data clear + uninstall prompt), **H28** (CUDA backend drop), **H41** (UTC log timestamps), **H49** (Run-key `uninsdeletevalue`). The deeper Alpha-2 hardening items (e.g. integrity manifest, tamper-resistance, kill-switch) are **partially landed / partially open** — the backlog document tracks no per-item status, so an exact fixed/open tally requires a fresh code audit.

### 13.7 What the System Does Not Claim / 不主張的事項
- SenseEase is **not** a medical device.
- The MAD-based OFI is a magnitude proxy, not a calibrated optical-flow vector field; the norm-factor is validated only against idle + one mid-band motion scene (D1 upper band open).
- The efference-copy weighting magnitudes are **internal working hypotheses** awaiting fuller calibration.
- The 12-level projection is content-agnostic and has not been validated against population-stratified VIMS susceptibility scores.
- The Adaptive-Fit selector weights/tolerances are **provisional and deliberately conservative** until M1 anchors the OFI norms; the Shape axis is render-only today.
- Multi-monitor configurations are not supported.

---

## 14. Bibliography / 參考文獻

1. **Bárány Society Classification Committee** (2023). *Consensus criteria for motion sickness and visually-induced motion sickness.* Journal of Vestibular Research.
2. **Chang, C.-P. & Hain, T. C.** (2020). *Visual vertigo treatment through optokinetic stimulation with stationary anchoring.* Physiotherapy Research and Reports 4:1–4. *(A short ~4-page theoretical proposal — not a clinical trial.)*
3. **Pavlou, M., Bronstein, A. M. & Davies, R. A.** (2013). *Randomized Trial of Supervised Versus Unsupervised Optokinetic Exercise in Persons With Peripheral Vestibular Disorders.* Neurorehabilitation and Neural Repair 27(3):208–218. *(The ~55 % dropout figure is the unsupervised arm; supervised ~10 %.)*
4. **Golding, J. F. & Keshavarz, B.** (2025). *Norms and Correlations of the VIMSSQ-short.* Multisensory Research.
5. **Vection literature, general.** Palmisano, Allison and co-authors.

---

## 15. Appendix: File Reference Index / 附錄：檔案參考索引

### 15.1 Core Layer / 核心層
- `src/SenseEase.Core/Configuration/SenseDefaults.cs` — all load-bearing constants
- `src/SenseEase.Core/Inference/LeakyIntegrator.cs` — `:40-54` the VIMS pressure update
- `src/SenseEase.Core/Inference/HabituationEngine.cs` — offset/strike/relaxation, `EffectiveTrigger`
- `src/SenseEase.Core/Inference/HabituationProfile.cs` — `:138-175` three-profile numerics
- `src/SenseEase.Core/Inference/HabituationOverride{Parser,Processor}.cs`
- `src/SenseEase.Core/Inference/ShaderCommandLexicon.cs` — G1 slash commands (mask floor `:79`, Q4.5 `:41`)
- `src/SenseEase.Core/Inference/DailyBehaviorAccumulator.cs` — UM1 collector
- `src/SenseEase.Core/Inference/{HabituationTrajectoryBuilder,LongitudinalSummaryBuilder}.cs` — UM4/UM5
- `src/SenseEase.Core/AdaptiveFit/{GridCoord,SetGrid,SetCurve,LevelParams,UserIntensityStats,IntensityStatsBuilder,DeterministicSetSelector,SelectorWeights,SelectorDecision,CooldownState,AdaptiveFitStateMachine}.cs`
- `src/SenseEase.Core/Safety/SafetyFloor.cs` — extracted self-harm interceptor
- `src/SenseEase.Core/Sensing/{MadCalibration,NormFactorRecommender}.cs` — M1 OFI calibration pipeline
- `src/SenseEase.Core/State/ShaderParams.cs` — `:28-33` Clamp
- `src/SenseEase.Core/Models/{SenseState,DailyBehaviorAggregate,UserModelIndex,HabituationProfileSnapshot}.cs`
- `src/SenseEase.Core/Inference/CalibrationProcessor.cs` / `CalibrationTypes.cs` — **vestigial** (constants + types only)

### 15.2 Infrastructure Layer / 基礎設施層
- `src/SenseEase.Infrastructure/Real/WgcSensingProvider.cs` — `:725-754` OFI pipeline, `:908` BT.601, `:989` `ComputeMad`, `:1002` median
- `src/SenseEase.Infrastructure/Real/CursorVelocityProvider.cs`
- `src/SenseEase.Infrastructure/Real/{UserActivityModeDetector,WgcDiagLog,SidecarHeadPoseReader}.cs`
- `src/SenseEase.Infrastructure/Real/MistralChatbotService.cs` — lazy load, loopback auth, UTC trace (H41)
- `src/SenseEase.Infrastructure/Real/UserModelRetrospectiveAnalyzer.cs` — UM4 batch analyzer
- `src/SenseEase.Infrastructure/Storage/{DailyBehaviorAggregateWriter,UserModelIndexStore,FileHabituationProfileStore,LongitudinalObservationWriter}.cs`
- `src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs` — `:366/377` OFI weights, `:378` integrator step, `:391-394` DisplayStressPercent, `:399-413` mask hysteresis, `:682-735` auto-profiling/vestibular, `:1743-1788` FOV composition, `:1790-1794` tightness, `:1811-1834` level projection, selector wiring (`:457` mask-depth override, `:935-980` ApplySetMove)

### 15.3 App Layer / 應用層
- `src/SenseEase.App/Configuration/AppConfig.cs` — `:268` AdaptiveFit ON, `:294` AutoProfiling ON, `:198-214` user-model paths
- `src/SenseEase.App/ViewModels/MainViewModel.cs` — `:696-708` flag mirroring + auto-profiling, `:1477-1525` behaviour collector, `:1633-1655` background analyzer
- `src/SenseEase.App/ViewModels/ChatbotViewModel.cs` — `BuildContextualInput` (`[behavior_trend]`), lazy `EnsureModelLoadedAsync`
- `src/SenseEase.App/ViewModels/HabituationOverrideApplicator.cs` — three-rule intercept

### 15.4 Views / UI Layer / 視圖層
- `src/SenseEase.App/Views/OverlayWindow.xaml.cs` — `:635-669` feathering envelope, `:798-799` grid-off ceiling, `:392-403` mask-depth override
- `src/SenseEase.App/Views/{TierColorPalette,Converters/TierComfortConverters}.cs` — 12-level → 5-band「動態感」mapping
- `src/SenseEase.App/Views/NotificationCardWindow.xaml.cs` — proposal card + `SetMoveProposalPanel`
- `src/SenseEase.App/Views/SettingsContent.xaml.cs` — `:442` vestibular toggle, `:468-511` clear-all-data
- `src/SenseEase.App/MainWindow.xaml` — `:162-186` vestibular prompt bar

### 15.5 Documentation / 文件
- `docs/SAFETY-PROVISIONS.md`, `docs/QA-AND-TIERING.md`, `docs/HABITUATION-PROFILE.md`, `docs/HEAD-TRACKING-PRIVACY.md`, `docs/LOG-PATHS.md`
- `docs/ALPHA_SECURITY_BACKLOG.md` (H1–H53), `docs/BETA_FEATHERING_COHERENCE_PLAN.md`
- `baseline/M1-CALIBRATION-README.md`, `baseline/wgc-mad-calibration.csv`

### 15.6 Packaging / 封裝
- `installer/SenseEase-v0.11.iss` (committed HEAD; deleted in working tree), `tools/head-tracker/`, `face_landmarker.task`

### 15.7 Tests / 測試
- `src/SenseEase.Tests/` — ~1058 passing / 0 failing / 3 skipped (commit `f381a43`); one known `UserPreferences` isolation flake.

---

*End of document. Version Pre-Alpha v0.11.1 — Alpha-2 engine-integrity revision. 2026-06-08.*
