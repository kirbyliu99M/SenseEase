# SenseEase — Proof-of-Concept Technical Whitepaper
# SenseEase — 概念驗證技術白皮書

**Version:** Pre-Alpha v0.11.1 (audit-revised 2026-06-04)
**Date:** 2026-06-04
**Status:** POC complete — G_final GO, UVHS-V11 PASS, 672 / 0 / 1 tests
**Document Class:** Internal engineering reference / 內部工程參考文件

---

## Glossary of Frequently-Used Terms / 常用術語表

| Term | Definition / 定義 |
|---|---|
| **VIMS** (視覺誘發性動暈症) | Visually-Induced Motion Sickness. Sopite, oculomotor and disorientation symptoms produced by visual stimuli that imply self-motion the vestibular system does not corroborate. |
| **OFI** (光流指數) | Optical Flow Index. SenseEase's scalar proxy for the magnitude of screen-relative motion in the most recent sample window. |
| **MAD** (平均絕對差) | Mean Absolute Difference. Per-pixel L1 distance between two consecutive frames, the numerical basis of OFI in the WGC sensing path. |
| **WGC** (Windows.Graphics.Capture) | The Windows screen-capture API the default sensing provider uses. |
| **Leaky integrator** (漏積分器) | First-order linear time-invariant discrete-time accumulator with exponential decay. Maps OFI(t) to the continuous VIMS pressure scalar P(t). |
| **Pressure scalar** (壓力純量) | Continuous nonnegative scalar P(t) produced by the leaky integrator. Engine source of truth for tier projection. |
| **VimsTier / Tier** (分層 / 等級) | Integer 1–10 derived from P(t). Engine-internal granularity; UI surfaces three perceptual bands. |
| **Band** (帶位) | Grey / Amber / Red — the three perceptual buckets the user sees, mapped from tier ranges 1–5 / 6–7 / 8–10. |
| **Driver / Passenger mode** (駕駛模式 / 乘客模式) | User-activity classification from `GetLastInputInfo`. Driver = actively producing input; Passenger = idle. |
| **Efference copy** (傳出複本) | The motor system's prediction of the sensory consequences of its own commands. The neurophysiological basis for the Driver / Passenger weighting asymmetry. |
| **Habituation** (習慣化) | A code-identifier name retained for the `Habituation*` engine types. The clinically accurate mechanism term for SenseEase's long-term effect is **graded exposure → sensory reweighting** (視覺再加權); for visually-induced vertigo, recovery is driven mainly by sensory reweighting / adaptation, with classical VOR habituation playing a limited role (see §2.5). SenseEase implements the graded-exposure offset as an `_offset` that is **added to the FOV radius percent** over time (NOT subtracted from the trigger pressure — see §6.5 for the load-bearing correction). |
| **Strike** / **Severe spike** (累計次數 / 重度峰值) | A discrete event when P(t) crosses the severe threshold. Strike count drives the protective backoff arithmetic. |
| **EffectiveTrigger** (有效觸發值) | `TriggerPressure / personal_pressure_scale + TriggerThresholdAdjustment`. The trigger value used **only** by the habituation engine's spike-recurrence and calm-fraction checks (NOT by the mask hysteresis, FOV-radius projection, or tier projection — those still read raw `TriggerPressure`). See §6.5 / §7.4. |
| **FOV mask** (視野遮罩) | The three-zone radial **peripheral attenuation mask** (周邊衰減遮罩) that dims peripheral content while preserving the foveal viewport. This is the *comfort / symptom-relief* layer, distinct from the **Chang–Hain anchor grid** (錨定格) — the stationary anchoring layer that carries the graded-exposure / therapy framing (see §2.3). |
| **Notification Card** (提案確認卡片) | The propose-confirm UI surface where chatbot proposals reach the user. |
| **Ambient bar** (環境式狀態列) | The small always-on-top window that paints the current band as a coloured pill. |
| **OBS-3** | The three privacy hard-rules that govern what the head-tracker sidecar is allowed to publish across the IPC boundary. |
| **UVHS** (使用者可視硬停協議) | User-Visual-Hard-Stop Playbook — a cross-sprint reusable anti-cheat protocol for any sprint with visible output. |
| **Load-bearing** (關鍵承載) | Within this document, a design choice, parameter value or invariant whose change would break a user-visible contract, a safety claim or a test. |
| **Working hypothesis (WH)** (內部工作假設) | A SenseEase-internal claim not yet externally validated. Used to mark places where the document is candid that a number or relationship is an engineering choice awaiting future calibration evidence. |

---

## 1. Executive Overview / 執行摘要

SenseEase is a Windows desktop application whose Proof-of-Concept (POC, 概念驗證) phase set out to answer one question: **can a non-medical, client-side software intervention measurably reduce Visually-Induced Motion Sickness (VIMS, 視覺誘發性動暈症) provocation on a conventional desktop monitor without degrading the user's primary task?** This whitepaper documents the architecture, theoretical grounding, parameter calibration, validation evidence, and process discipline that took the project from initial sensing prototypes to a v0.11.1 shippable Pre-Alpha installer.

The POC delivers three integrated subsystems. A **sensing layer** (感測層) derives an Optical Flow Index (OFI, 光流指數) from real-time Windows.Graphics.Capture (WGC, 像素差分擷取) pixel-difference measurement, augmented by cursor velocity and user-activity classification. An **inference layer** (推論層) integrates OFI through a noise-gated first-order leaky integrator (漏積分器) producing a continuous VIMS pressure scalar (壓力純量), then projects that scalar onto a 10-tier comfort classification with hysteresis (遲滯). A **rendering layer** (渲染層) applies a three-zone radial peripheral attenuation mask (周邊衰減遮罩) plus a Chang–Hain stationary anchor grid (錨定格, see §2.3), an ambient bar (環境式狀態列), a tray icon (系統匣圖示), and a propose-confirm Notification Card (提案確認卡片). A locally-hosted **Ministral-3B** chatbot — specifically `Ministral-3-3B-Instruct-2512-Q4_K_M.gguf`, served via `llama-server` over an OpenAI-compatible HTTP surface — provides a natural-language adjustment channel gated by a five-rule structured-override Processor and a defensive intercept Applicator. A MediaPipe FaceLandmarker sidecar — portable, vendored, and Vulkan-accelerated across NVIDIA, AMD, and Intel GPUs since v0.11.1 — drives the foveal viewport centre from head pose without ever permitting raw landmark data to cross the IPC boundary.

The headline POC outcomes are:

- **G_final GO** — the final go/no-go gate (G1–G7 advisor gates plus the M8 safety review) cleared with no locked-decision violations recorded across the 32-commit head-tracking sprint arc.
- **UVHS-V11 PASS** — the User-Visual-Hard-Stop protocol (4 anti-cheat clauses, see §13.2) passed eleven consecutive verification rounds, including the clause-5 orthogonal-probe requirement satisfied by the `WgcDiagLog` monotonic-sequence sink.
- **672 / 0 / 1 tests** — 672 passing, 0 failing, 1 skipped across the Core, Infrastructure, and App test suites, with **zero locked-decision violations**. Per-layer breakdown in §13.5.
- **v0.11.1 cross-vendor Vulkan + portable sidecar** — the chatbot backend runs on any Vulkan-capable GPU regardless of vendor; the head-tracker Python sidecar is fully portable and ships inside the Inno Setup installer.

**Audit-revision note (2026-06-04):** This version of the whitepaper integrates a 30-item audit against the codebase. Notable corrections include: (a) the prior `FeatherWidth ∈ [0, 0.5]` claim was wrong — actual clamp is `[0, 1]` with default `0.50` (§8.2 / §8.3); (b) tier projection is a closed-form linear ramp, not a lookup table (§6.4); (c) the habituation `_offset` is added to the **FOV radius percent**, NOT subtracted from the trigger pressure (§6.5); (d) `personal_pressure_scale` divides the trigger via `EffectiveTrigger`, and is consumed only by the habituation engine — the mask hysteresis, FOV-radius projection, and tier projection still read raw `TriggerPressure` (§6.5, §7.4); (e) the prior "600 ms mask animation" reference was incorrect — actual durations are 380 / 280 / 250 / 500 ms at four sites (§8.3); (f) `SevereDebounceMs` does not exist — severe-strike debouncing is de-facto through `_pauseCooldown` (§7.4). Multiple known gaps (per-profile shader defaults, multi-monitor coverage, `CalibrationConfidence` having no consumer, `ResetPersonalization` violating Q4.5, missing convergence tests, MAD/cursor calibration data, etc.) are documented in §13.6 with backlog references.

The remainder of this document is organised as follows. §2 grounds each architectural commitment in vestibular neuroscience, signal processing, and operant exposure psychology. §3 and §4 describe the system architecture and the project's physical organisation. §5 through §9 walk the data path from camera and screen down to the painted pixel. §10 and §11 cover the LLM integration. §12 enumerates every load-bearing parameter. §13 records validation evidence and roadmap. §14 hosts the bibliography. §15 provides a file index.

Where the engineering departs from external literature — for example, treating desktop scroll as a primary VIMS trigger surface — the departure is labelled an **internal working hypothesis (WH, 內部工作假設)**.

---

## 2. Theoretical Foundation / 理論基礎

This section lays the conceptual scaffolding for the SenseEase POC. Each subsection grounds one architectural commitment in the Core / Inference / App layers against an externally recognisable concept from vestibular neuroscience, signal processing, or operant exposure psychology. Where SenseEase departs from established literature, the departure is marked as an **internal working hypothesis (WH)**.

### 2.1 VIMS Pathophysiology, Triggers, and Desktop Relevance / VIMS 病理生理學、觸發因子與桌面相關性

Visually-Induced Motion Sickness, VIMS (視覺誘發性動暈症), is the family of sopite, oculomotor, and disorientation symptoms elicited when the visual scene implies self-motion that the vestibular and proprioceptive systems do not corroborate. The dominant mechanistic account remains **sensory-conflict theory (感覺衝突理論)**: when the optic-flow channel signals translational or rotational ego-motion while the vestibular semicircular canals and otoliths report a stationary head, the central nervous system has no consistent model of self-state and generates symptoms ranging from mild eyestrain to acute nausea. A complementary account, **postural-instability theory (姿勢不穩理論)**, proposes that VIMS arises when the visual environment perturbs the postural-control loop in ways the body cannot pre-empt.

For SenseEase the two theories converge on the same operational antecedent: **large-field, low-predictability optical flow without a matching vestibular signal**. Classical triggers include head-mounted VR, IMAX cinema, simulator sickness, and the cybersickness (網路暈動症) literature spanning the past three decades.

**Desktop relevance (WH).** Desktop monitor content has been studied less than VR but is not field-free: high-frame-rate scroll on a 27-inch monitor at typical viewing distance subtends roughly 40 to 50 degrees of visual angle, which is within the regime where **vection (自我運動錯覺)** can be elicited in susceptible individuals. SenseEase's *internal working hypothesis* is that for the VIMS-sensitive desktop user, three recurring stimulus classes — autoscroll on dense feeds, video playback containing camera dolly or shaky-cam, and animated UI carousels — produce sub-acute exposure that accumulates over a session.

### 2.2 Optical Flow and the MAD Measurement Estimator / 光流與 MAD 量測估計器

SenseEase uses a **frame-difference proxy (像素差分代理)** for optical-flow magnitude, computed as the Mean Absolute Difference (MAD, 平均絕對差) between consecutive downsampled greyscale frames captured by the Windows.Graphics.Capture (WGC) provider:

```
MAD(t) = (1 / N) · Σ_i | I_t[i] − I_{t-1}[i] |
OFI(t) = MAD(t) × WgcMadNormFactor      // 4.0, SenseDefaults.cs:42
```

This is implemented in `WgcSensingProvider.ComputeMad` (§5.2.1). It is a deliberate engineering simplification: the per-pixel L1 difference is not a flow vector, only a scalar proxy.

**Audit caveat (D1):** The narrative "idle desktop ≈ MAD 0-1, scrolling video ≈ MAD 15-40" is an **inline-comment hypothesis** (`SenseDefaults.cs:40`), not a measured calibration. `baseline/` and `soak_report.json` contain no MAD-to-OFI mapping for any content type; `WgcMadNormFactor = 4.0` and `CursorVelocityNormFactor = 120.0` remain working-hypothesis values until `baseline/wgc-mad-calibration.csv` and `baseline/cursor-velocity-calibration.csv` are populated (see §13.6 backlog item D1).

**Why MAD rather than dense flow (WH).** The hypothesis is that for VIMS prediction the *magnitude and duration* of screen-relative motion matters more than its *direction field*.

### 2.3 Peripheral Attenuation and the Radial Gradient Rationale / 周邊衰減與徑向梯度的設計依據

A central anatomical fact underpins SenseEase's mitigation strategy: peripheral retina (周邊視網膜) is dominated by rod photoreceptors and large-receptive-field magnocellular ganglion cells, which are sensitive to motion and low spatial frequency. The vection literature consistently reports that **peripheral motion is a stronger elicitor of self-motion illusion than foveal motion** at matched stimulus energy.

SenseEase inverts the simulator's trick: if the peripheral field is the disproportionate VIMS-driver, then **occluding or attenuating peripheral content while preserving the foveal reading region** should reduce the provocative stimulus without destroying task utility. The implementation is the three-zone radial **peripheral attenuation mask** (周邊衰減遮罩) rendered by `OverlayWindow.xaml` (§8).

**Two layers, two clinical roles — load-bearing distinction.** The overlay carries two visually distinct contributions that this document keeps separate:

- **Peripheral attenuation mask** (周邊衰減遮罩) — the continuous, pressure-driven radial dimming. Its role is **comfort / avoidance**: it reduces the provocative peripheral stimulus and relieves symptoms in the moment. It is the layer every user gets, and it makes no therapy claim.
- **Chang–Hain anchor grid** (錨定格) — the static stationary-anchoring grid layer (`ApplyGridIntensity`, §8.3.2). Its role is the **graded-exposure / therapy** component: a fixed reference frame against which residual scene motion is judged, consistent with the optokinetic-with-stationary-anchor approach Chang & Hain (2020) describe. It carries the long-term *sensory-reweighting* framing (§2.5), not the comfort framing.

The prior whitepaper referred to a single "Chang–Hain three-zone radial FOV mask", conflating these two roles. They are kept distinct here because only the anchor grid is associated with the (modest, non-medical) graded-exposure rationale; the peripheral attenuation is purely a comfort surface.

The mask centre `(cx, cy)` is normalised to `[0, 1]` and produced either by the head-tracking sidecar or hardcoded to `(0.5, 0.5)`.

### 2.4 Leaky Integrator Theory — 1st-Order LTI System / 漏積分器理論（一階 LTI 系統）

The inference layer wraps OFI in a **first-order linear time-invariant (一階線性非時變) discrete leaky integrator (漏積分器) with an upstream noise gate (雜訊閘)**, implemented in `src/SenseEase.Core/Inference/LeakyIntegrator.cs`.

The update rule, after gating, is the standard exponential-moving-average accumulator with a stimulus term:

```
gated(t)    = OFI_eff(t) ≥ NoiseGateThreshold ? OFI_eff(t) : 0
stimulus(t) = (gated(t) · weight + QuadraticTerm · gated(t)²) · dt
α(dt)       = BaseDecayAlpha ^ (dt / 0.1)        // BaseDecayAlpha = 0.965, SenseDefaults.cs:21
P(t)        = α(dt) · P(t−1) + stimulus(t)
```

Three points warrant emphasis:

