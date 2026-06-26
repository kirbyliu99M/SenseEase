#!/bin/bash
# Resilient chunked NPU eval: fresh process per 80-case chunk (keeps per-context
# request count under the ~298 device-hang threshold), progress watchdog, incremental
# append, abort on persistent wedge. Runs naive-continue then KV/prefix.
cd C:/ovgenai
export PYTHONUTF8=1
PY="C:/ovgenai/venv/Scripts/python.exe"
CHUNK=80
WATCHDOG=120   # seconds with no new row -> kill the chunk

run_chunked() {
  local runner="$1"; local total="$2"; local gstart="$3"; local out="$4"
  local pos=$gstart; local stall=0
  [ "$gstart" -eq 0 ] && : > "$out"   # only truncate for a from-scratch run
  while [ "$pos" -lt "$total" ]; do
    local cf="C:/ovgenai/_chunk_${pos}.jsonl"; rm -f "$cf"
    echo "[orch] $(date +%H:%M:%S) chunk pos=$pos/$total"
    $runner --start "$pos" --limit "$CHUNK" --out "$cf" >> orch.log 2>&1 &
    local pid=$!
    local last=0; local idle=0
    while kill -0 "$pid" 2>/dev/null; do
      sleep 15
      local now=$(wc -l < "$cf" 2>/dev/null || echo 0)
      if [ "$now" -gt "$last" ]; then last=$now; idle=0; else idle=$((idle+15)); fi
      if [ "$idle" -ge "$WATCHDOG" ]; then
        echo "[orch] watchdog kill pos=$pos (idle ${idle}s rows=$now)"; kill -9 "$pid" 2>/dev/null; break
      fi
    done
    wait "$pid" 2>/dev/null
    local got=$(wc -l < "$cf" 2>/dev/null || echo 0)
    local okc=$(grep -c '"parse_ok": true' "$cf" 2>/dev/null || echo 0)
    if [ "$got" -gt 0 ]; then cat "$cf" >> "$out"; pos=$((pos+got)); fi
    if [ "$okc" -eq 0 ]; then stall=$((stall+1)); echo "[orch] no valid rows (stall=$stall)"; else stall=0; fi
    if [ "$stall" -ge 3 ]; then echo "[orch] NPU appears wedged at pos=$pos after $stall stalls; STOPPING run"; return 1; fi
    sleep 3
  done
  echo "[orch] run COMPLETE pos=$pos"; return 0
}

echo "[orch] === START $(date +%H:%M:%S) ==="
echo "[orch] --- RUN 1: naive NPU continue (298..622) ---"
run_chunked "$PY eval_run.py --device NPU --cases eval_cases.jsonl" 622 298 eval_npu_tail.jsonl
echo "[orch] naive-continue ended $(date +%H:%M:%S)"
echo "[orch] --- RUN 2: KV/prefix NPU (0..622) ---"
run_chunked "$PY eval_run_npu_kv.py --cases eval_cases.jsonl" 622 0 eval_npu_kv.jsonl
echo "[orch] === ALL NPU RUNS DONE $(date +%H:%M:%S) ==="
