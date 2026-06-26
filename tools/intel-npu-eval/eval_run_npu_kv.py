"""KV/prefix NPU eval via chat-mode (start_chat keeps the system prompt resident).
On NPU this is the only KV-reuse code path; cases are kept independent
(start_chat/finish_chat per case) so accuracy is clean. Includes device-loss recovery.
Usage: python eval_run_npu_kv.py --cases f --out f [--start N]"""
import argparse, json, time
import openvino_genai as ov

MODEL = r"C:\ovgenai\models\Qwen3-4B-Instruct-2507-ov-int4"
SYS = open(r"C:\ovgenai\eval_system_prompt.txt", encoding="utf-8").read()
STATE = ("\n[SenseEaseState] overlay: idle; central viewport normal; peripheral mask off; anchor grid faint."
         "\n[RecentAdjustments] none. [RecentEvents] none."
         "\n[habituation_state] tier=1; trajectory=steady; power_save_active=false; advisory=none; awaiting_user_confirm=false [/habituation_state]")
PIN = {"en": "US English", "zh": "Traditional Chinese", "mixed": "Traditional Chinese"}
CFG = {"CACHE_DIR": r"C:\ovgenai\ov_cache_fresh", "MAX_PROMPT_LEN": 6144, "MIN_RESPONSE_LEN": 512, "GENERATE_HINT": "BEST_PERF"}

def first_json(text):
    s = text.find("{")
    if s < 0: return None
    depth = 0; instr = False; esc = False
    for i in range(s, len(text)):
        ch = text[i]
        if esc: esc = False; continue
        if ch == "\\": esc = True; continue
        if ch == '"': instr = not instr
        if instr: continue
        if ch == "{": depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                try: return json.loads(text[s:i+1])
                except Exception: return None
    return None

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cases", required=True); ap.add_argument("--out", required=True)
    ap.add_argument("--start", type=int, default=0); ap.add_argument("--limit", type=int, default=0)
    a = ap.parse_args()
    pipe = ov.LLMPipeline(MODEL, "NPU", **CFG)
    print("[NPU/KV] loaded", flush=True)
    g = ov.GenerationConfig(); g.max_new_tokens = 160
    cases = [json.loads(l) for l in open(a.cases, encoding="utf-8") if l.strip()][a.start:]
    if a.limit: cases = cases[:a.limit]
    done = 0
    with open(a.out, "w", encoding="utf-8") as fo:
        for c in cases:
            pin = PIN.get(c.get("lang", "en"), "Traditional Chinese")
            sysmsg = SYS + STATE + f"\nPINNED LANGUAGE FOR THIS TURN: {pin}. Reply ONLY in {pin}."
            t = time.time(); raw = ""; err = None
            aborted = False
            try:
                pipe.start_chat(sysmsg)
                raw = str(pipe.generate(c["text"], g))
                pipe.finish_chat()
            except Exception as e:
                err = f"{type(e).__name__}: {str(e)[:120]}"
                if "DEVICE_LOST" in str(e) or "device hung" in str(e):
                    print(f"[NPU/KV] DEVICE_LOST id={c.get('id')} — aborting chunk", flush=True)
                    aborted = True
            dt = time.time() - t
            parsed = first_json(raw)
            rec = {**c, "device": "NPU/KV", "latency_s": round(dt, 2), "raw": raw,
                   "parsed": parsed, "parse_ok": parsed is not None, "error": err}
            fo.write(json.dumps(rec, ensure_ascii=False) + "\n"); fo.flush()
            done += 1
            if aborted: break
            if done % 10 == 0: print(f"[NPU/KV] {done}/{len(cases)} last={dt:.1f}s", flush=True)
    print(f"[NPU/KV] DONE {done} -> {a.out}", flush=True)

if __name__ == "__main__":
    main()
