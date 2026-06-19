# Chatbot eval harness (golden regression suite)

Faithful, reproducible evaluation of the SenseEase chatbot LLM (Qwen3-4B). Drives a local
`llama-server` with the **exact** shipped system prompt + GBNF grammar (extracted from source, never
hand-copied) over a taxonomy of user-utterance families, then scores the results. Built during the
2026-06-19 chatbot-tuning sprint; see `docs/CHATBOT_BEHAVIOR_SPEC.md` for the behavior contract it checks.

## Layout

| File | Role |
|---|---|
| `extract_prompt.py` | Pulls the byte-exact `SystemPrompt` + `EnvelopeGrammar` out of `LocalLlmChatbotService.cs` → `system_prompt.txt`, `grammar.gbnf`. Run this first (and after any prompt/grammar edit). |
| `cases.py` | The utterance taxonomy: 19 families (named/unnamed symptom, severity, comfort, reset, analyze, question-trap, vague, contradictory, multi-intent, profile/sensitivity, off-topic, meta, injection, medical, language, grid, malformed). EN + ZH. |
| `run_eval.py` | Builds the faithful contextual input, fires each case ×N reps at `llama-server`, runs deterministic checks, writes `results.jsonl` + a per-family pass-rate summary. |
| `analyze.py` | Post-processes `results.jsonl` → concrete failure lists + per-family judge payload (`judge_fam/`). |
| `judge_workflow.js` | The Workflow LLM-judge pass (subjective dimensions: tone, action-honesty, symptom-state boundary, jargon, injection-obedience). |
| `baseline_summary.txt` | The pre-tuning baseline (2026-06-19) to diff against. |

## Run it

```bash
# 0. one clean GPU llama-server (a SECOND instance competes for VRAM → CPU fallback → ~30s/call; keep ONE)
runtimes/win-x64/native/vulkan/llama-server.exe --model models/Qwen3-4B-Instruct-2507-GGUF/Qwen3-4B-Instruct-2507-Q4_K_M.gguf \
  --n-gpu-layers 99 --host 127.0.0.1 --port 8182 --no-webui --no-context-shift --ctx-size 8192 --no-warmup --jinja

# 1. re-extract the prompt+grammar (always, so the harness can't drift from source)
python tools/chatbot-eval/extract_prompt.py

# 2. sweep (≈1s/call on a clean single GPU server; ~6 min for 84×4)
python tools/chatbot-eval/run_eval.py --reps 4 --out results.jsonl

# 3. concrete failures + judge payload
python tools/chatbot-eval/analyze.py
```

Deterministic checks (the safety floor, no GPU needed to interpret): **0 unsafe actuations** on any
must-not-actuate family, **language purity** (no Simplified / kana / mixing), schema/range, intent.
The subjective layer (`judge_workflow.js`) scores tone + honesty + boundary.

## Notes

- The grammar extension (trailing `[PROFILE_OVERRIDE]` blocks) is **latency-free** — verified
  grammar-on vs grammar-off parity. If a sweep is slow (>5s/call), check for a **second** orphaned
  `llama-server` (VRAM contention), not the grammar.
- The deterministic gate rules (action-honesty, reset-downgrade, language purity, boundary) have **fast
  C# unit tests** in `tests/SenseEase.Tests` (`ReplyComplianceScrubberTests`, `InterrogativeDetectorTests`,
  `HabituationProposalLexiconTests`, `FmsCheckInServiceTests`, `FmsChatMessageTests`) — those are the CI
  regression gate; this live-LLM harness is the on-demand model-behavior check.
