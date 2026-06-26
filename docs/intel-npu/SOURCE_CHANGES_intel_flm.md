# Source changes for the repo — Intel FLM (local-LLM) branch

These land in the `SenseEase.Infrastructure` FLM subsystem. Symbol names below were recovered from the shipped PDBs (`FlmServerLauncher`, `LlmBackendSelector`, `ChildProcessReaper`, `LocalLlmChatbotService`, `LlamaServerCalibrationClient`). Apply on the **Intel** vendor branch; leave the **AMD FLM** path untouched (it keeps its Vulkan/llama-server flow). Vulkan stays as the universal fallback.

## A. Make `OpenVINO NPU (local)` use the GenAI host (not ggml-openvino)
The ggml-openvino GGUF path on NPU is unusable (~0.25 tok/s, ~100 s load). Replace it with the OpenVINO GenAI host.

1. **Ship** `openvino_genai.dll` + `openvino_tokenizers.dll` in `runtimes\win-x64\native\openvino\` (next to the OV 2026.2 runtime already there). **Match the OpenVINO version** the app bundles (app=2026.2.0; validated host=2026.2.1 — pick one and align both core + GenAI).
2. **Ship** the INT4 IR `models\Qwen3-4B-Instruct-2507-ov-int4\` (keep the GGUF for Vulkan fallback).
3. **Host:** production should be a **native C++ sidecar** exposing the same OpenAI contract (see §D); the validated reference is the Python `genai_host.py`. In `FlmServerLauncher.ResolveServerExePath`/`BuildSidecarArgs`, for the OpenVINO-NPU backend resolve the GenAI host exe + pass `--model <IR dir> --device NPU --port <p> --cache-dir %LOCALAPPDATA%\SenseEase\ov_cache` instead of the llama-server CLI.
4. **Minimal alternative (what was validated here):** keep `LlmBackendSelector` choosing the OpenVINO backend, but launch the GenAI host and rely on the existing `external server (env override)` HTTP client path. The host already speaks `/v1/chat/completions`, `/v1/models`, `/health`.

## B. The four reliability fixes (needed regardless of backend)
1. **Readiness timeout** — `FlmServerLauncher.WaitForServerReadyAsync`: raise the budget for the OpenVINO backend to cover a cold NPU compile (≥300 s for a cold first-run; ~10 s when the blob cache is warm). Gate on `GET /health` returning `200 {"status":"ready"}`, not a fixed wall-clock. The host reports `state=compiling` until the pipeline is built — surface that as first-run UX.
2. **Port cleanup on failure** — `FlmServerLauncher` + `ChildProcessReaper`: when a start fails/times out, **force-kill the child by PID and poll `IsLoopbackPortListening(8181)` until false** before the loopback guard runs on retry. Today the guard (`"…already has a loopback listener; refusing to spawn a second llama-server."`) fires because the dead server still holds the port. Fix removes the wedge.
3. **Per-request timeout** — `LocalLlmChatbotService` `HttpClient.Timeout`: raise to ≥120 s for the OpenVINO backend (cold prefill of the ~4900-token prompt), **or** switch that path to streaming and use a per-token idle timeout. This eliminates the 0-token `should_stop` cancellations.
4. **Warm-up + idle** — actually run `WarmUpPrefixIfAllowedAsync` on the real system-prompt prefix after load (host already self-warms before `/health=ready`), and lengthen/disable `MaybeUnloadIfIdle`/`StartIdleTimer` (`_idleTimer`) for NPU so the compiled blob + KV are not discarded; re-warm on reload.

## C. Latency optimization — KV/prefix reuse (high value)
Per-turn is ~27 s today because the host re-prefills the full ~4900-token prompt every request. The system prompt is **static**. Use OpenVINO GenAI **chat mode** (`start_chat(system_prompt)` once, keep the session) or KV-cache reuse so only the new user turn is prefilled → per-turn **~3–5 s**. Map the app's `cache_prompt:true` to "keep the system-prompt KV resident." This is the single biggest UX win and gets under the <15 s target. (Requires the host to treat the leading static system message as a persistent prefix rather than re-sending it through a stateless `generate()` each call.)

## D. Native C++ sidecar (production host) — spec
~200–400 lines: header-only HTTP server (e.g. cpp-httplib) + persistent `ov::genai::LLMPipeline(model_dir, "NPU", props)`.
- Properties: `MAX_PROMPT_LEN=6144`, `MIN_RESPONSE_LEN=512`, `PREFILL_HINT="DYNAMIC"`, `GENERATE_HINT="BEST_PERF"`, `CACHE_DIR=%LOCALAPPDATA%\SenseEase\ov_cache`.
- Routes: `POST /v1/chat/completions` (apply chat template via the tokenizer with `apply_chat_template` then `generate(..., apply_chat_template=false)`; greedy on NPU), `GET /v1/models` (return the `SENSEEASE_LLM_MODEL` id), `GET /health` (`compiling`/`ready`). Accept any `Authorization: Bearer`. Serialize requests (NPU has no batching).
- Logs to `%LOCALAPPDATA%\SenseEase\llama-server-genai.log` to match the per-backend log convention.
- The Python `genai_host.py` in this deliverable is a 1:1 behavioral reference (config, request mapping, warm-up, health).

## E. Selector / fallback
`LlmBackendSelector`: order **OpenVINO-GenAI NPU → Vulkan(GGUF) → (ggml-openvino retired)**. On NPU host failure / NPU absent / integrity fail / readiness timeout, auto-fall-through to Vulkan so the app always answers. Add the new binaries to the integrity hash manifest (`"Integrity check failed…"` path).

## F. NPU device-loss resilience + device-default (from the stress test)
The 622-case stress eval surfaced two hard facts:
1. **The NPU can enter a persistent `ZE_RESULT_ERROR_DEVICE_LOST`** after sustained back-to-back inference (~298 heavy requests). A same-device pipeline reload does **NOT** recover it — a fresh `LLMPipeline("NPU")` then *hangs on load*; only a driver/device reset (PnP disable-enable) or reboot clears it. The iGPU stayed healthy throughout.
   - **Host fix (implemented in `genai_host.py`):** on `DEVICE_LOST`/`device hung`/`infer_request` exceptions, **fall back to the iGPU (`device="GPU"`)** and reload there — do NOT retry the NPU (it hangs). Optionally surface a "switched to iGPU; NPU needs a reset" status.
2. **KV/prefix-reuse does not exist on the NPU**: `ContinuousBatchingPipeline` (prefix-caching) won't compile on NPU, and chat-mode gives zero amortization (~15 s/turn for the 3200-token prompt). So the NPU re-prefills the full prompt every turn.
   - **Implication for device default:** for this large-prompt chat, **default to the iGPU** (prefix-cached ~2.6 s/turn, stable, decision-equivalent to NPU). Offer NPU as a low-power opt-in with the fallback above. If NPU latency must drop, **shrink the NLU system prompt** (the NPU can't cache it). See [EVAL_REPORT.md](EVAL_REPORT.md).
