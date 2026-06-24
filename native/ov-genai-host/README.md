# ovgenai-host — OpenVINO GenAI HTTP sidecar (Intel NPU/iGPU LLM backend)

Production HTTP host for SenseEase's Intel **OpenVINO GenAI** LLM path. It serves the
OpenAI contract (`/v1/chat/completions`, `/v1/models`, `/health`) on `127.0.0.1:<port>`
backed by a persistent `ov::genai::LLMPipeline` running a pre-converted **INT4 OpenVINO IR**
of `Qwen3-4B-Instruct-2507`. It replaces the retired ggml-openvino GGUF "NPU" path
(unusable at ~0.25 tok/s / ~100 s load).

This C++ sidecar is a **1:1 behavioural port** of the validated reference
`genai_host.py` (Intel deliverable `_NPU_DELIVERABLES/scripts/`), which passed the
622-case NLU eval (99.8–100% deliverability, ~90.5% effective accuracy = the FLM build).

## Contract & behaviour
- **Config via env** (same as the Python reference): `OVHOST_MODEL` (IR dir, required),
  `OVHOST_DEVICE` (`NPU` default | `GPU` | `CPU`), `OVHOST_PORT` (8181), `OVHOST_MODELID`,
  `OVHOST_CACHE` (CACHE_DIR), `OVHOST_LOG` (default `%LOCALAPPDATA%\SenseEase\llama-server-genai.log`).
- **Background load** — the socket comes up immediately; `/health` reports
  `{"status":"compiling"}` (503) until the pipeline is built + warmed, then `{"status":"ready"}` (200).
  First NPU compile ~250 s cold, ~5 s warm (blob cache). The app must gate readiness on
  `/health`, not wall-clock (§B1).
- **NPU props** (NPU only): `MAX_PROMPT_LEN=6144`, `MIN_RESPONSE_LEN=512`, `GENERATE_HINT=BEST_PERF`.
  §D also lists optional `PREFILL_HINT="DYNAMIC"`; the validated host omitted it, so this port
  mirrors the validated set exactly.
- **Serialized** — NPU has no batching; one generation at a time (mutex). Greedy on NPU.
- **NPU device-loss resilience** (§F) — on a persistent `ZE_RESULT_ERROR_DEVICE_LOST`
  (proven unrecoverable by a same-device reload), it **falls back to the iGPU** and reloads
  there; it does **not** retry the NPU (which hangs on reload).
- **Device default = iGPU** for this large prompt (2.6 s/turn prefix-cached, stable,
  decision-equivalent to NPU); NPU is the low-power opt-in (~15 s/turn, no KV reuse).

## Not yet implemented (future)
- **§C chat-mode KV/prefix reuse** on the iGPU (`start_chat` keeping the static system-prompt
  KV resident). The app currently sends only `{system, user}` per turn (no growing history),
  so the stateless `generate()` path here matches what was validated. Add chat-mode when the
  iGPU per-turn latency needs to drop further.

## Build
Needs the **OpenVINO GenAI 2026.2.1 dev archive** (headers + import libs; the base OpenVINO
runtime archive does **not** include GenAI). Match the version the app bundles (2026.2.1 core +
genai + tokenizers — same as the validated stack).

```bat
build.bat "C:\path\to\openvino_genai_2026.2.1"
```

`build.bat` fetches the two header-only deps (`cpp-httplib`, `nlohmann/json`) into
`third_party/`, sources the archive's `setupvars.bat`, then CMake-configures + builds
`build\Release\ovgenai-host.exe`.

### Runtime deployment
Ship `ovgenai-host.exe` next to the OpenVINO + GenAI runtime DLLs in
`runtimes\win-x64\native\openvino\` (the app already bundles the OV 2026.2 runtime there;
add `openvino_genai.dll` + `openvino_tokenizers.dll`), plus the INT4 IR under
`models\Qwen3-4B-Instruct-2507-ov-int4\`. Add the new binaries to the integrity manifest (§E).

## Python interim
For the next Intel test (before the C++ build is wired into the installer), a PyInstaller
bundle of `genai_host.py` (`ovgenai-host` onedir, built from the pinned `openvino-genai==2026.2.1.0`
venv) is the drop-in interim. Same env vars, same contract.

## Layout
```
native/ov-genai-host/
  src/ovgenai_host.cpp   # the sidecar (this is the product source)
  CMakeLists.txt
  build.bat              # fetch deps + configure + build
  third_party/           # httplib.h + json.hpp (fetched, gitignored)
  README.md
```
