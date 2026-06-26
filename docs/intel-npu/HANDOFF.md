# HANDOFF — Intel OpenVINO NPU LLM for SenseEase (for the repo agent)

You are receiving the results of a debug+eval session run on an **Intel test box** (Core Ultra 5 336H, NPU + Arc iGPU, 16 GB) against the **portable build output** of SenseEase. Your job: fold these changes into the **repo's Intel branch** of the FLM (local-LLM) subsystem, alongside the existing **AMD/Vulkan** variant. This file is the index; details are in the sibling docs.

## What was wrong, and what now works
- The shipped **ggml-openvino** GGUF "NPU" path was unusable (~0.25 tok/s, ~100 s load that exceeded the app's readiness timeout, then squatted port 8181). Separately, the chat never replied because the app's ~65 s per-request timeout cancelled the cold prefill of a ~3200-token NLU prompt (0 tokens generated).
- **Fix delivered:** replace it with **OpenVINO GenAI** running a **pre-converted INT4 OpenVINO IR** of `Qwen3-4B-Instruct-2507`, served by a small host that speaks the app's existing OpenAI contract on `127.0.0.1:8181`. Validated end-to-end (app → host → NPU → real NLU replies) and stress-tested with 622 prompts.

## Bottom-line findings (evidence in EVAL_REPORT.md)
1. **Deliverability 99.8–100%** (valid NLU JSON), **accuracy ~90.5% effective** — equivalent to the FLM build (same model).
2. **KV/prefix-reuse is quality-safe** (iGPU 99.7% identical, NPU 100% identical to naive) — ship it without fear of quality regression. But it **only speeds up the iGPU** (2.6 s/turn); the **NPU can't reuse KV** and stays ~15 s/turn for this large prompt.
3. **NPU ≈ iGPU decisions** (94.4% agreement). The same INT4 IR runs on both — device is a one-line config flip.
4. **⚠ The NPU destabilizes under sustained saturation** (device-lost after ~298 back-to-back requests; corrupts the compile cache). The host must be resilient (fallback + cache-rebuild). Normal interactive use is fine.

## Actions for the repo (priority order — full specs in SOURCE_CHANGES_intel_flm.md)
1. **Reliability fixes (do first — fixes "no reply" on any backend):** readiness timeout ≥120 s gated on `/health`; kill+free-port on failed spawn before the loopback guard; per-request HttpClient timeout ≥120 s (or stream); real warm-up + no aggressive idle-unload. (§A–B)
2. **Swap the OpenVINO backend to the GenAI host:** ship `openvino_genai.dll` + `openvino_tokenizers.dll` next to the OV 2026.2 runtime (version-matched), ship the INT4 IR, and have `FlmServerLauncher` spawn the GenAI host for "OpenVINO NPU (local)". Production host = a native C++ sidecar (spec in §D); the validated Python reference is `scripts/genai_host.py`. (§A, §D)
3. **NPU resilience (from the stress test):** on `DEVICE_LOST`, **fall back to the iGPU** (a same-device reload hangs — proven); **detect a corrupt NPU cache** (load exceeding a sane bound → wipe `CACHE_DIR` → recompile). Already implemented in `scripts/genai_host.py`. (§F)
4. **Device default = iGPU for this prompt size** (2.6 s/turn, stable, prefix-cacheable), NPU as low-power opt-in. Keep **Vulkan/GGUF as the universal fallback**; retire only the old ggml-openvino path. (§E–F)
5. **Prompt fixes (model-level, help both vendors):** harden injection refusal (HARD-RULE-#2 — ~50% of jailbreaks were partially obeyed); normalize intent strings to the 6 envelope labels (the model emits card names like "Which-Symptom"); trim jargon. (EVAL_REPORT "Model/prompt findings")

## Artifacts in this package
- `EVAL_REPORT.md` — full results (5 runs), equivalences, latencies, stress finding.
- `SOURCE_CHANGES_intel_flm.md` — exact code-change spec (§A–F) with recovered symbol names (`FlmServerLauncher`, `LlmBackendSelector`, `ChildProcessReaper`, `LocalLlmChatbotService`).
- `CONVERT_MODEL.md` — one-time GGUF-checkpoint → INT4 IR recipe + the toolchain (`host_requirements.txt`).
- `scripts/genai_host.py` — validated host (OpenAI contract, NPU/iGPU, device-loss→iGPU fallback). `runtime_dlls/` — the two DLLs not yet shipped.
- `eval/` — the 622-case dataset, all result JSONLs, scorers, runners, and the resilient `npu_orchestrate.sh`.
- Model IR: `..\models\Qwen3-4B-Instruct-2507-ov-int4\` (in the build), ~2.2 GB.

## How to verify after integrating
1. Convert/obtain the IR (CONVERT_MODEL.md); first NPU compile ~250 s, cached after (~5 s warm).
2. Run the host standalone: `python scripts/genai_host.py` with `OVHOST_MODEL=<IR dir> OVHOST_DEVICE=GPU` (or NPU) → `curl /health` → `ready`; `curl /v1/chat/completions` → valid NLU JSON.
3. Re-score against the dataset: `python eval/score_eval.py <results.jsonl>` → expect ~100% deliverability, ~90% effective accuracy.
4. App test: set `SENSEEASE_LLM_BASEURL=http://127.0.0.1:8181`, launch the app, send a message → real reply.

## Caveat
All work is on the **build output**, not the repo. The native C++ sidecar (§D) and the in-app `FlmServerLauncher`/selector edits are **specified and validated by proxy** (Python host + measured behavior), not compiled here — implement and build them in the repo.
