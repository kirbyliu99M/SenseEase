# Mistral 7B — Model Directory

This directory is reserved for Phase 2.4+ Mistral 7B local inference integration.
**Model files are NOT committed to git** (too large). See download instructions below.

## Directory Layout

```
mistral-7b/
  gguf/          ← GGUF quantized weights (llama.cpp / LM Studio backend)
  onnx/          ← ONNX export (DirectML / Windows ML / OpenVINO backend)
  config/
    inference_config.json   ← temperature, context length, backend selection
    prompt_template.json    ← system prompt + SenseEase intent templates
```

## Recommended Model

| Format | File | Source | Size |
|---|---|---|---|
| GGUF Q4_K_M | `mistral-7b-instruct-v0.3.Q4_K_M.gguf` | HuggingFace / TheBloke | ~4.1 GB |
| GGUF Q8_0 | `mistral-7b-instruct-v0.3.Q8_0.gguf` | HuggingFace / TheBloke | ~7.7 GB |
| ONNX (INT4) | `model_int4.onnx` | microsoft/Mistral-7B-v0.1-ONNX | ~4 GB |

## Download (GGUF — recommended for Phase 2.4)

```powershell
# Install HuggingFace Hub CLI
pip install huggingface_hub

# Download Q4_K_M quantization into this folder
huggingface-cli download TheBloke/Mistral-7B-Instruct-v0.3-GGUF `
    mistral-7b-instruct-v0.3.Q4_K_M.gguf `
    --local-dir "models\mistral-7b\gguf"
```

## Integration Roadmap

- **Phase 2.3** — `MockNluService` → `HpIqNluService` (REST API)
- **Phase 2.4** — `HpIqNluService` → `MistralNluService` (local llama.cpp via LLamaSharp)
- **Phase 2.5** — OpenVINO / NPU acceleration for INT4 ONNX model

## Interface

The existing `INluService` contract in `src/SenseEase.Core/Abstractions/INluService.cs`
is already defined. Phase 2.4 only needs to implement the interface — no architecture changes.
