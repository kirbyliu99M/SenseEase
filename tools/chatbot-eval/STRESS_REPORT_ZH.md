# Chinese (Traditional / Taiwan) stress-test report — 2026-06-22

**Ask (Kirby):** "Chinese is our major consumer for this Beta — deep, thorough stress test; what can you
optimize in **response time** and **accuracy**? And more ambiguous-language testing." Run autonomously.

## Scope & method
- **230 Traditional-Chinese (Taiwan) prompts** across 7 families, **weighted toward colloquial/ambiguous**
  input (怪怪的 / 不太對勁 / 頭怪怪的der / 注音文 / netspeak / slang), at **3 reps → 690 calls**.
  Corpus: `stress_cases_zh.json`.
- Harness now records **per-call latency + prompt/completion tokens** (`run_eval.py` instrumented).
- Mode: **`--no-grammar` = the live FLM/NPU mirror**. Model: Qwen3-4B-Instruct Q4_K_M, eval server = Vulkan
  **iGPU** (so absolute ms reflect the iGPU; the **token costs scale to the NPU**, where prefill is ~13× slower).

---

## A. Response time

| metric | value | meaning |
|---|---|---|
| latency (iGPU) | mean **857 ms**, p95 1173, max 1756 | fast — this is the iGPU |
| **prompt_tokens** | mean **4,530** (4,522–4,562) | the per-turn **PREFILL** (system prompt + ZH turn) |
| **completion_tokens** | mean **56**, p95 76, max 84 | the **DECODE** (ZH reply length) |

**Conclusion: Chinese is prefill-bound, same as everything else.** The reply is *not* the cost — ZH replies
are concise (avg 56 tokens; the clinical-concise style holds). The **4,530-token prompt, re-read every turn**
(FLM does not cache it — measured) is the cost: ≈ **~13 s on the NPU** vs **~0.9 s on the iGPU** — a measured
**~25× prefill gap**.

### Response-time levers (biggest first)
1. **iGPU device-choice — the headline (~13 s → ~1 s).** Run the *foreground* chat on the Vulkan/iGPU path
   instead of FLM/NPU; keep NPU for battery (AC = iGPU / battery = NPU). The app already ships the Vulkan
   path — it's a backend-routing pref, not new infra. **This is the single biggest win for Chinese users.**
   *(Architectural + battery trade-off → your call; I did not implement it autonomously.)*
2. **Bonus: it removes the accuracy-vs-latency tension.** Every accuracy rule added to the prompt (the
   DO-NOT-ACTUATE checklist, the ZH idioms) costs ~prefill on the NPU but is **~free on the iGPU**. On the iGPU
   the prompt can be as detailed as accuracy needs.
3. **Prompt is near the floor** (4,571 tok, **−43%** vs the original 8,046) — little left to trim without losing
   accuracy.
4. **ZH replies are already tight** (avg 56). The decode-heaviest (~84 tok) are app/eye-comfort 2-sentence
   answers — capping `max_tokens` or tightening §9 would shave ~1 s of *decode* on the NPU, marginal vs the
   ~12 s of prefill.
5. **Prewarm on chat-open** (already shipped) kills the one-time cold-load.

---

## B. Accuracy (after the ZH fix, commit `a04c585`)

| family | n | intent | **actuate** | notes |
|---|---|---|---|---|
| ambiguous_discomfort_zh | 120 | 114 | 117/120 | vague 怪怪的/不太對勁 → asks; 3 stubborn |
| ambiguous_thoughts_zh | 120 | 51 | **120/120** | intent low but **0 actuation** (see below) |
| colloquial_slang_zh | 108 | 100 | 107/108 | netspeak/注音文 handled well |
| app_questions_zh | 84 | 84 | **84/84** | clean |
| eye_comfort_questions_zh | 84 | 84 | **84/84** | clean (answers in scope, no diagnosis) |
| edge_zh | 90 | 66 | 75/90 | adversarial — most residual over-actuation here |
| **genuine_control_zh** | 84 | 75 | **84/84** | **every real symptom acts — no under-actuation** |

**Safety-critical over-actuations: 25 → 18** after the fix.

### What the Chinese sweep found + fixed
- **FIXED — Chinese figurative idioms** (the clear bugs): a symptom word caused by *content/work*, not the
  screen — `看到帳單頭都暈了` (dizzy from the **bill**), `報表看得眼冒金星` (stars from the **report**),
  `資訊量大到天旋地轉` (spinning from **info overload**). These now correctly do **not** actuate (added ZH cues
  + a ZH example to the §3 checklist), and ZH vague body-words (`悶悶的`/`飄飄的`/`卡卡的`/`不太對勁`/`說不上來`).
- **Strong baseline:** the bot reliably **acts on real Chinese symptoms** (頭暈 / 噁心 / 頭痛 / 眼睛酸 / 想吐) —
  `genuine_control_zh` 84/84 — and answers app/eye-comfort questions in scope without diagnosing or actuating.

### Residual 18 over-actuations — mostly borderline (your judgement)
- **Real complaint wrapped in hyperbole (~6) — arguably *correct* to act:** `這畫面亮到我快往生` (screen
  genuinely too bright), `對比色根本謀殺我的眼睛` (real contrast problem). I deliberately did **not** suppress
  real brightness/contrast complaints.
- **Symptom emojis (~6):** `😵😵😵`, `🤢` — these *do* signal dizzy/nausea, so act-vs-ask is a **product call**
  (act on the signal, or ask since an emoji is ambiguous). Easy to flip either way if you have a preference.
- **A few stubborn (~6):** conditional `再看下去眼睛要瞎掉了啦`, vague `腦袋悶悶的不太對`. More prompt tightening
  here risks *missing real symptoms* (seen on the English side), so the deterministic gate is the better place.

### One thing worth a decision: `ambiguous_thoughts_zh` intent 51/120
Dismissive closers — `算了` / `沒事啦` / `再看看` / `隨便` / `以後再說` — get classified **ReportComfort** instead
of Unknown. **Zero actuation, reasonable replies**, so not a safety bug — but ReportComfort can nudge the
engine to *relax* protection. If that matters, a small gate rule ("bare dismissive closer ≠ comfort") would
fix it deterministically. Flagging, not fixing, since it's a judgement call.

### Language (Traditional purity)
English replies were 102/102; ZH `app`/`eye` show ~76–78/84 flags, but those are **brand names** (SenseEase)
and the now-fixed harness false-positive (亮 is valid Traditional). Real Traditional purity is fine; the
output-side language scrubber catches genuine mixing in production.

---

## Bottom line
- **Accuracy:** the bot is **safe on real Chinese symptoms (84/84)** and handles colloquial/ambiguous ZH well;
  the clear figurative-idiom bugs are fixed; the ~18 residuals are mostly defensible (real-complaint hyperbole,
  symptom-emojis) or at the frontier.
- **Response time:** **prefill-bound (~13 s NPU)** — the one big lever is the **iGPU device-choice (~13 s → ~1 s)**,
  which also frees you to keep a detailed (accurate) prompt. The prompt is already trimmed to the floor.

**Reproduce:** `python tools/chatbot-eval/run_eval.py --reps 3 --no-grammar --cases-json stress_cases_zh.json
--out r.jsonl` then `_perf.py r.jsonl` (timing) / `_triage_stress.py r.jsonl` (failures).
**Commits:** `a04c585` (ZH idiom/vague fix) · `78092eb` (DO-NOT-ACTUATE checklist) · `eba4f34` (corpus + harness).
