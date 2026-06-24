# OpenVINO GenAI host — operations & packaging

The Intel LLM backend (2026-06-24 pivot). The retired ggml-openvino + GGUF path is replaced by an
**OpenAI-compatible host** (`ov::genai::LLMPipeline`) serving an **INT4 IR** of Qwen3-4B-Instruct-2507 on the
Intel **NPU / iGPU**. SenseEase drives it over the same external-backend seam as FastFlowLM. This doc is the
single source of truth for how the app launches, talks to, and ships the host.

Validated stack: `openvino==2026.2.1`, `openvino-genai==2026.2.1.0`, `openvino-tokenizers==2026.2.1.0`.

---

## Host contract

Two interchangeable implementations, byte-for-byte equivalent behaviour:
- **Python interim host** (`genai_host.py` → PyInstaller `genai_host.exe`, ~285 MB) — the field-test host.
- **Native C++ sidecar** (`native/ov-genai-host/src/ovgenai_host.cpp`) — the shipping host.

Both speak OpenAI on `127.0.0.1:<port>` and expose:
- `GET /health` → `{"status":"ready"|"compiling"|"error","device":"NPU|GPU|CPU"}`, **HTTP 200 when ready, 503 otherwise**.
- `GET /v1/models` → 200 (lists the model) **the instant the socket binds** — NOT a readiness signal.
- `POST /v1/chat/completions` → 503 `{"error":"model not ready"}` until ready, else the completion.

**Request body — chunked transfer-encoding (Intel field bug, 2026-06-25):** the .NET `HttpClient` posts the
chat request with `Transfer-Encoding: chunked` (no `Content-Length`). The C++ host's **cpp-httplib de-chunks
this natively** into `req.body` (verify on the Intel box before shipping); the Python hosts' bare `http.server`
does NOT, so they were taught to de-chunk manually. An empty / missing-`messages` body must fail with **400**,
never generate from nothing — an empty body produced ~28 s of off-prompt text the app then rendered as a
lexicon "which-symptom" card (the misdiagnosed "never generates" symptom).

Env (identical across both hosts):

| Var | Default | Meaning |
|---|---|---|
| `OVHOST_MODEL` | *(required)* | path to the OpenVINO IR model dir |
| `OVHOST_DEVICE` | `NPU` | `NPU` \| `GPU` \| `CPU` (the app requests `NPU`; the host self-falls-back — see below) |
| `OVHOST_PORT` | `8181` | listen port |
| `OVHOST_MODELID` | `qwen3-4b` | model id reported in responses + sent by the app |
| `OVHOST_CACHE` | `C:\ovgenai\ov_cache` | `CACHE_DIR` — the app overrides this to `%LOCALAPPDATA%\SenseEase\ov_cache` |
| `OVHOST_LOG` | `%LOCALAPPDATA%\SenseEase\llama-server-genai.log` | host log |

---

## Device auto-probe — NPU → iGPU (decision #1)

The host owns the device fallback, so it works on the field-test (external) host AND the shipping (spawned)
host without app-side logic:
- **Load-time:** if the NPU pipeline fails to compile/load, the host retries once on the **iGPU** (same IR,
  equivalent quality) before reporting `error`. `/health` stays `compiling` across the retry.
- **Generate-time:** a persistent NPU `ZE_RESULT_ERROR_DEVICE_LOST` (unrecoverable by a same-device reload)
  falls the host back to the **iGPU** mid-session.

Only NPU falls back; a GPU/CPU failure is terminal → the app's selector drops to the Vulkan/CPU server.

---

## NPU KV / prefix caching (`NPUW_LLM_ENABLE_PREFIX_CACHING`)

