"""Run the NLU eval set through the OpenVINO model on a target device.
Usage: python eval_run.py --device NPU|GPU --cases <in.jsonl> --out <out.jsonl> [--limit N] [--stride K --offset O]
Records per case: raw output, latency, parsed JSON (best-effort), and parse_ok.
"""
import argparse, json, time, re, sys
import openvino_genai as ov

MODEL = r"C:\ovgenai\models\Qwen3-4B-Instruct-2507-ov-int4"
SYS = open(r"C:\ovgenai\eval_system_prompt.txt", encoding="utf-8").read()
STATE = ("\n[SenseEaseState] overlay: idle; central viewport normal; peripheral mask off; anchor grid faint."
         "\n[RecentAdjustments] none. [RecentEvents] none."
         "\n[habituation_state] tier=1; trajectory=steady; power_save_active=false; advisory=none; awaiting_user_confirm=false [/habituation_state]")
PIN = {"en": "US English", "zh": "Traditional Chinese", "mixed": "Traditional Chinese"}

def first_json(text):
    """Extract the first balanced {...} object from the model output."""
    s = text.find("{")
    if s < 0:
        return None
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
                frag = text[s:i+1]
                try: return json.loads(frag)
                except Exception: return None
    return None

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--device", required=True)
    ap.add_argument("--cases", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--stride", type=int, default=1)
    ap.add_argument("--offset", type=int, default=0)
    ap.add_argument("--start", type=int, default=0)
    a = ap.parse_args()

    cfg = {"CACHE_DIR": r"C:\ovgenai\ov_cache_fresh"}
    if a.device == "NPU":
        cfg.update({"MAX_PROMPT_LEN": 6144, "MIN_RESPONSE_LEN": 512, "GENERATE_HINT": "BEST_PERF"})
    t0 = time.time()
    pipe = ov.LLMPipeline(MODEL, a.device, **cfg)
    print(f"[{a.device}] loaded in {time.time()-t0:.1f}s", flush=True)
    tok = pipe.get_tokenizer()

    cases = [json.loads(l) for l in open(a.cases, encoding="utf-8") if l.strip()]
    cases = cases[a.start:]
    cases = cases[a.offset::a.stride]
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
                raw = str(pipe.generate(prompt, g)); err = None
            except Exception as e:
                # fast-fail (no in-process reload — it hangs on a wedged NPU). The
                # chunked orchestrator resets via a fresh process between chunks.
                raw = ""; err = f"{type(e).__name__}: {str(e)[:120]}"
                if "DEVICE_LOST" in str(e) or "device hung" in str(e):
                    print(f"[{a.device}] DEVICE_LOST at id={c.get('id')} — aborting chunk", flush=True)
                    fo.write(json.dumps({**c, "device": a.device, "latency_s": round(time.time()-t,2),
                                         "raw": "", "parsed": None, "parse_ok": False, "error": err}) + "\n")
                    fo.flush(); break
            dt = time.time() - t
            parsed = first_json(raw)
            rec = {**c, "device": a.device, "latency_s": round(dt, 2), "raw": raw,
                   "parsed": parsed, "parse_ok": parsed is not None, "error": err}
            fo.write(json.dumps(rec, ensure_ascii=False) + "\n"); fo.flush()
            done += 1
            if done % 10 == 0:
                print(f"[{a.device}] {done}/{len(cases)}  last={dt:.1f}s", flush=True)
    print(f"[{a.device}] DONE {done} cases -> {a.out}", flush=True)

if __name__ == "__main__":
    main()
