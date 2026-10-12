# Dependencies not stored in git / 不在 git 中的相依檔案

The app's AI models, native runtimes and prebuilt helper programs are too large for git (several GB) and
some are third-party binaries. They are listed in [`scripts/deps/deps.manifest.json`](../scripts/deps/deps.manifest.json)
with their exact location and a pinned SHA-256, and restored with one script.

```powershell
# What is present / missing / changed (downloads nothing)
powershell -ExecutionPolicy Bypass -File scripts\deps\Restore-Deps.ps1 -Verify

# Restore everything that is missing
powershell -ExecutionPolicy Bypass -File scripts\deps\Restore-Deps.ps1
```

Public components download from their original source. The rest come from the **private** store
`kirbyliu99M/SenseEase-deps` (release `deps-v1.3.2`), read with the GitHub CLI. Run `gh auth login`
first; no token or credential is kept in this repository. Restore never deletes local files: a
folder that has to be replaced is renamed to `<name>.old-<timestamp>`.

## Components

| Id | Destination | Size | Source | Used by |
|---|---|---|---|---|
| `qwen3-gguf` | `models/Qwen3-4B-Instruct-2507-GGUF/…-Q4_K_M.gguf` | 2.4 GB | Public: [lmstudio-community/Qwen3-4B-Instruct-2507-GGUF](https://huggingface.co/lmstudio-community/Qwen3-4B-Instruct-2507-GGUF) (hash-verified identical) | Vulkan llama-server chat backend |
| `face-landmarker` | `face_landmarker.task` (+ copy in `tools/head-tracker-sidecar/`) | 3.6 MB | Public: MediaPipe `float16/latest`; private backup because "latest" can change | Head-tracking sidecar |
| `qwen3-ov-int4` | `models/Qwen3-4B-Instruct-2507-ov-int4/` | 2.2 GB | Private. Rebuild: `models/recipes/convert_2507.sh` | Intel OpenVINO GenAI host |
| `qwen3-npu2-q4nx` | `models/Qwen3-4B-NPU2/` | 3.1 GB | Private. Pinned to FastFlowLM v0.9.43; the current Hugging Face copy is a different build | AMD Ryzen AI NPU |
| `native-runtimes` | `runtimes/win-x64/native/` (`vulkan/`, `openvino/`) | 323 MB | Private. Rebuild: llama.cpp [b9294](https://github.com/ggml-org/llama.cpp/releases/tag/b9294) Vulkan zip + `installer/build-openvino-llama-server.bat` | Vulkan backend; copied into builds by the `.csproj` |
| `ovgenai-host-py` | `%USERPROFILE%\ovhost-build\dist\ovgenai-host\` | 285 MB | Private. Rebuild: `native/ov-genai-host-py` | OV-Python and Auto packages |
| `ovgenai-host-cpp` | `%USERPROFILE%\ovhost-build\ovgenai-host-cpp\` | 216 MB | Private. Rebuild: `native/ov-genai-host` (OpenVINO GenAI 2026.2.1) | OV-Cpp package |
| `fastflowlm` | `C:\Program Files\flm\` | 168 MB | Private (FastFlowLM v0.9.43). Restore needs an Administrator shell | AMD packages |
| `head-tracker-sidecar` | `tools/head-tracker-sidecar/dist/head_tracker/` | 604 MB | Private. Rebuild: `pyinstaller head_tracker.spec` in `tools/head-tracker-sidecar` | Head tracking |

A whole folder is verified with a **tree hash**: SHA-256 over the sorted lines `<relative/path><TAB><sha256>`
(see `Get-TreeHash` in [`scripts/deps/DepsCommon.ps1`](../scripts/deps/DepsCommon.ps1)).

## Licences

Model weights: Qwen3 (Apache-2.0). MediaPipe (Apache-2.0). OpenVINO and OpenVINO GenAI (Apache-2.0).
llama.cpp (MIT). FastFlowLM has its own licence: check it before redistributing, and keep its
binaries and its Q4NX model in the private store only.

## Updating the backup

After a dependency changes (new model, new runtime build):

1. Update its `sha256` / `treeHash` in `deps.manifest.json`; `Restore-Deps.ps1 -Verify` shows what changed.
2. Bump `privateStore.tag` (for example `deps-v1.3.3`), so earlier releases stay restorable.
3. Pack and upload. Upload refuses to run unless the target repository is **private**:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\deps\Pack-Deps.ps1            # pack only, into %USERPROFILE%\SenseEase-deps-backup\<tag>
powershell -ExecutionPolicy Bypass -File scripts\deps\Pack-Deps.ps1 -Upload    # pack + upload to the private store
```

## Installer builds

The packages are built in three steps: publish, stage, then compile with Inno Setup.
- **Stage:** [`installer/_stage-beta-v1.1.ps1`](../installer/_stage-beta-v1.1.ps1) lays out each package under `dist/`, taking the helper programs from the locations above.
- **Compile:** `installer/_build_v13*.ps1` runs Inno Setup for each package. These scripts contain absolute paths for the maintainer's machine; adjust `$repo` and `$ISCC` elsewhere.

Installer output (`installer/beta-v*/`) and staging (`dist/`) are git-ignored.