Distinct from `CACHE_DIR` (which caches the *compiled blob*) — this caches the *computed KV*. The GenAI
`LLMPipeline` does **not** do resident-KV continuation on NPU (it `reset_state()`s + re-presents the full
prompt every turn; that path is CPU/GPU-only), and the ContinuousBatching `enable_prefix_caching` flag is
silently ignored on NPU. **But** OpenVINO **2025.4+** (PR #31669) added an NPU-plugin property,
`NPUW_LLM_ENABLE_PREFIX_CACHING:YES`, that reuses the KV of a repeated **prefix** (our identical system
prompt) across turns → lower TTFT. Our 2026.2.1 stack is downstream of 2025.4, so it should be available.

The host sets it on the NPU config, gated by `OVHOST_PREFIX_CACHE` (default `YES`; `NO` disables it for an
A/B). Caveats: it's an NPU-plugin key (verify `LLMPipeline` forwards it — openvino.genai #3366/#35641), it's
default-OFF in the plugin, and it does **not** lift the static `MAX_PROMPT_LEN=6144` window — a too-long prompt
still overflows, so the prompt-shrink work is separate and complementary. A rejected property throws at NPU
load, which the load-time NPU→iGPU fallback catches (no wedge). **Validate with a TTFT A/B on the Arrow Lake
box before relying on it.**

**Batch caveat (Intel field, 2026-06-25):** prefix caching can WEDGE the NPU under batch / concurrent load —
keep it OFF (`OVHOST_PREFIX_CACHE=NO`) for batch evals run against the host. The app is interactive (one turn
at a time, the case prefix caching is for), so the default-ON is correct there; only batch runs need it off.

---

## App integration

- **Launcher:** `OvGenAiHostLauncher.TryAcquireAsync` (mirrors `FlmServerLauncher`). Reuse-or-launch on
  `:8181`, owns the process it launches (reaped via the Job Object on crash/exit), returns a `ManagedBackend`.
  - Host exe: `SENSEEASE_OVHOST_EXE` override → else `packs\intel-ov\host\genai_host.exe` under the app dir.
  - IR model: `SENSEEASE_OVHOST_MODEL` / `OVHOST_MODEL` override → else `packs\intel-ov\model`.
  - Returns `null` (→ Vulkan) if either is absent, so the launcher is inert until the Intel pack ships.
- **Readiness (#38a):** `LocalLlmChatbotService.ConnectExternalAsync` gates on `/health=ready`, polling within a
  long budget (300 s) on the OV path while it compiles — never on `/v1/models`. Falls back to `/v1/models` only
  when `/health` is absent (FLM / plain llama.cpp).
- **Idle-unload disabled (decision #6):** the OV path passes `idleTimeout: 0`, so an idle window can't kill the
  host and force a re-compile.
- **Selector order:** OV host → Vulkan (the universal fallback, kept through the field test) → lexicon.
- **Field test:** the host is run externally and the app is pointed at it via `SENSEEASE_LLM_BASEURL`
  (`point-app-at-host.bat`), which uses the env-override path (bypasses the launcher).

---

## Cache & the pre-built blob (decision #3)

OpenVINO compiles the model once and stores a blob in `CACHE_DIR`, **keyed by a hash of
{model + device + plugin config + NPU driver/compiler version}**. Cache hit → import (~5 s); miss → recompile
(~237 s on the NPU) and re-cache locally. Setting `CACHE_DIR` bypasses the driver's own UMD cache.

**Blob portability is NOT guaranteed across OV / NPU-driver versions** (Intel: pre-compiled blobs are
development-only as a hard dependency). A driver update silently invalidates the blob → one recompile, then
cached. So the shipping strategy is a **hybrid**:

1. **Ship the pre-built blob** seeded into `%LOCALAPPDATA%\SenseEase\ov_cache` — the fast path on a matched box.
2. **Keep the first-run "preparing AI (~1 min, once)" message** as the graceful fallback when the blob misses
   and the host recompiles.
3. **Pin the NPU-driver version** across the controlled-AIPC fleet so the blob actually hits.

> **PINNED NPU-DRIVER VERSION (fill at blob-build time):** `<intel-npu-driver-version>` — the driver the shipped
> `ov_cache` blob was compiled against. Boxes on this driver + OV 2026.2.1 get the fast import; others recompile
> once. Record the exact `Intel(R) AI Boost` driver version from Device Manager here when the blob is built.

A bad/mismatched blob self-heals: the host treats a failed import as a compile (recompiles + re-caches).

---

## Integrity (decision #8 — hash everything incl. the IR)

- The build-time `install.manifest.json` lists the **host binaries AND the 2.2 GB IR** (see the Intel-pack block
  in `scripts/stage-pre-alpha-assets.ps1`).
- The runtime sweep (`IntegrityVerifier.VerifyManifestSweep`, called from `App.xaml.cs`) already runs on a
  **background `Task.Run`** and **streams** each file's SHA-256, so hashing the IR adds no felt startup latency
  (it overlaps the host's cold-compile window). Alpha-1 posture: a sweep mismatch is diagnostic-only (logged),
  not a hard block, until the packaging phase tightens it.
