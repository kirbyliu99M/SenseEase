# SenseEase — Intel NPU LLM bring-up (OpenVINO GenAI)

**Status: WORKING on this box.** The SenseEase chat now produces real, intent-correct NLU replies with inference running on the **Intel NPU** via **OpenVINO GenAI**, replacing the dead ggml-openvino path.

Validated on: Intel Core Ultra 5 336H, Intel NPU driver 32.0.100.4512, 16 GB shared RAM, OpenVINO/GenAI **2026.2.1**, model **Qwen3-4B-Instruct-2507** → INT4-sym(g128) OpenVINO IR.

---

## 1. What was wrong (recap)
- The shipped **ggml-openvino** NPU path: cold-load ~100 s (> app's ~67 s readiness timeout → killed, then squatted port 8181) and decoded **~0.25 tok/s** — unusable.
- Independent dominant bug: the **~4900-token NLU system prompt** cold-prefilled longer than the app's ~65 s per-request timeout → task cancelled with **0 tokens** → no reply (canned fallback shown).

## 2. What was done
Stood up an **OpenVINO GenAI host** that serves the app's exact OpenAI contract (`/v1/chat/completions`, `/v1/models`, `/health`) on `127.0.0.1:8181`, backed by a persistent `LLMPipeline(model, "NPU")`. The app was pointed at it through its **existing env override** (`SENSEEASE_LLM_BASEURL`) — **no app binary changes were needed to demonstrate runnable** on this build-output-only box. Source-level changes for the repo are specified in [SOURCE_CHANGES_intel_flm.md](SOURCE_CHANGES_intel_flm.md).

## 3. Measured results (this box)
| Metric | ggml-openvino NPU (old) | **OpenVINO GenAI NPU (new)** |
|---|---|---|
| Decode throughput | ~0.25 tok/s | **~11.1 tok/s** (~44×) |
| Prefill throughput | ~0.8 tok/s | **~287 tok/s** |
| Model load (cold compile) | ~100 s (timed out) | ~237 s **one-time**, cached |
| Model load (warm, CACHE_DIR hit) | n/a | **~5 s** |
| Full app turn (~4900-tok prompt) | never completed | **~27 s**, within timeout |
| Output quality | garbage / none | **coherent, valid NLU JSON, correct intents** |

End-to-end proof: two app chat turns produced distinct, correct replies on the NPU —
- "eyes strained / dizzy" → ReportDiscomfort ack ("好的，我記下了，會用這個來調整往後給你的防護。")
- "feels a lot better now, thanks" → ReportComfort ack ("了解，已記錄。如再有不適請告知。")

## 4. Artifacts in this deliverable
- `models/Qwen3-4B-Instruct-2507-ov-int4/` (copied into the build's `models\`) — the NPU-ready INT4 IR. **~2.2 GB.**
- `tools/genai-llm-host/genai_host.py` (and `_NPU_DELIVERABLES/scripts/`) — the validated OpenVINO GenAI host.
- `_NPU_DELIVERABLES/runtime_dlls/openvino_genai.dll`, `openvino_tokenizers.dll` — the two runtime DLLs **not currently shipped** (the app already ships the rest of the OpenVINO 2026.2 runtime + NPU plugin).
- `_NPU_DELIVERABLES/scripts/convert_2507.sh` — the one-time GGUF-checkpoint → INT4 IR conversion recipe. See [OV_MODEL_CONVERSION.md](../OV_MODEL_CONVERSION.md).
- `_NPU_DELIVERABLES/host_requirements.txt` — exact Python env that runs the host today.
- [SOURCE_CHANGES_intel_flm.md](SOURCE_CHANGES_intel_flm.md), [VERIFICATION.md](VERIFICATION.md).

## 5. How to run it (reproduce on this box)
The host process is currently **running** (NPU, port 8181) and the app is wired to it. To restart from scratch:
```powershell
# 1. start the GenAI NPU host (uses the warm cache at C:\ovgenai\ov_cache -> ready in ~5s)
$env:OVHOST_MODEL = "C:\ovgenai\models\Qwen3-4B-Instruct-2507-ov-int4"  # or the build's models\ copy
$env:OVHOST_DEVICE = "NPU"; $env:OVHOST_PORT = "8181"; $env:OVHOST_MODELID = "qwen3-4b"
C:\ovgenai\venv\Scripts\python.exe C:\Users\KentoI\Desktop\SenseEase-Debug-intel-2\tools\genai-llm-host\genai_host.py
# 2. point the app at it and launch
$env:SENSEEASE_LLM_BASEURL = "http://127.0.0.1:8181"; $env:SENSEEASE_LLM_MODEL = "qwen3-4b"
Start-Process "C:\Users\KentoI\Desktop\SenseEase-Debug-intel-2\SenseEase.App.exe"
# 3. open the tray chat, send a message -> NPU reply
```

## 6. Integrating into the repo (Intel FLM branch, alongside AMD)
Target layout in the repo's Intel build:
- `models\Qwen3-4B-Instruct-2507-ov-int4\` — ship the INT4 IR (or regenerate via CONVERT_MODEL.md at build time). **Keep the GGUF** for the Vulkan fallback.
- `runtimes\win-x64\native\openvino\` — add `openvino_genai.dll` + `openvino_tokenizers.dll` next to the OpenVINO 2026.2 runtime already there. **Version-match**: build/ship the GenAI DLLs against the SAME OpenVINO the app bundles (app ships 2026.2.0; the validated host used 2026.2.1 — align them).
- LLM host: for production prefer a **native C++ sidecar** (spec in SOURCE_CHANGES) built against OV-GenAI 2026.2.x, shipped under `runtimes\...\openvino\` so `FlmServerLauncher` spawns it exactly like `llama-server.exe`. The Python host here is the validated reference for behavior/config.
- Wire the `OpenVINO NPU (local)` backend to launch the GenAI host (or, minimal: keep using the `SENSEEASE_LLM_BASEURL` env-override path). Apply the four reliability fixes in SOURCE_CHANGES so it self-heals (readiness timeout, port cleanup, request timeout, warm-up/idle).
- Cache dir: point `CACHE_DIR` at `%LOCALAPPDATA%\SenseEase\ov_cache` and ship a pre-built NPU blob, or run a first-run "preparing on-device AI (~1–2 min, once)" compile (cold ~237 s, warm ~5 s thereafter).

## 7. Known limitations / production follow-ups
- **Per-turn latency ~27 s** because the host re-prefills the full ~4900-token prompt each request. **Biggest win:** add **KV/prefix reuse** so the static system prompt is encoded once → per-turn drops to ~3–5 s. (OpenVINO GenAI chat-mode `start_chat()` keeps the system-prompt KV resident.) This gets you under the <15 s target.
- **NPU is greedy-only** (no temperature/top-p) — fine for the NLU JSON task; the host ignores sampling fields on NPU.
- First-ever compile is ~237 s (one-time, cached). Ship the pre-built blob or show first-run progress.
- Optional accuracy upgrade: re-export the IR with `--awq --scale-estimation --dataset wikitext2` (slower convert, better INT4 quality). Current IR is data-free int4-sym-g128.
- Device toggle: the same IR runs on the **iGPU** (`OVHOST_DEVICE=GPU`) if you ever want sampling or a different perf/power tradeoff — one-line change.
