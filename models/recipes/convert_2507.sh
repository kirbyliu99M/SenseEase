#!/bin/bash
# Convert the app's exact checkpoint (Qwen3-4B-Instruct-2507, non-thinking) to NPU-ready INT4 IR.
export PYTHONUTF8=1
export HF_HUB_DISABLE_TELEMETRY=1
OUT="C:/ovgenai/models/Qwen3-4B-Instruct-2507-ov-int4"
echo "convert start $(date +%H:%M:%S)"
"C:/ovgenai/venv/Scripts/optimum-cli.exe" export openvino \
  --model Qwen/Qwen3-4B-Instruct-2507 \
  --task text-generation-with-past \
  --weight-format int4 --sym --group-size 128 --ratio 1.0 \
  "$OUT" 2>&1 | tail -40
echo "convert exit=${PIPESTATUS[0]} $(date +%H:%M:%S)"
ls -la "$OUT" 2>/dev/null
