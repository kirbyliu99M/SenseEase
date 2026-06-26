# Model conversion — Qwen3-4B-Instruct-2507 → NPU-ready INT4 OpenVINO IR

One-time, offline (a separate Python venv; does not touch the app). Produces `models\Qwen3-4B-Instruct-2507-ov-int4\`.

## Toolchain (matches OpenVINO 2026.2 runtime)
```
python -m venv ovgenai-venv
ovgenai-venv\Scripts\pip install "openvino==2026.2.*" "openvino-genai==2026.2.*" \
  "openvino-tokenizers==2026.2.*" "nncf>=2.19" "optimum-intel[openvino]" \
  "transformers>=4.51,<4.53" huggingface-hub
```
(Exact frozen versions used here: `host_requirements.txt`.)

## Convert (what was used — data-free INT4 sym, group-128)
```
optimum-cli export openvino \
  --model Qwen/Qwen3-4B-Instruct-2507 \
  --task text-generation-with-past \
  --weight-format int4 --sym --group-size 128 --ratio 1.0 \
  models/Qwen3-4B-Instruct-2507-ov-int4
```
Result: NNCF applied INT4-sym g128 to 90% of layers + INT8 per-channel backup to the 10% sensitive layers. ~2.2 GB on disk. INT4 is **mandatory** for NPU (INT8-weight IR crashes at generate).

## Optional accuracy upgrade (slower convert)
Add `--awq --scale-estimation --dataset wikitext2` for data-aware INT4 recovery. Validate NLU JSON quality after.

## Sanity check on NPU before shipping
```
python validate_2507.py NPU   # prints LOAD_SEC, DECODE_TOK_S, coherence, long-prompt prefill
```
Expected on Core Ultra 5 336H: cold compile ~237 s, warm load ~5 s, decode ~11 tok/s, coherent output + valid NLU JSON.