- **Noise gate.** Sub-threshold flow is mapped to zero, not merely down-weighted. `NoiseGateThreshold = 8` (`SenseDefaults.cs:16`).
- **Quadratic term.** A small `QuadraticTerm · gated²` extra stimulus encodes the *working hypothesis (WH)* that high-magnitude flow is disproportionately provocative. `QuadraticTerm = 0.0008` (`SenseDefaults.cs:19`).
- **Time-corrected decay.** Decay is re-exponentiated by the inter-sample interval `dt`. The 100 ms anchor `BaseDecayAlpha = 0.965` corresponds to a time constant τ ≈ 2.81 s.

### 2.5 Graded-Exposure Theory (the "Habituation" Engine), the Strike Model, and the Three Profiles / 漸進暴露理論（即「習慣化」引擎）、累計次數與三檔體質

**Terminology note (mechanism naming).** The engine type is named `HabituationEngine` and its state fields carry `Habituation*` identifiers, retained to avoid a large code rename. The clinically accurate description of SenseEase's *long-term* mechanism — for the more-susceptible subgroup — is **graded exposure → sensory reweighting** (漸進暴露 → 感覺再加權), not classical habituation. For visually-induced vertigo, recovery is driven mainly by sensory reweighting / adaptation (the central nervous system re-weighting an unreliable visual channel against vestibular and proprioceptive evidence); classical VOR habituation contributes comparatively little. "Habituation" in code-identifier names should be read as shorthand for this graded-exposure / sensory-reweighting process.

In the vestibular-rehabilitation tradition, Chang & Hain (2020) describe an optokinetic-stimulation-with-stationary-anchoring approach — the conceptual basis for SenseEase's *anchor grid* (§2.3) — in which a fixed reference frame supports a graded increase in tolerated scene motion, with explicit *protective backoff* when over-exposure produces severe symptoms. (Chang & Hain 2020 is a short theoretical proposal, not a clinical trial — see §14.) The empirical motivation for *not* using an aggressive single-shot exposure protocol comes from Pavlou, Bronstein & Davies (2013): in their randomized trial of supervised versus unsupervised optokinetic exercise, the **unsupervised** arm showed roughly 55 % dropout while the supervised arm dropped out at about 10 %. Because SenseEase is, by nature, an *unsupervised* desktop tool, its design deliberately keeps each exposure step small and protectively gated.

SenseEase encodes these two principles as the **graded-exposure offset (習慣化偏移, `_offset`)** + **severe-strike count (重度峰值累計次數)** dual-state system implemented in `HabituationEngine`.

**Three profiles** stratify the user population along clinical risk. Verified against `HabituationProfile.cs:138-175`:

| Profile | Clinical analogue | StableWindowMs | StepIntervalMs | StepMagnitudePp | HabOffsetCapPp | MinSamplesForFullWeight | SevereStrikeLimit |
|---|---|---|---|---|---|---|---|
| **RESILIENT** (耐受型) | normative-tolerance user | 120 000 | 30 000 | 2.0 | 30 | 3 | 2 |
| **TYPICAL** (一般型) | population-median user | 180 000 | 45 000 | 1.5 | 25 | 4 | 2 |
| **SENSITIVE** (敏感型) | clinically reactive user | 360 000 | 60 000 | 1.0 | 15 | 6 | 1 |

The progression of `StableWindowMs`, `StepIntervalMs`, and `StepMagnitudePp` across the three profiles is monotonic and reflects the **working hypothesis** that more sensitive users need (a) longer evidence of comfort before relaxing protection, (b) longer cooldown between relaxation steps, and (c) smaller per-step relaxations.

**Tier-8+ behaviour is NOT profile-differentiated.** `HabituationProfile.GetPostSpikePauseMs` returns `requireUserConfirm = true` for **any** profile when `vimsTier >= 8`. All three profiles enter `PausedAwaitingUserConfirm` and stay there until `AcknowledgeAndResume` is invoked.

**Audit gap (A7) — shader axes are NOT profile-differentiated.** `HabituationProfileParams` (`HabituationProfile.cs:67-206`) carries per-profile values for habituation cadence (`StableWindowMs`, `StepIntervalMs`, `StepMagnitudePp`, `PostSpikePauseLow/MidMs`, `HabOffsetCapPp`, `MinSamplesForFullWeight`, `SevereStrikeLimit`), but the visual shader knobs (`MaskDepth = 0.74`, `GridVisibility = 0.75`, `FeatherWidth = 0.50`) are global. `MockInferenceEngine._shader = ShaderParams.Default` (`MockInferenceEngine.cs:19`) is initialised once and never re-seeded on profile change; `SetHabituationProfile` does not touch `_shader`; `ResetPersonalization` resets to global defaults without consulting the profile. A SENSITIVE user reaches a tighter habituation ceiling and recovers more slowly from spikes, but their **peak mask intensity is identical to a RESILIENT user's**. The chatbot can personalise per-user via `ApplyShaderAdjustment` (`MockInferenceEngine.cs:313`), but per-profile shader defaults are a Alpha-2 candidate (§13.6 item A7).

The default profile is RESILIENT.

### 2.6 Head-Pose Viewport Rationale / 頭部姿勢視口設計依據

The head-tracking module addresses foveal targeting by driving `(cx, cy)` from a derived head-pose estimate produced by the MediaPipe FaceLandmarker running in a Python sidecar.

**OBS-3 hard rules — quoted in full.**

> **OBS-1.** No raw camera frame, no encoded video, no still image and no per-frame thumbnail crosses the IPC boundary.
>
> **OBS-2.** No raw FaceLandmarker output (478 landmark coordinates, blendshape coefficients, transformation matrices) crosses the IPC boundary. Only the *derived scalars* are published.
>
> **OBS-3.** No camera frame, landmark or scalar is ever written to disk by the sidecar except a single rolling text log that records sidecar lifecycle.

**Audit clarification (E3):** The actual IPC schema is broader than the four-field `(cx, cy, face_detected, luma)` shorthand suggests. `SidecarHeadPoseReader.SidecarFrameDto` (`SidecarHeadPoseReader.cs:636-666`) carries **eight** JSON fields: `t_ms`, `cx`, `cy`, `luma`, `face_detected`, `face_present`, plus debug-only `yaw_deg` and `pitch_deg`. All are scalar / boolean; no landmarks, blendshapes, or pixel data. The whitepaper terminology "derived scalars" remains accurate but the exact allowlist should be enforced by a reflection-based schema test (§13.6 item E3) so a future property addition fails CI rather than passing silently.

There are two follow-mode policies the user can choose: **Balanced** (mask centre clamped to `[0.10, 0.90]`) and **Full follow** (1:1).

### 2.7 Efference-Copy and the Driver–Passenger Asymmetry / 傳出複本與駕駛–乘客不對稱

SenseEase carries the driver-passenger asymmetry into desktop interaction. The `Mode` field on `SenseState` is either `"Driver"` or `"Passenger"`.

The concrete weights, verified at `MockInferenceEngine.cs:217` and `:228`:

- Driver mode: `effectiveOfi = opticalFlowIndex × 0.25`, `weight = 1.0` → net stimulus contribution multiplier **0.25**.
- Passenger mode: `effectiveOfi = opticalFlowIndex × 1.0`, `weight = PassiveFlowWeight = 2.0` (`SenseDefaults.cs:18`) → net stimulus contribution multiplier **2.0**.

The Passenger / Driver ratio is therefore **8×** (2.0 / 0.25). This 8× ratio is an **internal working hypothesis (WH)**.

**Audit caveat (D2):** The 8× ratio is asserted only *qualitatively* in `InferenceTests.PassengerMode_BuildsPressureFasterThanDriverMode` (`tests/SenseEase.Tests/InferenceTests.cs:68-82`). The test asserts only `passenger > driver`, not the ratio magnitude. Either half (`0.25`, `2.0`) could silently drift. A ratio assertion test is tracked as Alpha-2 backlog item D2.

### 2.8 Comfort-Tier Classification Rationale / 舒適度分級的設計依據

The engine's `VimsTier` is a 1–10 integer derived from `Pressure(t)` with hysteresis. The 10-tier scale is the **engine source of truth** but is *not* the UI surface. For ambient bar, tray icon, and pop-up card title, the 10 tiers are bucketed into 3 perceptual bands by `TierColorPalette`:

