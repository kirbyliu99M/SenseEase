"""Optimized eval runner using ContinuousBatchingPipeline + prefix caching.
The shared system-prompt prefix is prefilled once and its KV reused across
independent requests (no cross-case contamination). Demonstrates production
per-turn latency. Usage: python eval_run_cbp.py --device NPU|GPU|CPU --cases f --out f [--limit N] [--stride K]
"""
import argparse, json, time
import openvino_genai as ov

MODEL = r"C:\ovgenai\models\Qwen3-4B-Instruct-2507-ov-int4"
SYS = open(r"C:\ovgenai\eval_system_prompt.txt", encoding="utf-8").read()
STATE = ("\n[SenseEaseState] overlay: idle; central viewport normal; peripheral mask off; anchor grid faint."
         "\n[RecentAdjustments] none. [RecentEvents] none."
         "\n[habituation_state] tier=1; trajectory=steady; power_save_active=false; advisory=none; awaiting_user_confirm=false [/habituation_state]")
PIN = {"en": "US English", "zh": "Traditional Chinese", "mixed": "Traditional Chinese"}

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
    ap.add_argument("--device", required=True)
    ap.add_argument("--cases", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--stride", type=int, default=1)
    a = ap.parse_args()

    sched = ov.SchedulerConfig()
    sched.enable_prefix_caching = True
    sched.max_num_seqs = 1            # single sequence (no batching)
    sched.max_num_batched_tokens = 8192  # must exceed the ~3300-token prompt
    sched.cache_size = 4             # GB reserved for KV cache (holds the system prefix)
    try:
        sched.dynamic_split_fuse = False
    except Exception:
        pass
    props = {"CACHE_DIR": r"C:\ovgenai\ov_cache"}
    if a.device == "NPU":
        props.update({"MAX_PROMPT_LEN": 6144, "MIN_RESPONSE_LEN": 512, "GENERATE_HINT": "BEST_PERF"})
    t0 = time.time()
    pipe = ov.ContinuousBatchingPipeline(MODEL, sched, a.device, props)
    print(f"[{a.device}/CBP] loaded in {time.time()-t0:.1f}s", flush=True)
    tok = pipe.get_tokenizer()

    cases = [json.loads(l) for l in open(a.cases, encoding="utf-8") if l.strip()]
    cases = cases[::a.stride]
    if a.limit: cases = cases[:a.limit]

    g = ov.GenerationConfig(); g.max_new_tokens = 160; g.apply_chat_template = False
    done = 0
    with open(a.out, "w", encoding="utf-8") as fo:
        for c in cases:
            pin = PIN.get(c.get("lang", "en"), "Traditional Chinese")
            sysmsg = SYS + STATE + f"\nPINNED LANGUAGE FOR THIS TURN: {pin}. Reply ONLY in {pin}."
            prompt = tok.apply_chat_template(
                [{"role": "system", "content": sysmsg}, {"role": "user", "content": c["text"]}],
                add_generation_prompt=True)
            t = time.time()
            try:
                res = pipe.generate([prompt], [g])
                raw = res[0].m_generation_ids[0] if hasattr(res[0], "m_generation_ids") else str(res[0])
                err = None
            except Exception as e:
                raw = ""; err = f"{type(e).__name__}: {e}"
            dt = time.time() - t
            parsed = first_json(raw)
            rec = {**c, "device": a.device + "/CBP", "latency_s": round(dt, 2), "raw": raw,
                   "parsed": parsed, "parse_ok": parsed is not None, "error": err}
            fo.write(json.dumps(rec, ensure_ascii=False) + "\n"); fo.flush()
            done += 1
            if done % 10 == 0:
                print(f"[{a.device}/CBP] {done}/{len(cases)} last={dt:.1f}s", flush=True)
    print(f"[{a.device}/CBP] DONE {done} -> {a.out}", flush=True)

if __name__ == "__main__":
    main()
