# OpenVINO GenAI host — Python SKU (source)

The **Python** build of the Intel OpenVINO GenAI host. It is the validated reference host (passed the
622-case NLU eval) and ships in the `py` and `auto` installer variants. The native **C++** SKU lives next
door in [`../ov-genai-host`](../ov-genai-host) and is the production sidecar; this Python one is the interim /
reference host. Both speak the same OpenAI contract on `127.0.0.1:8181` and read the same `OVHOST_*` env
(`OVHOST_MODEL` / `OVHOST_DEVICE` / `OVHOST_PORT` / `OVHOST_MODELID` / `OVHOST_CACHE` / `OVHOST_PREFIX_CACHE`) —
see [`../../docs/OV_GENAI_HOST.md`](../../docs/OV_GENAI_HOST.md) for the runtime/cache/ops contract.

Vendored into the repo 2026-06-26 when the `SenseEase-Debug-intel-2` dev folder was retired — previously the
only canonical copy lived in the un-versioned `~/ovhost-build`.

## Files
- `genai_host.py` — the host (background load → `/health` ready|compiling|error; `/v1/chat/completions`;
  NPU greedy; device-loss → iGPU fallback; NPUW prefix-cache; chunked-body de-chunk).
- `ovgenai-host.spec` — PyInstaller spec → a standalone one-dir `ovgenai-host` (all 6 OV plugins incl. npu+gpu).
- `host_requirements.txt` — frozen deps (the validated pin is `openvino-genai==2026.2.1.0` + tokenizers).

## Build (PyInstaller one-dir)
On a box with Python 3.12:
```
python -m venv venv
venv\Scripts\python -m pip install -r host_requirements.txt pyinstaller
venv\Scripts\python -m PyInstaller ovgenai-host.spec
```
Output: `dist\ovgenai-host\` (≈285 MB). The installer stage script renames `ovgenai-host.exe` → `genai_host.exe`
so `OvGenAiHostLauncher.FindExe` resolves it under `packs\intel-ov\host\`.

> Note: invoke PyInstaller as `python -m PyInstaller` (the `pyinstaller.exe` launcher is AV-blocked on the
> dev box — see the `trendmicro-av-environment` note).