- **Grey** = tier 1–5 (it's fine / 一切正常)
- **Amber** = tier 6–7 (be careful / 請留意)
- **Red** = tier 8–10 (stop / 請停下)

**小結（中文）：** 本節將八項架構承諾分別錨定在外部概念上，並標示審計揭露的差異，包含三檔體質僅在習慣化節律上有區分、視覺著色器參數目前是全域非分檔；以及 OBS-3 IPC 實際欄位數量為八項。機制用語已正名：長期機制應稱「漸進暴露 → 感覺再加權（graded exposure → sensory reweighting）」，並區分「Chang–Hain 錨定格（治療成分）」與「周邊衰減遮罩（舒適/症狀緩解）」；`Habituation*` 僅為保留的程式碼識別子。

---

## 3. System Architecture / 系統架構

SenseEase is structured as a four-layer Windows desktop application in C# (.NET) with two out-of-process auxiliaries.

### 3.1 End-to-End Data Flow / 端到端資料流

1. **Capture.** `WgcSensingProvider` at ~15 Hz captures a 160×90 greyscale downsample. `CursorVelocityProvider` at ~30 Hz polls `GetCursorPos`. `UserActivityModeDetector` at 1 Hz polls `GetLastInputInfo`. An optional head-tracking sidecar publishes its scalar tuple.
2. **Sample emission.** Each sensing provider emits `OpticalFlowSample(OFI, sampledAtUtc)` events.
3. **Inference.** `MockInferenceEngine.Update(opticalFlowIndex, mode, sampledAt)` applies driver attenuation (×0.25), the Passenger-mode mask-attenuation feedback (×0.45→0.15), the leaky-integrator step, tier hysteresis, and habituation strike/offset bookkeeping.
4. **State propagation.** `SenseState` is exposed via `MainViewModel` to all visible UI surfaces.
5. **Auxiliary channels.** Chatbot subscribes to `SenseState` for the `[habituation_state]` and `[engine_state]` context blocks.

### 3.2 Process Topology / 程序拓撲

- **`SenseEase.exe`** (main WPF process) hosts all four in-process layers.
- **`llama-server.exe`** (Vulkan backend since v0.11.1) listens on **`127.0.0.1:8181`**.
- **Head-tracking sidecar** (Python, portable EXE since v0.11.1) wraps MediaPipe FaceLandmarker.

Sample sidecar IPC line (per audit clarification E3, the actual DTO has eight fields; this is the typical wire form):

```
{"t_ms":1717459200123,"cx":0.512,"cy":0.487,"luma":0.41,"face_detected":true,"face_present":true,"yaw_deg":-2.1,"pitch_deg":1.4}
```

Heartbeat semantics: the sidecar emits one line per FaceLandmarker tick (~10 Hz on CPU-only, ~30 Hz on Vulkan). If no line arrives within 2 seconds, the source is marked stale.

### 3.3 Layer Responsibility Summary / 各層職責摘要

| Layer | Project | Owns | Forbidden (禁止項) |
|---|---|---|---|
| Core | `SenseEase.Core` | abstractions, pure-logic processors, parameter defaults | I/O, Win32, WPF, HTTP |
| Infrastructure | `SenseEase.Infrastructure` | sensing providers, persistence, chatbot HTTP, sidecar IPC, mock implementations | UI, ViewModels |
| App | `SenseEase.App` | composition root, ViewModels, applicators, event log | direct Win32 |
| Views/UI | `SenseEase.App` (Views folder) | WPF windows, XAML, code-behind for input plumbing only | engine writes |

---

## 4. Project Structure & Dependency Graph / 專案結構與相依關係

### 4.1 Solution Layout / 方案佈局

```
SenseEase APP/
├── src/
│   ├── SenseEase.Core/                    # abstractions, pure logic
│   ├── SenseEase.Infrastructure/
│   ├── SenseEase.App/
│   └── SenseEase.Tests/                   # 672 / 0 / 1
├── docs/
├── tools/head-tracker/                    # vendored portable Python sidecar (v0.11.1)
├── installer/                             # Inno Setup .iss + assets (v0.11)
├── baseline/                              # perf + diag baselines for advisor gates
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
│ (Views + VMs) │        │  .Infrastructure │       │  (abstractions only) │
└───────┬───────┘        └────────┬─────────┘       └──────────────────────┘
        │                         │                          ▲
        └─────────────────────────┴──────────────────────────┘
                            (App → Core direct)
```

### 4.3 External Dependencies / 外部相依

| Package | Version | Consumer | Purpose |
|---|---|---|---|
| `Microsoft.Windows.SDK.Contracts` | 10.0.x | Infrastructure | WGC / WinRT |
| `LLamaSharp` | 0.27 | `MistralChatbotService` | llama-server process control |
| `xUnit` | 2.x | Tests | test runner |

### 4.4 Test Project Layout / 測試專案佈局

The 672 / 0 / 1 split is reproducible by `dotnet test` from the solution root.

---

## 5. Sensing Layer Deep-Dive / 感測層深度說明

### 5.1 Overview / 概述

Three concrete `ISensingProvider` implementations satisfy the contract: `WgcSensingProvider`, `CursorVelocityProvider`, and `MockSensingProvider`. A fourth helper — `UserActivityModeDetector` — runs orthogonally.

### 5.2 WgcSensingProvider — WGC 螢幕擷取感知器

`WgcSensingProvider` is the default sensor since Path Z UX iter 3 (2026-05-27).

#### 5.2.1 Pipeline / 管線

1. WGC session captures primary monitor at native resolution.
2. Each frame is copied into a CPU-readable D3D11 staging texture.
3. Step-sampled into a **160 × 90 greyscale buffer**.
4. Greyscale conversion uses integer BT.601 `(19·B + 150·G + 77·R) >> 8`.
5. MAD computed against previous frame, multiplied by `WgcMadNormFactor = 4.0`.
6. Raw OFI clamped to [0, 150], passed through 3-tap running median.
7. EMA-smoothed with `α = 0.55`.

#### 5.2.2 Cadence / 取樣頻率

`WgcCaptureIntervalMs = 66 ms` ≈ 15 Hz.

#### 5.2.3 Exclude-rect Mask / 排除矩形遮罩

`SetMonitorExcludeRect(left, top, width, height)` accepts a monitor-coordinate rectangle. During the downsample pass, cells corresponding to that rect are zeroed in **both** the current and previous frame buffers.

#### 5.2.4 Env-gated Tracing / 環境變數開關的追蹤紀錄

When `SENSEEASE_WGC_BAR_VALIDATION=1`, the provider opens `%TEMP%/wgc_ofi_trace.txt`.

#### 5.2.5 Clause-5 Diagnostic Writes / clause-5 診斷寫入

All diagnostic output goes through `WgcDiagLog` (§5.9).

### 5.3 CursorVelocityProvider — 游標速度感知器

`CursorVelocityProvider` is the Phase 2.1 legacy sensor, retained as a fallback.

#### 5.3.1 Sampling / 取樣

Polls Win32 `GetCursorPos` at 30 Hz. Normalises by `CursorVelocityNormFactor = 120.0`.

#### 5.3.2 EMA Smoothing / 指數移動平均

`α = 0.55` EMA. Output clamped to [0, 150].

#### 5.3.3 Cursor-drag OFI Rejection / 游標拖曳 OFI 抑制

Self-generated cursor motion does **not** induce vection. The provider suppresses OFI to 0 whenever any mouse button is held or `GetCapture() != 0`.

### 5.4 MockSensingProvider — 模擬感知器

No internal timer; OFI pushed externally via `Push(double opticalFlowIndex)`.

### 5.5 Sensing-mode Selection / 感知模式選擇

```csharp
public enum SensingMode { Demo, Cursor, Wgc, }
```

Path Z UX iter 3 flipped the default from `Cursor` to `Wgc`.

### 5.6 OFI Value Semantics / OFI 值語意

Phase 5 split the single `SenseState.OpticalFlowIndex` field into two semantically distinct values:

- **`SenseState.OpticalFlowIndex`** — post-attenuation effective value.
- **`SenseState.RawOpticalFlowIndex`** — raw sensor OFI before any engine processing.

### 5.7 UserActivityModeDetector / 使用者活動模式偵測器

Classifies the user as **Driver** or **Passenger**.

#### 5.7.1 Mechanism / 機制

Polls Win32 `GetLastInputInfo` at 1 Hz.

#### 5.7.2 Asymmetric Hysteresis / 不對稱遲滯

- **Passenger → Driver** when `idleMs ≤ DriverThresholdMs = 2500`. Fast attack.
- **Driver → Passenger** when `idleMs ≥ PassengerThresholdMs = 5000`. Slow release.

#### 5.7.3 Typing Override / 打字覆寫

Consults `WindowsActivityFilter.Instance.SuppressionReason`. If `"typing"`, force `"Driver"`.

#### 5.7.4 Auto-mode Master Switch / 自動模式總開關

`AppConfig.AutoDriverPassengerModeEnabled` (default `true`).

### 5.8 Efference-Copy Weighting / 傳出複本權重

#### 5.8.1 Driver Attenuation / Driver 衰減

```csharp
double effectiveOfi = isDriverMode ? (opticalFlowIndex * 0.25) : opticalFlowIndex;
```

#### 5.8.2 Passive Flow Weight / 被動光流權重

```csharp
double weight = isDriverMode ? 1.0 : SenseDefaults.PassiveFlowWeight; // 2.0
```

The 8× effective ratio is an **internal working hypothesis (WH)**.

#### 5.8.3 Mask-Attenuation Feedback Loop / 遮罩衰減回饋環路

`MockInferenceEngine.cs:220-225`, **only when the user is in Passenger mode AND the mask is already active**:

```csharp
if (!isDriverMode && Current.MaskActive)
{
    double tightness = ComputeMaskTightness(Current.FovRadiusPercent);
    double attenuation = 0.45 - (tightness * 0.30); // 0.45 -> 0.15
    effectiveOfi *= Math.Clamp(attenuation, 0.15, 0.45);
}
```

**`ComputeMaskTightness` — derivation (A3).** `ComputeMaskTightness(int fovRadiusPercent) → double in [0, 1]` is a private helper in `MockInferenceEngine` (`MockInferenceEngine.cs:876-880`) that normalises the *current* clear-zone radius against the design's outer-transition radius window:

```
tightness = clamp((OuterTransitionRadiusPercent - fovRadiusPercent) /
                  (OuterTransitionRadiusPercent - DefaultFovRadiusPercent), 0, 1)
```

With `OuterTransitionRadiusPercent = 45` and `DefaultFovRadiusPercent = 35` (`SenseDefaults.cs:13, 9`), the denominator span is `10`. The function returns `0` when `fovRadiusPercent ≥ 45` (fully relaxed, mask edge soft) and saturates at `1.0` for `fovRadiusPercent ≤ 35` (mask at or below default radius, tightness maxed). The single consumer (`MockInferenceEngine.cs:222`) maps tightness back into an effective-OFI attenuation `attenuation = clamp(0.45 - 0.30 × tightness, 0.15, 0.45)` — a closed-loop damping that reduces ping-pong oscillation around the trigger. It has **no direct visual effect** — the gradient shape is fixed in `BuildLevelFeather` / `BuildFovStopOffsets` (`OverlayWindow.xaml.cs:99-133, 639-664`).

**Driver-mode gate (audit gap E1):** This guard fires **only** in Passenger mode. In Driver mode the ×0.25 attenuation already dominates and stacking mask attenuation on top would make the engine unresponsive (and at tight FOV would drop effective OFI to `0.25 × 0.15 = 0.0375×`). The `!isDriverMode` clause at the head of the `if` is therefore load-bearing. No test currently exercises the Driver + MaskActive=true coexistence path; backlog item E1 adds `DriverMode_WithMaskActive_AppliesOnlyDriverAttenuation_NoExtraMaskAttenuation` to lock the invariant.

**Audit gap E2:** Constants `0.45`, `0.15`, `0.30` are test-orphans. A typo flipping the sign would not break any test. Backlog item E2 adds `PassengerMode_WithMaskActive_AttenuatesOfiBetween15And45Percent` and `…AttenuationScalesWithFovTightness`.

### 5.9 WgcDiagLog — clause-5 設計

Shared sink for `%TEMP%/wgc_diag.txt`.

#### 5.9.1 Retry on IOException / IOException 重試

`Append(msg)` retries up to 3 attempts on `IOException` with 5 / 10 ms back-off.

#### 5.9.2 Monotonic Sequence Prefix / 單調序號前綴

Every log line is prefixed with `[N=xxxxxxx]`.

#### 5.9.3 Independent Loss Sink / 獨立流失沉點

When all 3 retries fail, the failure event is recorded to a **separate** file `%TEMP%/wgc_diag.lost.log`.

#### 5.9.4 Scope and Trade-off / 範圍與取捨

Options 1 (retry) and 3 (orthogonal probe) of the advisor's three-option clean-up. Option 2 (single-writer queue) deferred to v1.0.

### 5.10 Sensing-layer Summary / 感測層小結

| Component | Cadence | Output | Key constant |
|---|---|---|---|
| `WgcSensingProvider` | 15 Hz | OFI from 160×90 MAD ×4.0, 3-tap median, α=0.55 EMA | `SenseDefaults.cs:36-42` |
| `CursorVelocityProvider` | 30 Hz | OFI from \|Δp\|/Δt / 120.0, α=0.55 EMA | `SenseDefaults.cs:29-33` |
| `MockSensingProvider` | external push | OFI as provided | `MockSensingProvider.cs:20` |
| `UserActivityModeDetector` | 1 Hz | Driver / Passenger | `UserActivityModeDetector.cs:51-53` |

**小結（中文）：** 感測層提供三個 ISensingProvider 與一個 Driver/Passenger 偵測器；OFI 經 MAD ×4.0 → 中位數 → EMA。Driver ×0.25、Passenger ×2.0 達成 8× 不對稱。MAD-attenuation 回饋僅在 Passenger 模式且遮罩啟用時觸發。

---

## 6. Inference Layer Mathematics / 推論層數學

### 6.1 The Update Rule / 更新規則

```
gated(t)    = OFI_eff(t) ≥ NoiseGateThreshold ? OFI_eff(t) : 0
stimulus(t) = (gated(t) · weight  +  QuadraticTerm · gated(t)²) · dt
α(dt)       = BaseDecayAlpha ^ (dt / 0.1)
P(t)        = α(dt) · P(t−1) + stimulus(t)
```

| Symbol | Constant | Value | Source |
|---|---|---|---|
| `NoiseGateThreshold` | `NoiseGateThreshold` | **8** | `SenseDefaults.cs:16` |
| `BaseDecayAlpha` | `BaseDecayAlpha` | **0.965** (τ ≈ 2.81 s) | `SenseDefaults.cs:21` |
| `QuadraticTerm` | `QuadraticTerm` | **0.0008** | `SenseDefaults.cs:19` |
| `PassiveFlowWeight` | `PassiveFlowWeight` | **2.0** (Passenger only) | `SenseDefaults.cs:18` |
| `TriggerPressure` | `TriggerPressure` | **85** | `SenseDefaults.cs:17` |

### 6.2 Time-Corrected Decay Derivation / 時間校正衰減推導

τ = −0.1 / ln(0.965) ≈ 2.81 s. A 30 Hz sample (`dt ≈ 0.033`) decays by `0.965^0.33 ≈ 0.988` per tick; a 15 Hz sample (`dt ≈ 0.066`) decays by `0.965^0.66 ≈ 0.977` per tick.

### 6.3 Equilibrium Pressure — Worked Example / 平衡壓力範例

```
P*  =  (g·w + q·g²) · dt  /  (1 − α(dt))
```

Worked example, Passenger mode, `g = 30`, `w = 2`, `q = 0.0008`, `dt = 0.066`:

- `g·w = 60`
- `q·g² = 0.72`
- Stimulus/tick: `(60 + 0.72) · 0.066 ≈ 4.008`
- `1 − α(0.066) ≈ 0.0231`
- **P\* ≈ 173.5**

The transient to equilibrium takes roughly `5τ ≈ 14 s`.

Compare Driver mode: `effectiveOfi = 30 · 0.25 = 7.5`, **below the noise gate of 8**, so gated(t) = 0 and P\* = 0.

**Audit caveat (D3):** This entire derivation lives only as prose. No `LeakyIntegratorConvergenceTests` fixture exists; no test references `2.81`, `5τ`, `14s`, or the analytic equilibrium. A refactor changing `BaseDecayAlpha` (0.965), the dt-scaling exponent (0.1), `QuadraticTerm`, or the integrator update order would silently invalidate the rule of thumb. Four convergence tests are tracked as Alpha-2 backlog item D3.

### 6.4 Tier Projection / 分層投影

**Audit correction (B1):** The prior whitepaper described a "tier table"; the actual implementation is a **closed-form linear projection** at `MockInferenceEngine.cs:909-910`:

```
levelF   = 1 + 9 · clamp(P / TriggerPressure, 0, 1)
VimsTier = round(levelF)   // with hysteresis from §7.1
```

There is no piecewise breakpoint table; the projection is a single saturating affine map.

- `P = 0` → tier 1.
- `P = TriggerPressure = 85` → tier 10.
- `P > 85` → clamp pins `t = 1.0`; the integrator can sit far above 85 (saturation only happens visually at `1.6 × TriggerPressure = 136` inside `ComputeFovRadiusPercent`) but the **tier** signal saturates at 10 the moment pressure crosses 85.
- Mid-points: `P = 42.5` → tier ~5.5; `P = 17` → tier 2.8.

**Tier saturation at 85 is intentionally earlier than FOV saturation at 136.** They are NOT the same threshold — `VimsTier` drives the visual mask color/alpha lookup in `OverlayWindow.VimsLevels[]` (`OverlayWindow.xaml.cs:54-66`), while FOV radius is a continuous pressure-driven mapping (see §8.2).

Approximate band-boundary `P` values (post-hysteresis, see §7.1):

| Boundary | P value | Note |
|---|---|---|
| Tier 5 / Tier 6 (Grey → Amber) | `P ≈ 42.5` (levelF = 5.5) | first "be careful" |
| Tier 7 / Tier 8 (Amber → Red) | `P ≈ 66.1` (levelF = 7.5) | first "stop" |
| Tier 10 saturation | `P ≥ 85` | mask engaged + all-profile pause |

### 6.5 Habituation Offset Arithmetic — Corrected / 習慣化偏移算術 — 修正

**Audit correction (B2 / B6).** The prior whitepaper said the habituation `_offset` was "added to the effective trigger threshold". This was wrong on two counts:

1. **`_offset` is added to the FOV radius percent**, NOT subtracted from / scaled into the trigger pressure. `HabituationEngine.cs:97` exposes `HabituationOffset => (int)Math.Round(_offset)`; `MockInferenceEngine.ComposeFovRadius` (`MockInferenceEngine.cs:829-831`) composes the final radius as:

   ```
   FOV_radius = clamp(ComputeFovRadiusPercent(P) + _userFovOffset + _habituation.HabituationOffset,
                      15, 100)
   ```

   `_offset ∈ [0, HabOffsetCapPp]` percent-points of FOV radius. A positive `_offset` **widens** the clear zone (re-habituation = exposing the user to more visual stimulus). It never touches the spike trigger.

2. **The trigger relaxation is a separate mechanism: `EffectiveTrigger = TriggerPressure / personal_pressure_scale + TriggerThresholdAdjustment`** (`HabituationEngine.cs:172`, applied at `MockInferenceEngine.cs:487-493` and `:547-551`). `personal_pressure_scale ∈ [0.7, 1.5]` (`CalibrationProcessor.cs:25-26`):
   - `scale = 1.0` → `EffectiveTrigger = 85` (default).
   - `scale = 0.7` → `EffectiveTrigger ≈ 121.4` (less sensitive user).
   - `scale = 1.5` → `EffectiveTrigger ≈ 56.7` (more sensitive user).

   The math direction is "divide trigger" rather than "multiply pressure" so sensor readings stay pristine in raw form; only the threshold the user must cross to trigger a spike moves per user (inline rationale at `HabituationEngine.cs:161-179`).

**Scope qualifier (B6) — load-bearing.** `EffectiveTrigger` is consumed by **only two** sites: the spike-recurrence check (`HabituationEngine.cs:273`: `pressure >= EffectiveTrigger * 0.60`) and the calm-fraction check (`:288`: `pressure < EffectiveTrigger * 0.25 && !maskActive`). It is **NOT** used by:
- `MockInferenceEngine`'s mask hysteresis (lines `:243-246`, still compare against raw `SenseDefaults.TriggerPressure`).
- `ComputeFovRadiusPercent` (lines `:855-874`, raw `TriggerPressure`).
- `UpdateVimsTier` (line `:909`, raw `TriggerPressure`).

This means: under `scale ≠ 1.0`, **mask engagement, FOV radius, and tier projection still use the raw 85 threshold**, while only the habituation engine's spike registration and calm tracking use `EffectiveTrigger`. The asymmetry is design intent ("engine state ignorant of scale") but is load-bearing — a refactor that wires `EffectiveTrigger` into the mask hysteresis would silently change the trigger-vs-mask behaviour for every user with `scale != 1.0`.

**Override precedence (B6):** When `TriggerThresholdOverride` is set (the vestibular branch: `CalibrationProcessor.cs:74` sets `triggerOverride = 50`), scale + adjustment are **ignored**; the absolute integer trigger wins outright (`MockInferenceEngine.cs:480-493`).

**Personalisation confidence (B4).** `MinSamplesForFullWeight` is used **only** as the denominator of a linear confidence ramp in `HabituationEngine.SetPersonalization` (`:387-405`). It does NOT gate the engine in any other way:

```
confidence = clamp(fovHistory.Count / MinSamplesForFullWeight, 0, 1)
caution    = rawCaution * confidence
_effectiveWindow      = floor + caution × (2 × floor − floor)
_effectiveStepInterval = stepFloor + caution × (HabituationMaxStepIntervalSeconds − stepFloor)
```

`rawCaution = clamp(-fovHistory.MeanDelta / 15.0, 0, 1)` — only *tightening* history (negative MeanDelta) drives caution up; relaxing users get caution = 0. The 15.0 divisor is `SenseDefaults.HabituationCautionMeanDeltaDivisor`. A `MeanDelta` of `-15` (i.e. user has averaged -15 FOV percent points of tightening per turn) saturates rawCaution to 1.0.

---

## 7. Inference Layer State Machine / 推論層狀態機

### 7.1 Tier Hysteresis / 分層遲滯

Tier transitions are gated by asymmetric debounce plus a fast-onset/fast-release bypass.

| Constant | Value | Source |
|---|---|---|
| `VimsTierDeadband` | 0.15 | `SenseDefaults.cs:87` |
| `VimsTierUpDebounceMs` | **200** | `SenseDefaults.cs:88` |
| `VimsTierDownDebounceMs` | **500** | `SenseDefaults.cs:89` |
| `VimsTierDownDebounceLowMs` | **250** | `SenseDefaults.cs:94` |
| `VimsLowTierReleaseCutoff` | 7 | `SenseDefaults.cs:95` |
| `VimsTierFastOnsetGap` | 1.5 | `SenseDefaults.cs:99` |
| `VimsTierFastReleaseGap` | 1.5 | `SenseDefaults.cs:106` |

State table:

| From | Guard | To | Action |
|---|---|---|---|
| `current` | `levelF ≥ current + 0.5 + deadband` for 200 ms | next tier up | commit |
| `current` | `levelF ≥ current + FastOnsetGap` | snap to `round(levelF)` | bypass debounce |
| `current` | `levelF ≤ current − 0.5 − deadband` for 500 ms (or 250 ms if target ≤ 7) | next tier down | commit |
| `current` | `levelF ≤ current − FastReleaseGap` | snap to `round(levelF)` | bypass debounce |

### 7.2 Band Mapping / 帶位對映

- Tier 1–5 → **Grey**
- Tier 6–7 → **Amber**
- Tier 8–10 → **Red**

### 7.3 Mask Engagement State / 遮罩啟用狀態

```
              [pressure ≥ TriggerPressure ∧ cooldownPassed]
   ┌─────────┐ ─────────────────────────────────────────▶ ┌──────────┐
   │  Idle   │                                            │ Engaging │
   └─────────┘ ◀───────────────────────┐                  └────┬─────┘
        ▲                              │ [animation done]      │ [animation done]
        │ [pressure < 0.85·Trig.]      │                       ▼
   ┌────┴──────┐                       │                  ┌─────────┐
   │ Releasing │ ◀────────────────────┐│                  │ Active  │
   └───────────┘                      ││                  └────┬────┘
                                      │└───────────────────────┘
                       [pressure < 0.85·TriggerPressure]
```

Guards (verified at `MockInferenceEngine.cs:243-257`, **read raw `TriggerPressure`, NOT `EffectiveTrigger`** per §6.5 scope qualifier):

| Transition | Guard | Action |
|---|---|---|
| Idle → Engaging | `pressure ≥ TriggerPressure (85) ∧ now − _lastMaskChange ≥ CooldownMs (8000)` | `MaskActive = true` |
| Engaging → Active | animation complete | mask-attenuation feedback enabled |
| Active → Releasing | `pressure < TriggerPressure · 0.85 = 72.25` | `MaskActive = false` |
| Releasing → Idle | animation complete | feedback disabled |

### 7.4 Severe-Strike State and the Habituation State Machine / 累計次數與習慣化狀態機

**Audit correction (B5):** There is **no `SevereDebounceMs` constant**. The codebase contains only **tier-transition** debounces (`VimsTierUpDebounceMs`, `VimsTierDownDebounceMs`, `VimsTierDownDebounceLowMs`). Severe strikes themselves are NOT time-debounced at registration. The de-facto debounce comes from the **post-spike pause** mechanism described below.

**Strike registration (`HabituationEngine.cs:273-277`).** A strike is registered when `pressure >= EffectiveTrigger * HabituationRecurrencePressureFraction` (= 0.60). On strike: `_offset := _offset / 2`, `_severeStrikeCount += 1`. If `_severeStrikeCount ≥ SevereStrikeLimit`: `_offset := 0`, `_severeStrikeCount := 0`.

**Post-spike pause as de-facto debounce.** After a strike, `EnterPause` (`HabituationEngine.cs:451-467`) sets `_pauseCooldown` from `GetPostSpikePauseMs(vimsTier)`:

| Profile | tier ≤ 5 (`PostSpikePauseLowMs`) | tier 6–7 (`PostSpikePauseMidMs`) | tier ≥ 8 |
|---|---|---|---|
| Resilient | 90 s | 120 s | ∞ (awaits user confirm) |
| Typical | 90 s | 180 s | ∞ |
| Sensitive | 180 s | 300 s | ∞ |

During the pause, the next Step call hits the early-return at line `:279` (`if (_externallyPaused || _pauseCooldown > 0.0 || _awaitingUserConfirm) return`) and cannot register another strike. **Effective per-profile severe debounce equals these pause durations.**

**Tier-8+ uniform `PausedAwaitingUserConfirm` (audit confirmation B7).** As §2.5 noted, all three profiles enter `PausedAwaitingUserConfirm` at `vimsTier >= 8`.

**During `PausedAwaitingUserConfirm` — what is frozen vs live (B7):**

| Subsystem | Behaviour |
|---|---|
| `_pauseCooldown` | Set to `double.PositiveInfinity`; decrement skipped because `_awaitingUserConfirm` is true. Never ticks down on its own. |
| `_stableElapsed` / `_stepAccumulator` | Frozen at 0.0 (zeroed in `EnterPause`). Cannot accumulate. |
| `_offset` | Frozen — relaxation branch unreachable. Value at entry was halved or zeroed by the strike handler just before the pause. |
| `_phase` | Pinned to `HabituationPhase.Paused`. |
| `_severeStrikeCount` | Can still **increase** — the pressure-spike check at line `:273` **precedes** the pause early-return. A sensor spike during the await window triggers another `RegisterSevereStrike`, halving/zeroing offset further and overwriting `_lastSpikeAtUtc`. |
| `_lastSeenTier` | Updated every Step call. |
| **Sensor side (in `MockInferenceEngine.Update`)** | OFI continues accumulating in `LeakyIntegrator`; pressure, mask hysteresis, VIMS tier projection, FOV-radius pressure-component all update normally. `Current.MaskActive` toggles via its own hysteresis using **raw `TriggerPressure`** (NOT `EffectiveTrigger`). |
| Public state | `IsPaused = true`, `IsAwaitingUserConfirm = true`, `CurrentEngineState = PausedAwaitingUserConfirm`. |
| Exit | **Only** `AcknowledgeAndResume` clears `_awaitingUserConfirm`, zeros `_pauseCooldown`, resets `_stableElapsed` / `_stepAccumulator`, sets phase to Observing. **No timer-based exit.** |

The asymmetry between mask hysteresis (raw `TriggerPressure`) and habituation spike check (`EffectiveTrigger`) can disagree on what counts as a "spike" when `scale != 1.0`. `Current.HabituationPaused = true` is propagated to dashboard UI, but UI-side rendering (mask, overlay) is **not** visually frozen — it tracks live pressure regardless of `HabituationPaused`.

**Audit coverage gap (E4):** Engine-level tier-≥8 tests (`HabituationStrikeAndStateTests.cs:75, 96, 158, 254`) all run against default-constructed Resilient engine. Tier 8 boundary is only hit at the param layer (`HabituationProfileTests.cs:142-151`). Backlog item E4 converts the engine-level test to a `[Theory]` over `HabituationProfile × {8, 9, 10}` plus a tier-7 negative control.

### 7.5 Habituation Relaxation State Machine (Two-Stage Gated) / 習慣化放鬆狀態機（兩階段閘控）

**Audit addition (B3).** The relaxation arithmetic in `HabituationEngine.Step()` (`:286-318`) is a two-stage gated state machine:

**Stage 1 — Stability accumulator (`_stableElapsed`).**

```csharp
bool calm = pressure < trigger * HabituationStablePressureFraction (0.25)
            && !maskActive;
if (calm)
    _stableElapsed = Math.Min(_effectiveWindow, _stableElapsed + dt);
else
    _stableElapsed = Math.Max(0.0,
        _stableElapsed - (dt * HabituationMidBandDecayRate));  // 0.5
```

Time accrues while `pressure < 0.25 * EffectiveTrigger` AND `!maskActive`; otherwise it **decays** at `HabituationMidBandDecayRate = 0.5` (`SenseDefaults.cs:69`). Decay (not instant reset) is the load-bearing detail — non-contiguous calm cannot ratchet, but a brief jitter doesn't erase prior progress.

**Stage 2 — Step gate.**

```csharp
if (_stableElapsed >= _effectiveWindow)
{
    _phase = HabituationPhase.Relaxing;
    _severeStrikeCount = 0.0;  // "earn a clean slate"
    _stepAccumulator += dt;
    while (_stepAccumulator >= _effectiveStepInterval
           && _offset < _params.HabOffsetCapPp)
    {
        _stepAccumulator -= _effectiveStepInterval;
        _offset = Math.Min(_params.HabOffsetCapPp,
                           _offset + _params.StepMagnitudePp);
    }
}
```

Once `_stableElapsed >= _effectiveWindow`, time begins banking into `_stepAccumulator`. Every full `_effectiveStepInterval` consumed bumps `_offset += StepMagnitudePp`, clamped at `HabOffsetCapPp`. The `while`-loop drains the accumulator (handles dt > step interval gracefully).

**Per-profile cadence** (from §2.5 table). `_effectiveWindow` / `_effectiveStepInterval` are personalised in `SetPersonalization` (lines `:387-405`): linear interp by `caution` between profile-floor and `2 × window` / `HabituationMaxStepIntervalSeconds = 75 s` ceiling.

**Side-effect — strike-counter zeroing.** A full window pass zeros `_severeStrikeCount` (line `:302`) — the user "earns a clean slate". This couples §7.5 to §7.4.

### 7.6 Severe-Strike State Summary

A **strike** is registered on `pressure >= EffectiveTrigger * 0.60`; on strike: `_offset := _offset / 2`, `_severeStrikeCount += 1`. Reaching `SevereStrikeLimit` zeroes `_offset` and resets the counter. The blanket user-confirm gate at tier ≥ 8 is the load-bearing safety floor.

---

## 8. Rendering Layer — Overlay / 渲染層 — Overlay

### 8.1 OverlayWindow / OverlayWindow 視窗

`OverlayWindow.xaml` is a click-through transparent topmost WPF window. The window styles are set in code-behind:

```csharp
const int GWL_EXSTYLE = -20;
const int WS_EX_LAYERED = 0x00080000;
const int WS_EX_TRANSPARENT = 0x00000020;
const int WS_EX_TOOLWINDOW = 0x00000080;
const int WS_EX_TOPMOST = 0x00000008;

IntPtr hwnd = new WindowInteropHelper(this).Handle;
int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
SetWindowLong(hwnd, GWL_EXSTYLE,
    ex | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_TOPMOST);
```

### 8.2 FOV Radius — Composition, Range, and Pressure Mapping / FOV 半徑 — 組合、範圍與壓力對映

**Audit revision (A1 / A2).** `FovRadiusPercent` is an integer percentage stored on `SenseState`. The clear-zone inner radius is composed in `MockInferenceEngine.ComposeFovRadius` (`MockInferenceEngine.cs:829-831`) from three contributors:

```
FOV_radius = clamp(ComputeFovRadiusPercent(pressure)
                 + _userFovOffset
                 + _habituation.HabituationOffset,
                 15, 100)
```

- **Pressure-driven base** — `ComputeFovRadiusPercent(pressure)` (see below).
- **Negative-only user feedback offset** — `_userFovOffset`, range `[-60, 0]` per `ApplyUserFeedback` (`MockInferenceEngine.cs:296`); chatbot tightening.
- **Non-negative habituation offset** — `_habituation.HabituationOffset`, range `[0, HabOffsetCapPp]`; engine relaxation (per §6.5 correction).
- **Hard clamp `[15, 100]`** — absolute floor and ceiling.

**Engine default (rest state) is `100`** (fully open, `MockInferenceEngine.cs:77`). The pressure-curve target at the trigger is `DefaultFovRadiusPercent = 35` (`SenseDefaults.cs:9`); the lowest the pressure curve alone can reach post-trigger is `TightestNaturalFovRadiusPercent = 15` (`SenseDefaults.cs:12`). Note `15` is both the natural-pressure floor and the absolute clamp floor: accumulated user `ComplaintFovDelta = -15` cannot drive the radius below it.

**Pressure-to-radius mapping (A2).** Piecewise-linear, in `MockInferenceEngine.ComputeFovRadiusPercent` (`MockInferenceEngine.cs:855-874`):

- **Pre-trigger band** (`pressure ≤ TriggerPressure = 85`): linear interpolation from `100%` at rest to `DefaultFovRadiusPercent = 35%` at the trigger.

  `radius = 100 − (100 − 35) × (pressure / 85)`

- **Post-trigger band** (`pressure > TriggerPressure`): linear interpolation from `35%` at the trigger to `TightestNaturalFovRadiusPercent = 15%`, saturating at `1.6 × TriggerPressure = 136`. **Note the `0.6 × trigger` denominator, not `2.0×`** — a 2025 tuning iteration steepened the post-trigger slope.

  `radius = 35 − (35 − 15) × clamp((pressure − 85) / 51, 0, 1)`

**There is no separate "tier → FOV radius" lookup table.** The discrete `VimsTier` (1..10) is computed in parallel from the same pressure scalar (`UpdateVimsTier`, `MockInferenceEngine.cs:897-988`), but it drives the visual mask color/alpha lookup in `OverlayWindow.VimsLevels[]` (`OverlayWindow.xaml.cs:54-66`), NOT the FOV radius. FOV radius is a continuous pressure-driven mapping; tier is a debounced visual presentation knob.

### 8.3 Shader Knobs — `ShaderParams` (Audit-Corrected) / 著色器參數（已修正）

**`ShaderParams` carries three normalised `[0, 1]` shader knobs** alongside other state (`ShaderParams.cs:9-42`). The full clamp table at `ShaderParams.cs:28-33`:

```csharp
public void Clamp()
{
    MaskDepth      = Math.Clamp(MaskDepth, 0.0, 1.0);
    GridVisibility = Math.Clamp(GridVisibility, 0.0, 1.0);
    FeatherWidth   = Math.Clamp(FeatherWidth, 0.0, 1.0);
}
```

| Knob | Default | Hard clamp | Source |
|---|---|---|---|
| `MaskDepth` | 0.74 | `[0, 1]` | `ShaderParams.cs:11, 30` |
| `GridVisibility` | 0.75 | `[0, 1]` | `ShaderParams.cs:12, 31` |
| **`FeatherWidth`** | **0.50** | **`[0, 1]`** (audit correction A5 — prior whitepaper said `[0, 0.5]`, which was wrong; `0.5` is the default, not the upper bound) | `ShaderParams.cs:13, 32` |

**Audit correction (A5):** The prior whitepaper's claim of `FeatherWidth ∈ [0, 0.5]` was incorrect. The actual `Clamp()` enforces `[0, 1]`. The `0.5` figure is the *default* initialiser (`DefaultFeatherWidth`, `ShaderParams.cs:13`). Because the chatbot-adjustment path can push deltas up to `+0.3` per turn (`MistralChatbotService.cs:609`), a user requesting "softer edges" two turns in a row reaches `0.5 + 0.6 = 1.1` → clamped to `1.0`, far above the prior claimed ceiling. The bound has been corrected to `[0, 1]` in this revision. Backlog item A5 documents the open design question of whether `[0, 0.5]` was ever an *intended* invariant (no source-tree evidence either way).

#### 8.3.1 `MaskDepth`

Drives the peripheral attenuation depth in the radial gradient. Encoded as `Color.FromScRgb(MaskDepth, 0, 0, 0)`.

#### 8.3.2 `GridVisibility` — Audit detail (A4)

Scales the *opacity ceiling* of the static Chang-Hain anchor grid layer in `OverlayWindow.ApplyGridIntensity` (`OverlayWindow.xaml.cs:666-695`):

```csharp
int    tier        = Math.Clamp(state.VimsTier, 1, 10);
double tierFraction = (tier - 1) / 9.0;                  // 0 at L1, 1 at L10
double gv          = Math.Clamp(state.Shader.GridVisibility, 0.0, 1.0);
double ceiling     = Math.Min(1.0, 0.40 + (0.80 * gv));
double target      = Math.Clamp(tierFraction * ceiling * (0.55 + (0.45 * _smoothedIntensity)),
                                0.0, ceiling);
```

- **Default**: `0.75` (`ShaderParams.cs:12`), tuned to reproduce a grid floor of ~0.30 and ceiling of ~1.00 at peak tier.
- **Role**: target opacity per tick is `tierFraction × ceiling × (0.55 + 0.45 × smoothedIntensity)`, where `ceiling = min(1.0, 0.40 + 0.80 × GridVisibility)` and `tierFraction = (VimsTier − 1) / 9`. The grid never appears at tier 1 (where `tierFraction = 0`); it ramps in linearly with VIMS tier and is amplified by current pressure-driven intensity.
- **Chatbot tuning**: chatbot adjusts via `IInferenceEngine.ApplyShaderAdjustment(maskDepthDelta, gridVisibilityDelta, featherWidthDelta)` (`MockInferenceEngine.cs:313`), which adds the delta then re-clamps to `[0, 1]`.

**UX gap (documented as Beta polish, A4):** The chatbot adapter `MistralChatbotService.cs:608` caps per-turn deltas to `[-0.3, 0.3]`. Because `ApplyShaderAdjustment` is **delta-only with no absolute-set API**, a user who wants to fully zero the grid must request at least three sequential "less grid" turns to walk `0.75 → 0.45 → 0.15 → 0`. Backlog: expose absolute-set intent path or raise per-turn cap on grid-only requests.

#### 8.3.3 `FeatherWidth`

Drives the inner edge offset of the radial gradient: `InnerEdgeOffset = 1.0 − FeatherWidth`.

### 8.4 Radial Gradient Brush / 徑向漸層筆刷

```xml
<Rectangle.Fill>
  <RadialGradientBrush
      GradientOrigin="{Binding Cx}, {Binding Cy}"
      Center="{Binding Cx}, {Binding Cy}"
      RadiusX="{Binding FovRadiusFraction}"
      RadiusY="{Binding FovRadiusFraction}">
    <GradientStop Offset="0.0" Color="#00000000" />
    <GradientStop Offset="{Binding InnerEdgeOffset}" Color="#00000000" />
    <GradientStop Offset="1.0" Color="{Binding MaskDepthColor}" />
  </RadialGradientBrush>
</Rectangle.Fill>
```

The mask centre `(cx, cy)` is fed either from head-tracking or hardcoded to `(0.5, 0.5)`.

### 8.5 Animations — Actual Durations (Audit-Corrected) / 動畫實際時長

**Audit correction (A6).** The prior whitepaper referenced a "600 ms mask animation". **No such 600 ms constant exists in `OverlayWindow.xaml.cs`.** The actual animation durations are inline literals at four sites:

| Animation | Duration | Location |
|---|---|---|
| Window opacity fade-in | 380 ms | `OverlayWindow.xaml.cs:209` |
| Grid opacity per-tick anim | 280 ms | `OverlayWindow.xaml.cs:191` |
| Escape-hatch fade-down | 250 ms | `OverlayWindow.xaml.cs:496` |
| Escape-hatch fade-up | 500 ms | `OverlayWindow.xaml.cs:509` |

Additionally, the FOV mask color/alpha cross-fade between VIMS tiers is implemented as a per-tick exponential ease with `MaskSmoothingFactor = 0.25` (`OverlayWindow.xaml.cs:77`), giving ~250 ms convergence at the ~15 Hz tick rate.

**None of these durations have been promoted to `SenseDefaults.cs`** — extraction is a Beta polish item (§13.6 item F2). The `TimeSpan.FromMilliseconds(600)` literal that exists in the codebase is in `ValidationModeBanner.xaml.cs:51` — a developer-mode banner fade, unrelated to the overlay mask.

### 8.6 Exclude-rect Coupling / 排除矩形耦合

The overlay bar's monitor coordinates are pushed back into `WgcSensingProvider.SetMonitorExcludeRect` whenever the bar repositions or resizes.

### 8.7 DPI and Multi-monitor — Audit Limitation (A8) / DPI 與多螢幕

**Audit gap (A8) — multi-monitor coverage is a known POC limitation.**

`OverlayWindow` is constructed once with `WindowState="Maximized"` (`OverlayWindow.xaml:11`) and maximises onto whichever monitor it instantiates on (typically the primary). Grid spacing is DPI-scaled against `SystemParameters.PrimaryScreenWidth` only (`OverlayWindow.xaml.cs:232`); there is:

- no per-monitor enumeration (`Screen.AllScreens`),
- no Display-Changed event subscription,
- no multiple-overlay-instance management,
- no per-monitor DPI awareness beyond the primary read.

Users on multi-monitor configurations get protection on a single display (primary, or the launch-time active display). If the user drags attention to a non-primary screen, the overlay does not follow, and no second overlay covers the other display.

**Full multi-monitor coverage** — one overlay per screen with independent grid scaling and per-monitor head-pose centring — is a v1.0 scope item (§13.6 item A8).

---

## 9. Rendering Layer — Bar, Tray, Card, Settings / 渲染層

### 9.1 OverlayBarWindow — 環境式狀態列

The ambient bar displays the current tier band as a coloured pill using the three-band palette from §7.2.

### 9.2 TrayIconHost — 系統匣

16×16 ARGB bitmap rendered to match the current band colour, swapped on band crossings.

### 9.3 NotificationCardWindow — 提案確認卡片

Four sections:
1. **Header** — current band colour + short status text.
2. **Quick-feedback row** — four buttons backed by `QuickDiscomfortPhrases`.
3. **Proposal section** — visible when an `_activeProposal` is pending. 15-second countdown.
4. **Resume section** — visible when the engine is in tier-8+ pause state.

**Auto-show truth matrix (band crossings):**

| prev \ new | Grey | Amber | Red |
|---|---|---|---|
| **Grey** | Stay | **Show** (header) | **Show** (suggest pause) |
| **Amber** | Suppress | Stay | **Show** (suggest pause) |
| **Red** | Suppress | Suppress | Stay |

The Card's 15-second proposal-expiry timer is distinct from the 60-second VM watchdog in `MainViewModel.StartProposalExpiryWatchdog`.

### 9.4 SettingsWindow / 設定視窗

Exposes: profile picker, head-tracking enable + follow-mode picker, chatbot enable, calibration entry, factory reset.

### 9.5 MainWindow / Dashboard / 主視窗

Hosts the chatbot surface, live OFI / pressure / tier readouts, calibration entry, Demo-mode controls.

### 9.6 CalibrationWindow / 校準視窗

Multi-stage modal running Q1–Q5 questionnaire (Barány-aligned) and a Stage-5c demo loop.

---

## 10. LLM Integration — Core / LLM 整合 — 核心

### 10.1 Transport / 傳輸

`MistralChatbotService` speaks the OpenAI-compatible `POST /v1/chat/completions` surface against `127.0.0.1:8181`.

### 10.2 Model and Backend / 模型與後端

`Ministral-3-3B-Instruct-2512` distributed in GGUF format with `Q4_K_M` quantization. Backend since v0.11.1: `runtimes/win-x64/native/vulkan/llama-server.exe`. Vulkan minor version not pinned.

Sampling parameters:
- `--ctx-size 4096`
- `--temp 0.6`
- `--top-p 0.95`
- `--repeat-penalty 1.1`

### 10.3 System Prompt Assembly / 系統提示組裝

Per-turn assembly from five blocks: (1) safety preamble, (2) `[habituation_state]`, (3) `[engine_state]`, (4) `[intent_schema]`, (5) `[exemplars]`.

### 10.4 NLU JSON Envelope / NLU JSON 信封

```json
{
  "Intent": "AdjustParameters | ReportDiscomfort | ResetRequest |
             ProfileOverride | ObservationAdd | ResumeAcknowledge | Unknown",
  "Response": "<reply text>",
  "FovDelta": <int, optional, clamped [-30, 0]>,
  "MaskDepthDelta": <double, optional, clamped [-0.3, 0.3]>,
  "GridVisibilityDelta": <double, optional, clamped [-0.3, 0.3]>,
  "FeatherWidthDelta": <double, optional, clamped [-0.3, 0.3]>
}
```

**Audit revision (C7):** The clamp ranges above match `MistralChatbotService.cs:606-609`. The prior whitepaper's `[-1.0, +1.0]` figure for `GridVisibilityDelta` and `[-0.20, +0.20]` for `FeatherWidthDelta` were inconsistent with code; the actual per-turn cap for **all three shader deltas** is `[-0.3, 0.3]`, and the FOV per-turn cap is `[-30, 0]`. See §12.1.6 for the unified bounds table.

### 10.5 Structured Override Blocks / 結構化覆寫區塊

Up to three structured blocks may accompany the JSON envelope: `[PROFILE_OVERRIDE]`, `[OBSERVATION_ADD]`, `[RESUME_ACKNOWLEDGE]`.

### 10.6 Stateless Grounding / 無狀態 grounding

The chatbot is **stateless across app restarts** (`Q4.6`, `Q4.7 = B`).

### 10.7 Calibration Sub-service / 校準子服務

`CalibrationChatbotService` shares the safety-floor implementation *by duplication* with `MistralChatbotService`.

---

## 11. LLM Integration — Safety + Override Flow / LLM 整合 — 安全與覆寫流程

### 11.1 Safety Floor — 安全底線

#### 11.1.1 Phrase Categories (Audit Categorisation C2)

The safety-floor vocabulary is organised into six categories across two languages. The audit was performed without recording the verbatim phrase strings in this document (the trigger phrases themselves are in `MistralChatbotService.cs:858-871` and the identical copy in `CalibrationChatbotService.cs:296-309`):

| Category | Language | Count |
|---|---|---|
| Self-harm explicit | zh | 3 |
| Means/method | zh | 2 |
| Suicidal ideation | zh | 6 |
| Self-harm explicit | en | 4 |
| Method/finality | en | 3 |
| Suicidal ideation | en | 5 |

**Tuning rationale.** Phrase tables are tuned to favour false-positives over misses — a false positive on benign phrasing is not catastrophic, but missing a real distress signal is.

#### 11.1.2 Decision Logic

1. `IsHan(userText)` decides language preference (any U+4E00..U+9FFF character → zh).
2. If `zh`, scan zh phrases first using whole-substring `Ordinal` match.
3. Then scan en phrases against `ToLowerInvariant()` text (case-insensitive).
4. Match → emit hard-coded localized crisis response, return true. No LLM call, no NLU.

#### 11.1.3 Response Strategy (without verbatim sensitive content)

- Opens with empathic acknowledgement that the user is struggling.
- Explicit non-medical-device disclaimer naming SenseEase.
- Two crisis resources per locale: a national/regional line (TW 1925 for zh, US 988 for en) plus international `findahelpline.com`.
- Mixed-script users in zh context get zh response even if matched on en phrase.

#### 11.1.4 Execution Differential (Audit C2)

The two services trigger the floor at different lifecycle points with different downstream consequences:

| Service | Call site | On trigger |
|---|---|---|
| `MistralChatbotService` | top of `SendAsync` (before model load, NLU, lexicon — pinned by `MistralChatbotServiceSafetyFloorTests.SendAsync_SafetyPhrase_ShortCircuitsBeforeModelLoad`) | returns crisis response inside the normal NluResult (no zeroed deltas), session continues, history records the exchange |
| `CalibrationChatbotService` | top of `SendUserMessageAsync` (`:133-138`) | fires a `SafetyFloorTriggered` event AND invokes `EndSession()` — calibration session terminated |

This differential is deliberate. The general chatbot keeps the user able to continue conversing after the crisis response is delivered; the calibration session ends because its question loop would otherwise resume after the response and ask "ok, on a scale of 1-5 how bad does motion bother you?" — clinically inappropriate immediately after a distress signal.

#### 11.1.5 Drift Status (Audit C3)

The phrase tables are **byte-for-byte identical** between the two services. However, the crisis-response strings have **a known 1-character typographic divergence**:

- `CalibrationChatbotService.cs:330` uses full-width colon: `請立即聯繫專業協助：台灣...`
- `MistralChatbotService.cs:892` uses ASCII colon: `請立即聯繫專業協助:台灣...`

Functionally harmless (both mention 1925 and findahelpline), but indicates the "copy-paste, maintain separately" pattern is already drifting. **No drift-prevention test exists** (no `Assert.Equal(SafetyFloorPhrasesZh, …)` parity assertion). Alpha-1 backlog item C3 fixes the typographic divergence and adds the parity test.

### 11.2 `HabituationOverrideParser` — 結構化覆寫區塊解析

```
[PROFILE_OVERRIDE]
{ "intent": "adjust_sensitivity" | "switch_profile" | "adjust_strike_limit",
  "target_field": <string>, "proposed_value": <value>,
  "rationale": "<reason>", "expires_at_iso": "<UTC>" }
[/PROFILE_OVERRIDE]

[OBSERVATION_ADD]
<one-line natural language observation>
[/OBSERVATION_ADD]

[RESUME_ACKNOWLEDGE]
{ "user_confirmed": true|false, "user_quote": "<verbatim>" }
[/RESUME_ACKNOWLEDGE]
```

`OverrideIntent` enum (`HabituationOverrideParser.cs:313-318`) deliberately enumerates only three values; `ResetOffset` is **DELIBERATELY absent** per Q4.5 sprint-state-locked decision.

### 11.3 `HabituationOverrideProcessor` — 五條驗證規則

1. **Rule 1 — Expiry window.** `expires_at_iso` must be in the future, and not more than `ExpiresAtMaxFutureDays = 7` days out.
2. **Rule 2 — Value range.** Sensitivity ∈ [0.7, 1.5], known profile, strike-limit ∈ [1, 5].
3. **Rule 3 — Vestibular hard cap.** Vestibular advisory + relaxation → REJECT.
4. **Rule 4 — One proposal at a time.**
5. **Rule 5 — 7-day decline cooldown.** `RecentDeclineCooldown = TimeSpan.FromDays(7)` (`HabituationOverrideProcessor.cs:64`).

**Latent bug (audit F1):** The rejection message at `HabituationOverrideProcessor.cs:137` says `"declined within the last {ExpiresAtMaxFutureDays} days"` when it should reference the cooldown days. They equal 7 today, but they're two policies. Alpha-1 backlog item F1 fixes this cosmetic bug as part of the §12.1.4 migration.

### 11.4 `HabituationOverrideApplicator` — 防禦性攔截與引擎寫入

#### 11.4.1 `ApplyAccepted` — 寫入前重檢

Runs the defensive intercept BEFORE the engine write — even though the Processor already cleared the proposal at propose-time.

#### 11.4.2 Three Intercept Rules — M8 Gating / 三條攔截規則

In **most-specific-first precedence order** (`HabituationOverrideApplicator.cs:143-179`):

1. **Rule 1 — Vestibular strike-limit hard cap.** Vestibular advisory + strike-limit-1 + `AdjustStrikeLimit` proposal → "strike limit was hard-set by vestibular calibration; chatbot cannot override".
2. **Rule 2 — Vestibular + relaxation.** Vestibular advisory + ANY relaxation proposal → "vestibular advisory active; relaxation proposals require manual Settings change".
3. **Rule 3 — HighRiskCombo + significant relaxation.** HighRiskCombo advisory + significant relaxation (`SignificantRelaxationThreshold = 0.15`, see §12.1.4) → "high-risk-combo advisory; small adjustments only via this channel".

**Coverage status (audit C6):** Tests exist for the canonical scenarios (`HabituationOverrideApplicatorTests.cs` exercises Rule 1 at line `:173-183`, Rule 2 at `:127-143`, Rule 3 at `:146-170`). Gaps: HighRiskCombo + AdjustStrikeLimit not tested; HighRiskCombo + SwitchProfile not tested; Rule 1 negative (vestibular + strike-limit-2) not tested; no precedence-order test where a proposal satisfies multiple rules. Backlog item C6 covers the per-rule × per-intent matrix.

#### 11.4.3 Engine Surface / 引擎介面

- `SwitchProfile` → `_habituation.SetProfile(profile, source: "chatbot_override")`.
- `AdjustSensitivity` → writes `personal_pressure_scale` via `_habituation.SetPersonalScale(value, source)`.
- `AdjustStrikeLimit` → writes `severe_strike_limit` via `_habituation.SetSevereStrikeLimit(value, source)`.

#### 11.4.4 `RecordDeclined` and `RecordExpired`

Records to the `_actionLog` without touching the engine.

#### 11.4.5 `ChatbotActionLog` States / 動作日誌狀態

Five states: `Proposed`, `Accepted`, `Declined`, `Expired`, `Intercepted`.

### 11.5 `ProposalReceived` Flow / 提案接收流程

1. **Snapshot + validation.** `SafeLoadSnapshot` loads the persisted profile.
2. **Reject branch.** Logs `"Habituation.SafetyIntercept"` and returns.
3. **Accept branch.** Clamp expiry to `min(model expiry, now + 7 days)`; store `_activeProposal`; record `Proposed`; start the **60-second VM watchdog**; raise `ProposalSurfaceRequested`.

#### 11.5.1 Timer Rationale / 計時器設計依據

- **Card timer = 15 s.** The user-facing "respond now" budget.
- **VM watchdog = 60 s.** A safety net for headless / Card-closed-early scenarios.

Either timer is idempotent via the `is not { } p` guard in `ExpireActiveProposal`.

### 11.6 Calibration Pipeline / 校準管線

`CalibrationProcessor.Process` pure logic:
- **Step 1:** Q3 (intensity 1–5) + Q4 (comparison) → base profile.
- **Step 2:** Q5 risk-factor escalation. **Vestibular hard-locks to** `Sensitive`, `trigger=50` (audit C1 — currently a magic literal at `CalibrationProcessor.cs:74`; Alpha-1 backlog extracts to `VestibularTriggerPressureOverride`), `strikeLimit=1`, `medicalAdvisory="vestibular"`.
- **Step 3:** `personal_pressure_scale` lookup from `docs/HABITUATION-PROFILE.md §3`.
- **Step 4:** Vestibular cap clamps scale to ≤ 1.0.

**`CalibrationConfidence` — audit gap (C4).** Computed in `CalibrationProcessor.ComputeCalibrationConfidence` (`:297-320`) with rules: Abandoned → Low; Q3=1 with ≥2 risk factors → Low (internal contradiction); Q1 signal conflict → Low; Partial completeness → Medium; Complete + Q1 aligns with sensor peak → High; else Medium. **The field is persisted on `HabituationProfileSnapshot.cs:177` but has zero downstream consumers.** A grep across the entire `src/` tree for `CalibrationConfidence.Low|Medium|High` returns only the producer and DTO definitions. Low confidence does not downgrade application, prompt a re-do, reduce chatbot reliance, or defer `_loadedCalibrationCount` bumps. The comment at `CalibrationProcessor.cs:306` mentions "Flag as low confidence so the chatbot can probe in subsequent sessions" — but no chatbot code probes based on this field. **Either a downstream effect must be wired (Alpha-2 backlog item C4) or the field must be documented as diagnostic-only with pinned tests.**

**Q1-vs-sensor-median alignment** drives the `CalibrationConfidence` rating.

`CalibrationApplicator.Apply` calls `engine.ApplyCalibrationResult(result)`.

### 11.7 Profile Picker / 體質檔選擇器

The user's pick is **NOT** routed through the propose-confirm Card; it goes directly through the Applicator, inheriting the defensive-intercept layer.

### 11.8 NLU JSON Reply Parsing — including `ResetRequest` audit gap / NLU JSON 回覆解析

For non-override requests, `ChatbotViewModel.ApplyNluResult` consumes the `NluResult`:

- `FovDelta` → `_engine.ApplyUserFeedback(result.FovDelta)`.
- `MaskDepthDelta`, `GridVisibilityDelta`, `FeatherWidthDelta` → `_engine.ApplyShaderAdjustment(...)`.

**`Intent == "ResetRequest"` triggers `_engine.ResetPersonalization()` — and contradicts Q4.5 (audit gap C5).**

The Q4.5 locked decision states the chatbot reset must only clear shader values, NOT the habituation offset. The current implementation at `MockInferenceEngine.cs:324-338` calls `_habituation.Reset()`, which in `HabituationEngine.cs:433-446` explicitly sets `_offset = 0.0` along with strike count, stable elapsed, step accumulator, pause cooldown, externally-paused, awaiting-user-confirm, and phase. **A user who has slowly built up habituation over weeks loses ALL of that earned relaxation on a single "reset" utterance.**

`HabituationEngine.Reset()` also flips `_externallyPaused = false` (line `:332`) — leaking the user's explicit pause opt-in at startup.

**Zero tests pin this contract** (grep for `ResetPersonalization` in tests returns zero matches). Alpha-1 backlog item C5 splits `HabituationEngine.Reset()` into `ResetPostCalibration()` and `ResetTransientState()`, and routes `ResetPersonalization` to the latter to preserve `_offset`.

### 11.9 Lexicon Backfill — `QuickDiscomfortPhrases`

```csharp
private static readonly string[] QuickDiscomfortPhrases = {
    string.Empty,
    "我有點不舒服",
    "我不舒服",
    "我很不舒服",
    "我非常不舒服，請大力調整",
};
```

### 11.10 End-to-End Summary / 端到端摘要

A user message traverses: safety-floor short-circuit → LLM round-trip → parser → 5-rule Processor → if accepted, `_activeProposal` stored → Card surfaces → user clicks → Applicator re-runs defensive intercepts → engine write → `ChatbotActionLog` records.

---

## 12. Design Decisions + Complete Parameter Catalog / 設計決策與完整參數目錄

### 12.1 SenseDefaults — 集中參數來源

All load-bearing parameters live in `src/SenseEase.Core/Configuration/SenseDefaults.cs`.

#### 12.1.1 Sensing-Layer Parameters / 感測層參數

| Constant | Value | Purpose | Source | Why this value | What breaks if changed | rationale_zh |
|---|---|---|---|---|---|---|
| `WgcCaptureIntervalMs` | 66 | WGC capture period (~15 Hz) | `SenseDefaults.cs:36` | 1 capture per 2 display frames at 60 Hz | <40 ms: CPU doubles; >100 ms: tier-up latency exceeds debounce | 每 2 個顯示框擷取一次 |
| `WgcDownsampleWidth / Height` | 160 / 90 | Greyscale buffer | `:37-38` | 16:9 preserved; per-frame ~14 KB stays in L1 cache | smaller: MAD noisy; larger: cache miss | 維持 16:9 |
| `WgcMadNormFactor` | 4.0 | MAD → OFI scale | `:42` | **inline-comment hypothesis** — no measured calibration data exists (audit D1) | lower: trigger never reached on real content; higher: idle jitter trips noise gate | 校至 Cursor OFI 量級（尚未實測） |
| `CursorVelocityPollIntervalMs` | 33 | Cursor poll (~30 Hz) | `:30` | matches Win32 mouse refresh granularity | lower: WM_INPUT overhead; higher: aliasing | 對齊滑鼠刷新粒度 |
| `CursorVelocityNormFactor` | 120.0 | px/s → OFI scale | `:29` | **inline-comment hypothesis** — no measured calibration data exists (audit D1) | lower: every cursor blink alarming; higher: gaming sweeps don't register | 80 px/s per OFI 單位（尚未實測） |
| `CursorEmaAlpha` | 0.55 | Shared EMA alpha | `:33` | balances onset latency with jitter rejection | <0.4: flicker; >0.7: 600 ms+ onset lag | 平衡延遲與抖動 |
| `VimsMedianTaps` | 3 | Running-median window | `:81` | 5-tap added 333 ms WGC-only delay | larger: slow; smaller: scene-cut spikes leak | 3-tap 是甜蜜點 |
| `NoiseGateThreshold` | 8 | Sub-threshold OFI → 0 | `:16` | static desktop residual jitter ≤ 6 | lower: drift accumulates; higher: gentle scroll never registers | 桌面殘餘抖動 ≤6 |

#### 12.1.2 Inference-Layer Parameters / 推論層參數

| Constant | Value | Purpose | Source | Why this value | What breaks if changed | rationale_zh |
|---|---|---|---|---|---|---|
| `PassiveFlowWeight` | 2.0 | Passenger-mode flow multiplier | `:18` | combined with Driver ×0.25 yields 8× asymmetry (WH, no ratio test — audit D2) | <1.5: efference-copy inverts; >3: Passenger trips on every scroll | 與 Driver ×0.25 共組 8× 不對稱 |
| `QuadraticTerm` | 0.0008 | Quadratic stimulus coefficient | `:19` | small enough to be gentle convexity | =0: pure linear loses spike sensitivity; ≥0.005: dominates | 輕微凸性 |
| `BaseDecayAlpha` | 0.965 | Per-100 ms decay anchor (τ ≈ 2.81 s) | `:21` | τ ≈ 2.81 s — no convergence test pins this (audit D3) | >0.98: pressure never decays; <0.93: even sustained flow can't reach trigger | τ≈2.81s 為中等記憶 |
| `TriggerPressure` | 85 | Mask-engagement threshold | `:17` | calibrated so OFI ≈ 30 sustained for ~14 s (5τ) reaches trigger (audit D3 — no test) | lower: false-positive on normal scroll; higher: severe symptoms before mask | 校至 OFI≈30 持續 14s 觸發 |
| `MaskReleaseFactor` (`MockInferenceEngine.cs:244`) | 0.85 | Release at 0.85 × Trigger (= 72.25) | inline | gap of 12.75 prevents oscillation | =1.0: oscillation; <0.7: mask sticks | 與穩態σ相比足夠 |
| `CooldownMs` | 8000 | Min interval between mask engagements | `:23` | covers transient calm-then-spike | <3 s: user-perceived flicker; >15 s: missed second wave | 跨越退場過渡 |
| `TightestNaturalFovRadiusPercent` | 15 | Tightness denominator + clamp floor | `:12` | 15 % of screen ≈ central 8° ≈ functional fovea | smaller: foveal text occluded; larger: feedback loop ineffective | ≈中央視野 8° |
| `DefaultFovRadiusPercent` | 35 | Pre-trigger pressure curve target | `:9` | pressure curve hits 35 at TriggerPressure | wider: mask too soft at trigger; narrower: visual jolt | 觸發時的目標視野 |
| `OuterTransitionRadiusPercent` | 45 | Tightness numerator window edge | `:13` | gives 10-point span for `ComputeMaskTightness` denominator | =DefaultFovRadiusPercent: divide-by-zero (guarded by Math.Max(1)); larger span: feedback loop saturates later | 與預設半徑共組 10 點窗 |
| `HabituationStablePressureFraction` | 0.25 | Calm-fraction threshold | `:65` | "calm" = below 25% of trigger | higher: relaxation never advances; lower: brief calm advances offset | 達標壓力的安靜判定 |
| `HabituationRecurrencePressureFraction` | 0.60 | Spike-recurrence threshold | `:67` | 60% of trigger registers as severe strike | higher: misses moderate spikes; lower: every minor bump is severe | 中度尖峰標準 |
| `HabituationMidBandDecayRate` | 0.5 | `_stableElapsed` decay rate (per dt) | `:69` | non-contiguous calm decays at half-time, doesn't reset | =0: any calm credit retained forever; =1: full reset on jitter | 中段抖動的容忍率 |
| `HabituationCautionMeanDeltaDivisor` | 15.0 | rawCaution saturation divisor | `:73` | MeanDelta=-15 saturates caution=1.0 | smaller: every nudge maxes caution; larger: caution barely engages | 平均偏移飽和點 |
| `HabituationMaxStepIntervalSeconds` | 75 | Cautious-user step ceiling | inline | ≈ 2.5× Resilient floor; provides cautious headroom | smaller: caution ineffective; larger: Sensitive never advances | 最大步長上限 |
| `VimsTierDeadband` | 0.15 | Half-band width in level units | `:87` | matches steady-state σ of levelF | =0: every fluctuation transitions; >0.3: meaningful changes filtered | 與抖動量級相當 |
| `VimsTierUpDebounceMs` | 200 | Tier-up debounce | `:88` | ~3 frames at 15 Hz | <100: noise leaks; >400: feels delayed | 約 3 個 WGC 框 |
| `VimsTierDownDebounceMs` | **500** | Tier-down debounce | `:89` | halved from 1200 ms after user feedback | <250: flickers off; >800: "stuck darkness" | 由 1200ms 減為 500ms |
| `VimsTierDownDebounceLowMs` | 250 | Tier-down debounce for tiers ≤ 7 | `:94` | low tiers clear briskly | =DownDebounceMs: low tiers slow; <100: jitter | 低分層快速釋放 |
| `VimsLowTierReleaseCutoff` | 7 | Threshold for fast-release path | `:95` | tiers 8+ retain slow release | <7: Red band release brisk (unsafe feeling) | 紅帶仍慢釋放 |
| `VimsTierFastOnsetGap` | 1.5 | Snap-up bypass threshold | `:99` | 1.5 levels in one tick is unmistakable escalation | <1.0: false snaps; >2.5: snap never fires | 清楚的瞬時升階 |
| `VimsTierFastReleaseGap` | 1.5 | Snap-down bypass threshold | `:106` | mirror of fast-onset | absence: "stuck darkness" residue | 鏡像快速升階 |

#### 12.1.3 Habituation-Profile Parameters / 習慣化體質參數

Verified against `HabituationProfile.cs:138-175`:

| Profile | `StableWindowMs` | `StepIntervalMs` | `StepMagnitudePp` | `PostSpikePauseLowMs` | `PostSpikePauseMidMs` | `HabOffsetCapPp` | `MinSamplesForFullWeight` | `SevereStrikeLimit` |
|---|---|---|---|---|---|---|---|---|
| **RESILIENT** (default) | 120 000 | 30 000 | 2.0 | 90 000 | 120 000 | 30 | 3 | 2 |
| **TYPICAL** | 180 000 | 45 000 | 1.5 | 90 000 | 180 000 | 25 | 4 | 2 |
| **SENSITIVE** | 360 000 | 60 000 | 1.0 | 180 000 | 300 000 | 15 | 6 | 1 |

**Tier-8+ behaviour is uniform across profiles.** **Shader axes are NOT profile-differentiated** (audit gap A7 — see §2.5).

#### 12.1.4 Override / Chatbot Policy Values — Migration Required / 覆寫與聊天政策值（待遷移）

**Audit gap F1.** All eight values below currently live OUTSIDE `SenseDefaults`. Backlog item F1 (Alpha-1) migrates them.

| Symbol | Value | Purpose | **Current location** | Migration target |
|---|---|---|---|---|
| `PersonalPressureScaleMin / Max` | 0.7 / 1.5 | Sensitivity clamp | **DUPLICATED**: `CalibrationProcessor.cs:25-26` AND `HabituationOverrideProcessor.cs:53-54` | `SenseDefaults.PersonalPressureScaleMin/Max`; have both processors reference it |
| `SevereStrikeLimitMin / Max` | 1 / 5 | Strike-limit clamp | `HabituationOverrideProcessor.cs:59-60` | `SenseDefaults.SevereStrikeLimitMin/Max` |
| `ExpiresAtMaxFutureDays` | 7 | Hard upper clamp on proposal expiry | `HabituationOverrideProcessor.cs:49` (read by `MainViewModel.cs:230`) | `SenseDefaults.ProposalExpiresAtMaxFutureDays` |
| `ProposalExpiryTargetHours` | 48 | Model-prompt target | **THREE call-sites**: `HabituationProposalLexicon.cs:47`, `MainViewModel.cs:267`, `MistralChatbotService.cs:128` (URGENT triple-literal) | `SenseDefaults.ChatbotProposalExpiryTargetHours` |
| `RecentDeclineCooldown` | TimeSpan.FromDays(7) | Rule-5 cooldown | `HabituationOverrideProcessor.cs:64` | `SenseDefaults.ChatbotProposalRecentDeclineCooldownDays` |
| `CardCountdownSec` | 15 | Card-side proposal timer | `NotificationCardWindow.xaml.cs:186` | `SenseDefaults.NotificationCardCountdownSec` |
| `WatchdogTimeoutSec` | 60 (TimeSpan.FromMinutes(1)) | VM-side proposal watchdog | `MainViewModel.cs:299` | `SenseDefaults.ProposalExpiryWatchdogSec` |
| `SignificantRelaxationThreshold` | 0.15 | M8 high-risk gate | `HabituationOverrideApplicator.cs:172` (literal named-arg) | `SenseDefaults.HighRiskComboSignificantRelaxationThreshold` |

**Cross-layer invariant to document (audit F1):** `NotificationCardCountdownSec ≤ ProposalExpiryWatchdogSec`. Currently 15 ≤ 60. This is the Phase 5 Step 7 handshake; the watchdog must outlast the Card timer or the headless / Card-closed-early scenarios race. The invariant is currently load-bearing but only verified by happens-to-work coincidence.

**Latent cosmetic bug:** `HabituationOverrideProcessor.cs:137` rejection message says `"declined within the last {ExpiresAtMaxFutureDays} days"` when it should reference the cooldown days. Both equal 7 today; if either changes independently, the message goes wrong. Alpha-1 fixes this as part of F1.

#### 12.1.5 Additional Migration Candidates (Tier-2 / Tier-3) — Audit F2 / 額外遷移候選

**Tier-2 — UX animation/polling constants (currently scattered):**

| Symbol | Value | Current location | Suggested name |
|---|---|---|---|
| Window opacity fade-in | 380 ms | `OverlayWindow.xaml.cs:209` | `UiOverlayFadeInMs` |
| Grid opacity per-tick anim | 280 ms | `OverlayWindow.xaml.cs:191` | `UiGridAnimMs` |
| Escape-hatch fade-down | 250 ms | `OverlayWindow.xaml.cs:496` | `UiOverlayFadeDownMs` |
| Escape-hatch fade-up | 500 ms | `OverlayWindow.xaml.cs:509` | `UiOverlayFadeUpMs` |
| `MaskSmoothingFactor` | 0.25 | `OverlayWindow.xaml.cs:77` | `VimsMaskSmoothingFactor` |
| `BrushRadius` | 0.85 | `OverlayWindow.xaml.cs:20` | `OverlayBrushRadiusBaseline` |
| `BrushRadiusCornerMargin` | 1.10 | `OverlayWindow.xaml.cs:43` | `OverlayBrushCornerMargin` |
| Ramp geometry tuple | (0.35, 0.95, 8) | DUPLICATED at `OverlayWindow.xaml.cs:102-105, 645-648` | `OverlayRampClearZoneStart / PeakZoneStart / StopCount` |
| Grid EMA α | 0.85 / 0.15 | `OverlayWindow.xaml.cs:671` | `GridIntensityEmaAlpha` |
| Card fade-in | 220 ms | `NotificationCardWindow.xaml.cs:370, 392` | `UiCardFadeInMs` |
| Card idle timeout | 60 s | `NotificationCardWindow.xaml.cs:45` | `CardIdleTimeoutSec` |
| Card fade duration | 5 s | `NotificationCardWindow.xaml.cs:50` | `CardFadeDurationSec` |
| Chatbot LLM-context window | 6 turns | `ChatbotViewModel.cs:35` | `ChatbotContextWindowTurns` |
| Chatbot persisted history max | 100 | `ChatbotViewModel.cs:39` | `ChatbotPersistedHistoryMax` |

**Tier-3 — Cross-layer literal duplication (governance failures):**

- `OFI / 120.0` at `OverlayWindow.xaml.cs:699` should reference existing `SenseDefaults.CursorVelocityNormFactor`.
- `ChartTriggerLine = 85.0` at `MainViewModel.cs:889` should reference existing `SenseDefaults.TriggerPressure`.
- Ramp geometry `(0.35, 0.95, 8)` is literal-duplicated in two methods of `OverlayWindow.xaml.cs`.

**Rule to be added to `SenseDefaults.cs` header doc-comment:** *"any tuning constant referenced by 2+ files MUST live in SenseDefaults, regardless of whether it is 'engine' or 'UI'"*.

#### 12.1.6 Delta Bounds Table — Single Source of Truth (Audit C7) / 增減量界限表

**Per-call clamp ranges (LLM-path AND deterministic-lexicon path, must match):**

| Knob | Per-call clamp | Source (LLM) | Source (lexicon) |
|---|---|---|---|
| FovDelta | `[-30, 0]` | `MistralChatbotService.cs:606` | `FeedbackLexicon.cs:220` |
| MaskDepthDelta | `[-0.3, +0.3]` | `MistralChatbotService.cs:607` | `FeedbackLexicon.cs:221` |
| GridVisibilityDelta | `[-0.3, +0.3]` | `MistralChatbotService.cs:608` | `FeedbackLexicon.cs:222` |
| FeatherWidthDelta | `[-0.3, +0.3]` | `MistralChatbotService.cs:609` | `FeedbackLexicon.cs:223` |

**Accumulated bounds (engine-side):**

| Field | Accumulated clamp | Source |
|---|---|---|
| `_userFovOffset` | `[-60, 0]` (sum of FOV deltas) | `MockInferenceEngine.cs:296` |
| `_shader.MaskDepth` | `[0, 1]` | `ShaderParams.cs:30` |
| `_shader.GridVisibility` | `[0, 1]` | `ShaderParams.cs:31` |
| `_shader.FeatherWidth` | `[0, 1]` (NOT `[0, 0.5]`, audit A5 correction) | `ShaderParams.cs:32` |

**Final composed FOV radius (after all contributors):**

| Value | Composed clamp | Source |
|---|---|---|
| `FOV_radius` | `[15, 100]` | `MockInferenceEngine.cs:830` |

**Severity → magnitude lookup (deterministic lexicon path, `FeedbackLexicon.cs:208-210`):**

| Severity | FovDelta | MaskDepthDelta | GridVisibilityDelta |
|---|---|---|---|
| 1 (slight) | -6 | 0.05 | 0.03 |
| 2 (mild) | -10 | 0.08 | 0.03 |
| 3 (strong) | -15 | 0.12 | 0.06 |
| 4 (severe) | -22 | 0.18 | 0.06 |
| 5 (extreme) | -30 | 0.25 | 0.06 |

Directional hints (`FeedbackLexicon.cs:152-156`) **beat severity defaults**: `hintMd / hintGv / hintFw` overwrite the severity-derived values when present. Each hint contributes ±0.15. This precedence is currently undocumented in inline comments; Alpha-2 backlog item C7 surfaces it as a doc-comment + tests pin per-severity magnitudes.

**Asymmetry note:** FOV has an explicit floor of `15%` in `MockInferenceEngine.cs:830` while shader knobs allow full `0.0` floor. The asymmetry is design intent (FOV at 0% would visually destroy the screen; shader at 0 simply turns off that visual element) but is not currently justified in inline comments.

#### 12.1.7 User-Activity Detection / 使用者活動偵測

| Constant | Value | Purpose | Source |
|---|---|---|---|
| `PollIntervalMs` | 1000 | `GetLastInputInfo` cadence | `UserActivityModeDetector.cs:53` |
| `DriverThresholdMs` | 2500 | Passenger → Driver | `:51` |
| `PassengerThresholdMs` | 5000 | Driver → Passenger | `:52` |

#### 12.1.8 Safety Floor Constants / 安全底線常數

| Constant | Value | Purpose | Source |
|---|---|---|---|
| `TriggerPressure` | 85 | Default mask-engagement threshold | `SenseDefaults.cs:17` |
| **Vestibular trigger override** | **50** | Calibration vestibular-branch hard override | `CalibrationProcessor.cs:74` (currently magic literal — audit C1) |
| **Vestibular strike-limit override** | **1** | Calibration vestibular-branch hard override | `CalibrationProcessor.cs:74` |
| `VestibularPersonalPressureScaleCap` | 1.0 | Step-4 vestibular cap | `CalibrationProcessor.cs` (named constant exists) |
| `HabituationRecurrencePressureFraction` | 0.60 | Spike-recurrence threshold | `SenseDefaults.cs:67` |
| `HabituationStablePressureFraction` | 0.25 | Calm-fraction threshold | `SenseDefaults.cs:65` |

The vestibular-branch overrides give vestibular users a 41% lower trigger than default (50 vs 85) — the strictest safety envelope. Combined with strike-limit-1 the user enters spike-mode much sooner. Backlog item C1 extracts `50` to a named constant `VestibularTriggerPressureOverride`.

### 12.2 Locked Decisions / 鎖定決策

- **Q4.5** — Chatbot cannot propose `ResetOffset`. **CURRENTLY VIOLATED** — `ResetPersonalization` zeros habituation offset; see audit gap C5 and Alpha-1 backlog.
- **Q4.6** — Chatbot is stateless across restarts.
- **Q4.7 = B** — `ChatbotActionLog` is in-memory only.
- **Path Z default** — `AppConfig.Mode = Wgc`.
- **Default profile** — RESILIENT.
- **3-band UI / 10-tier engine** — engine source-of-truth is 10 tiers; UI surfaces only 3 bands.
- **Tier ≥ 8 user-confirm gate is profile-independent.**
- **Safety-floor duplication** — currently drifted by 1 character; see audit C3 and Alpha-1 backlog.
- **OBS-3 head-tracking** — IPC schema is the 8-field `SidecarFrameDto`; no schema test pins the allowlist (audit E3, Alpha-1 backlog).

---

## 13. POC Validation + Process Discipline + Roadmap / POC 驗證、流程紀律與未來路線圖

### 13.1 Headline Validation Results / 驗證結果摘要

| Gate | Status | Evidence pointer |
|---|---|---|
| G_final | **GO** | `head-tracking-sprint-state.md` |
| UVHS-V11 | **PASS** | `uvhs-playbook.md` |
| Locked-decision violations | **0** in CI; **2 known but uncovered** by tests (Q4.5 `ResetPersonalization`, safety-floor parity) — see §13.6 | sprint-state register |
| `dotnet test` | **672 / 0 / 1** | `SenseEase.Tests` |
| Vulkan cross-vendor backend | shipping (v0.11.1) | commit `2679e51` |
| Portable head-tracker sidecar | shipping (v0.11.1) | commit `2679e51` |
| Inno Setup installer | shipping (v0.11) | commit `18005fa` |

### 13.2 UVHS Playbook — 4 Anti-cheat Clauses

1. **Visual evidence required.**
2. **No same-process trust.**
3. **No self-attestation.**
4. **No silent fallbacks.**

### 13.3 Advisor Gates G1–G7 / 顧問門 G1–G7

| Gate | Reviews | Date passed |
|---|---|---|
| **G1** | Architecture | 2026-05-22 |
| **G2** | Privacy | 2026-05-23 |
| **G3** | Performance | 2026-05-24 |
| **G4** | Safety / intercept | 2026-05-25 |
| **G5** | UX | 2026-05-27 |
| **G6** | Test coverage | 2026-05-29 |
| **G7** | Documentation | 2026-05-30 |
| **M8** | Safety milestone | 2026-05-30 |

### 13.4 Sprint History / Sprint 歷史

- **v0.8** — Pop-up Overlay sprint; merged 2026-05-27.
- **v0.10** — Head-tracking sprint; 32 commits; merged 2026-05-30.
- **v0.11** — Inno Setup installer; commit `18005fa`.
- **v0.11.1** — Portable head-tracker sidecar + cross-vendor Vulkan; commits `2679e51`, `d4895b1`.

### 13.5 Test-suite Topology / 測試套件拓撲

| Layer | Tests | Coverage focus |
|---|---|---|
| Core | ~310 | LeakyIntegrator, CalibrationProcessor, HabituationOverrideParser, HabituationOverrideProcessor (5-rule matrix), HabituationProfile resolution, locked-decision structural tests |
| Infrastructure | ~210 | MockSensingProvider, MockInferenceEngine, `UserActivityModeDetector.DetectMode`, `WgcDiagLog`, `InMemoryChatbotActionLog`, safety-floor phrase tables |
| App | ~152 | MainViewModel proposal handshake, Applicator intercept paths, ChatbotViewModel NLU dispatch |

**Known coverage gaps (audit-revealed):**

| Item | Gap | Backlog priority |
|---|---|---|
| D1 | No MAD-to-OFI or cursor-velocity-to-OFI calibration data in `baseline/` | Alpha-2 |
| D2 | 8× Driver/Passenger ratio is direction-tested only, not magnitude | Alpha-2 |
| D3 | Leaky-integrator 5τ → 14s → TriggerPressure derivation has no test | Alpha-2 |
| E1 | `Driver + MaskActive` non-stacking invariant untested | Alpha-1 |
| E2 | Mask-attenuation constants 0.45/0.15/0.30 are test-orphans | Alpha-2 |
| E3 | `SidecarFrameDto` schema lacks reflection-based property allowlist test | Alpha-1 |
| E4 | Engine-level tier-≥8 only covers default Resilient | Alpha-1 |
| C3 | Safety-floor parity test missing; typographic drift exists | Alpha-1 |
| C5 | `ResetPersonalization` violation of Q4.5 has no regression test | Alpha-1 |
| C6 | Three-rule × three-intent matrix tests partial; no precedence test | Alpha-2 |

### 13.6 Alpha Roadmap / Alpha 路線圖

Carried forward from the POC sprint-state register, expanded with audit findings (full backlog in companion document `ALPHA_BACKLOG.md`).

**Release-phase naming note:** As of 2026-06-05 the release phases are renamed: `Pre-Alpha` (current POC) → `Alpha` (first user-facing release, formerly internally called "Beta" during planning) → `Beta 1` / `Beta 2` (future phases, scope TBD per POC + Alpha test outcomes). All items below are scheduled for the **Alpha** phase, sub-prioritized as `Alpha-1` (must-fix) / `Alpha-2` (should-fix) / `v1.0`.

**Alpha-1 (safety, correctness, audit integrity):**

- **C1** — Extract vestibular trigger override `50` to `VestibularTriggerPressureOverride`.
- **C3** — Fix safety-floor full-width vs ASCII colon drift; add parity test.
- **C5** — Split `HabituationEngine.Reset()` into post-calibration + transient methods; route `ResetPersonalization` to the latter so habituation `_offset` survives chatbot reset (Q4.5 fix).
- **E3** — Add `SidecarFrameDtoSchemaTests.cs` (reflection-based OBS-3 property allowlist).
- **E1** — Add `DriverMode_WithMaskActive_AppliesOnlyDriverAttenuation` test.
- **E4** — Promote tier-≥8 engine test to `[Theory]` over `HabituationProfile × {8, 9, 10}` plus tier-7 negative.
- **F1** — Migrate 8 §12.1.4 values to `SenseDefaults`; document `CardCountdown ≤ Watchdog` invariant; fix `HabituationOverrideProcessor.cs:137` cosmetic bug.

**Alpha-2 (governance, spec parity, coverage):**

- **A5** — Confirm `FeatherWidth ∈ [0, 1]` design (or tighten to `[0, 0.5]`); update doc-comment.
- **A7** — Per-profile shader defaults (Sensitive gets tighter MaskDepth, dimmer GridVisibility).
- **B5** — Add doc-comment at `HabituationEngine.cs:273-283` noting severe strikes are de-facto debounced via `_pauseCooldown`.
- **C4** — Wire `CalibrationConfidence` to chatbot context block annotation OR document as diagnostic-only with tests.
- **C6** — Three-rule × three-intent matrix tests; precedence-order test.
- **C7** — Extract step-magnitude named constants; add directional-hint-beats-severity doc-comment + tests.
- **D1** — Commit `baseline/wgc-mad-calibration.csv` + `baseline/cursor-velocity-calibration.csv`; add `WgcSensingProviderCalibrationTests`.
- **D2** — Promote `0.25` to `SenseDefaults.DriverFlowAttenuation`; add ratio assertion test.
- **D3** — Add `LeakyIntegratorConvergenceTests.cs` (4 tests: sustained-OFI→trigger, equilibrium-vs-analytic, rate-invariance, 1-sec-spike).
- **E2** — Add Passenger-mode mask-attenuation range + tightness-scaling tests.
- **F2** — Migrate Tier-2 / Tier-3 magic numbers to `SenseDefaults`; replace `OFI / 120.0` literal with `CursorVelocityNormFactor`, `ChartTriggerLine 85.0` with `TriggerPressure`.
- **G1** — Strict-language + slash-command path for shader parameters: add absolute-set API (`SetShaderParam`, `ResetShaderToProfileDefault`), pre-LLM interceptor mirroring the [ChatbotViewModel.cs:311](src/SenseEase.App/ViewModels/ChatbotViewModel.cs#L311) `TryDetectProfilePickerRequest` precedent, slash commands (`/grid off`, `/mask N`, `/feather N`, `/fov N`, `/reset shader`, `/help`, `/show`) and bilingual NL whitelist. Bypasses the `[-0.3, +0.3]` per-turn delta cap for high-confidence requests; mask hard-floored at 20% (core protection); `/reset shader` must not touch habituation offset (Q4.5). Lands after C5.

**Alpha security backlog (H1-H53) — see [docs/ALPHA_SECURITY_BACKLOG.md](docs/ALPHA_SECURITY_BACKLOG.md):**

A 10-domain cybersecurity audit (2026-06-05, workflow `w5ga7nd83`, 21 agents, 2.54M tokens, per-domain adversarial verification) produced 53 verified findings: 29 Alpha-1 must-fix, 24 Alpha-2 should-fix. Critical chain identified at the safety-floor boundary:

- **H1** — Profile-picker short-circuit ([ChatbotViewModel.cs:311](src/SenseEase.App/ViewModels/ChatbotViewModel.cs#L311)) runs BEFORE the self-harm safety floor; a user-in-crisis typing a profile-picker phrase mutates the engine profile without ever seeing the 988/1925 crisis-line response. Picker click goes directly to `_overrideApplicator.ApplyAccepted` with NO Card confirmation gate.
- **H2** — `ChatbotViewModel.SendAsync:362-364` calls `PersistMessage(ChatRole.User, text)` BEFORE the safety-floor intercept lives, so self-harm phrases persist verbatim to `%LOCALAPPDATA%\SenseEase\chat_history.jsonl` indefinitely — then ship off-device via the handbook's tester zip-up script.
- **H3** — Safety-floor phrase list ([MistralChatbotService.cs:858-871](src/SenseEase.Infrastructure/Real/MistralChatbotService.cs#L858-L871)) misses Simplified Han (`自杀/自残`), full-width Latin (`ｄｉｅ` — Ordinal + ToLowerInvariant does not normalise), and youth shorthand (`kys`, `kms`, `unalive`, `no reason to live`). Calibration service ([CalibrationChatbotService.cs:296-309](src/SenseEase.Infrastructure/Real/CalibrationChatbotService.cs#L296-L309)) is a verbatim duplicate with all the same gaps.

The full H-series also covers: H5 stored-prompt-injection via `[OBSERVATION_ADD]`, H9-H10 unauthenticated loopback API + port-trust spoofing, H12-H13 sidecar env-var redirection / encoding hijack, H14-H15 schema-v1 downgrade attack on `MedicalAdvisoryShown`, H16 non-atomic habituation save resetting weeks of progress, H17 in-memory-only audit trail with no forensic durability, H18 docs-vs-code privacy contradictions, H19-H21 build-engineer username embedded in shipped PDB + log paths, H22 handbook zip script ships protected medical PII without consent, H25 uninstall leaves full PII corpus on disk, H26 unsigned installer + tester README trains SmartScreen bypass habit, H27 GGUF/sidecar binary provenance not recorded, H28 506 MB unused cuda12 backend ships in installer (DLL hijack + attack surface), H32 user-writable install dir + no Authenticode signing on bundled binaries, H33 llama.cpp b9294 has CVE-2026-21869 (gated by `--context-shift`; bump for defense-in-depth), H49 HKCU Run-key orphan auto-start, plus 30 more across IPC, file-io, state-integrity, logging, dependency-CVE, and privilege domains.

**Other Beta items (existing roadmap):**

- **Single-writer queue diag-log** — option-2 fix from `WgcDiagLog.cs:30-34`.
- **Dev hotkey reactivation** — both hotkeys in the hidden table (commit `13aec63`).
- **Directional-flow estimator** — Farnebäck or learned-flow swap if MAD proves insufficient.
- **Settings-UI auto-mode opt-out** — surface `AppConfig.AutoDriverPassengerModeEnabled`.
- **Persistent action log** — re-evaluate `Q4.7` if telemetry requires cross-restart continuity.
- **Sidecar auto-restart** — currently disabled for the rest of the session on crash.
- **Animation constant extraction** — move the 380/280/250/500 ms literals to `SenseDefaults`.
- **Naming cleanup** — replace residual "Mistral 3B" mentions with "Ministral 3B".

**v1.0:**

- **A8 — Multi-monitor support** — overlay window + exclude-rect contract must generalise; per-monitor enumeration, Display-Changed handler, per-monitor DPI.

### 13.7 What the POC Does Not Claim / POC 不主張的事項

- SenseEase is **not** a medical device.
- The MAD-based OFI is **not** a calibrated optical-flow vector field; backing data has not been collected (audit D1).
- The efference-copy weighting magnitudes are **internal working hypotheses** awaiting future calibration data (audit D2).
- The 10-tier projection is content-agnostic and has not been validated against population-stratified VIMS susceptibility scores.
- The shader axes (`MaskDepth`, `GridVisibility`, `FeatherWidth`) are currently global rather than profile-stratified (audit A7).
- Multi-monitor configurations are not supported in v0.11.1 (audit A8).
- The `CalibrationConfidence` field is computed but currently has no downstream consumer (audit C4).

The whitepaper records what the POC built, what evidence supports each decision, and where the project is candid about the limits of that evidence.

---

## 14. Bibliography / 參考文獻

1. **Bárány Society Classification Committee** (2023). *Consensus criteria for motion sickness and visually-induced motion sickness.* Journal of Vestibular Research.
2. **Chang, C.-P. & Hain, T. C.** (2020). *Visual vertigo treatment through optokinetic stimulation with stationary anchoring.* Physiotherapy Research and Reports 4:1–4. *(First author Chih-Pei Chang. A short, ~4-page theoretical proposal — not a clinical trial; the prior citation's "Progressive Tolerance to Desensitization Protocol (TDP)" title and author initials were incorrect.)*
3. **Pavlou, M., Bronstein, A. M. & Davies, R. A.** (2013). *Randomized Trial of Supervised Versus Unsupervised Optokinetic Exercise in Persons With Peripheral Vestibular Disorders.* Neurorehabilitation and Neural Repair 27(3):208–218. *(The ~55 % dropout figure is the **unsupervised** arm; the supervised arm dropped out at ~10 %. The prior citation mis-dated this to "Pavlou 2010" and mis-attributed the 55 % to supervised regimens. Pavlou (2010), J Neurol Phys Ther 34(2):105–110, is a separate review article.)*
4. **Golding, J. F. & Keshavarz, B.** (2025). *Norms and Correlations of the VIMSSQ-short.* Multisensory Research. *(Authoritative source for the VIMSSQ-short norms; the prior "Lukacova et al. (2023, 2025)" attribution for these norms was incorrect — Lukacova's work, if cited, must be listed separately and not conflated with the norms paper.)*
5. **Vection literature, general.** Palmisano, Allison and co-authors.

---

## 15. Appendix: File Reference Index / 附錄：檔案參考索引

### 15.1 Core Layer / 核心層

- `src/SenseEase.Core/Configuration/SenseDefaults.cs`
- `src/SenseEase.Core/Inference/LeakyIntegrator.cs`
- `src/SenseEase.Core/Inference/CalibrationProcessor.cs` — `:25-26` scale clamps, `:74` vestibular triggerOverride literal (audit C1), `:297-320` `ComputeCalibrationConfidence` (audit C4)
- `src/SenseEase.Core/Inference/HabituationOverrideParser.cs` — `:10-28` block grammar, `:313-318` enum
- `src/SenseEase.Core/Inference/HabituationOverrideProcessor.cs` — `:49` expiry cap, `:53-60` clamps, `:64` cooldown, `:137` cosmetic bug
- `src/SenseEase.Core/Inference/HabituationProfile.cs` — `:67-206` `HabituationProfileParams`, `:138-175` profile numerics, `:196-205` `GetPostSpikePauseMs`
- `src/SenseEase.Core/Inference/HabituationEngine.cs` — `:97` `HabituationOffset`, `:161-179` `EffectiveTrigger` contract, `:260-318` Step ladder, `:387-405` SetPersonalization, `:433-446` Reset, `:449-467` EnterPause
- `src/SenseEase.Core/Inference/FeedbackLexicon.cs` — `:152-156` directional hints, `:208-210` severity table, `:220-223` per-call clamps
- `src/SenseEase.Core/State/ShaderParams.cs` — `:11-13` defaults, `:28-33` Clamp (audit A5)

### 15.2 Infrastructure Layer / 基礎設施層

- `src/SenseEase.Infrastructure/Real/WgcSensingProvider.cs` — `:528` MAD norm, `:765-771` ComputeMad
- `src/SenseEase.Infrastructure/Real/CursorVelocityProvider.cs` — `:14-29` rationale, `:150` velocity norm
- `src/SenseEase.Infrastructure/Real/UserActivityModeDetector.cs`
- `src/SenseEase.Infrastructure/Real/WgcDiagLog.cs`
- `src/SenseEase.Infrastructure/Real/MistralChatbotService.cs` — `:128` 48h prompt text, `:606-609` per-call clamps, `:858-871` phrase tables, `:892` ASCII colon (audit C3)
- `src/SenseEase.Infrastructure/Real/CalibrationChatbotService.cs` — `:296-309` mirrored phrases, `:330` full-width colon (audit C3)
- `src/SenseEase.Infrastructure/Real/SidecarHeadPoseReader.cs` — `:636-666` `SidecarFrameDto` (audit E3)
- `src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs` — `:19` `_shader` init, `:77` rest-state radius, `:217` Driver attenuation, `:220-225` mask-attenuation feedback (audit E1, E2), `:228` Passenger weight, `:243-257` mask hysteresis, `:296` `_userFovOffset` clamp, `:313` `ApplyShaderAdjustment`, `:324-338` `ResetPersonalization` (audit C5), `:480-493` calibration application (audit B6), `:547-551` chatbot scale application, `:829-831` `ComposeFovRadius` (audit A1, B2), `:855-874` `ComputeFovRadiusPercent` (audit A2), `:876-880` `ComputeMaskTightness` (audit A3), `:909-910` tier projection (audit B1)

### 15.3 App Layer / 應用層

- `src/SenseEase.App/Configuration/AppConfig.cs`
- `src/SenseEase.App/ViewModels/MainViewModel.cs` — `:230` `ExpiresAtMaxFutureDays` cross-layer read, `:267` 48h literal duplicate (audit F1), `:299` watchdog 60s, `:889` `ChartTriggerLine = 85.0` duplicate (audit F2)
- `src/SenseEase.App/ViewModels/ChatbotViewModel.cs` — `:783` `ResetPersonalization` call site (audit C5)
- `src/SenseEase.App/ViewModels/HabituationOverrideApplicator.cs` — `:143-179` three-rule intercept (audit C6), `:172` `SignificantRelaxationThreshold = 0.15` literal (audit F1), `:189-217` `IsSignificantRelaxation`
- `src/SenseEase.App/ViewModels/CalibrationApplicator.cs` — `:102-103, 106-107` advisory strings with literal 50 (audit C1)

### 15.4 Views / UI Layer / 視圖層

- `src/SenseEase.App/Views/OverlayWindow.xaml.cs` — `:20, 43` brush radii (audit F2), `:54-66` `VimsLevels` palette, `:77` `MaskSmoothingFactor` (audit A6, F2), `:99-133` `BuildLevelFeather`, `:191` 280ms grid anim (audit A6), `:209` 380ms fade-in (audit A6), `:232` `PrimaryScreenWidth` DPI scale (audit A8), `:496` 250ms fade-down (audit A6), `:509` 500ms fade-up (audit A6), `:639-664` `BuildFovStopOffsets`, `:666-695` `ApplyGridIntensity` (audit A4), `:699` `OFI / 120.0` literal (audit F2)
- `src/SenseEase.App/Views/OverlayWindow.xaml` — `:11` `WindowState="Maximized"` (audit A8)
- `src/SenseEase.App/Views/NotificationCardWindow.xaml.cs` — `:45` idle timeout 60s, `:50` fade duration 5s, `:186` countdown 15s (audit F1), `:648-655` `QuickDiscomfortPhrases`

### 15.5 Documentation / 文件

- `docs/SAFETY-PROVISIONS.md`
- `docs/QA-AND-TIERING.md`
- `docs/HABITUATION-PROFILE.md`
- `docs/HEAD-TRACKING-PRIVACY.md`
- `docs/HANDBOOK_zh-Hant.md`
- `docs/LOG-PATHS.md`

### 15.6 Sprint State and Process Artefacts / Sprint 狀態與流程

- `Habituation plan.md`
- `baseline/m5.1-a-diag.csv` — head-tracking sprint diag baseline (no OFI calibration data, audit D1)
- `baseline/m5.1-c-perf-isolated.json` — head-tracker MediaPipe perf (no OFI data)
- `soak_report.json` — head-tracker sidecar perf JSON (no OFI/MAD fields, audit D1)

### 15.7 Packaging / 封裝

- `installer/SenseEase.iss`
- `tools/head-tracker/`
- `face_landmarker.task`
- `publish-test/`

### 15.8 Tests / 測試

- `src/SenseEase.Tests/` — 672 passing, 0 failing, 1 skipped. Approximate split: Core ~310, Infrastructure ~210, App ~152.

Known test gaps surfaced by the 2026-06-04 audit are catalogued in §13.5 and §13.6.

---

*End of document. Version Pre-Alpha v0.11.1 (audit-revised). 2026-06-04.*
