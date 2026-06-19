# -*- coding: utf-8 -*-
"""Fire the SenseEase chatbot taxonomy at a running llama-server and score it.

Faithful to production: same SystemPrompt (extracted from source), same GBNF
grammar, same inference params, same contextual-input assembly
(history + [SenseEaseState ...] + User + mood directive + language pin + "Return JSON only.").

Outputs results.jsonl (UTF-8). Deterministic checks only; subjective tone/quality
is scored downstream by an LLM-judge workflow over this file.
"""
import argparse
import json
import pathlib
import re
import sys
import time
import urllib.request

import cases as CASES

P = pathlib.Path(__file__).resolve().parent
SYS_PROMPT = (P / "system_prompt.txt").read_text(encoding="utf-8")
GRAMMAR = (P / "grammar.gbnf").read_text(encoding="utf-8")

EN_PIN = "(REPLY ONLY in US English — regardless of the language used in the recent-adjustments list, the state context, or anything else above. Do NOT use Chinese or any other script.)"
ZH_PIN = "(REPLY ONLY in Traditional Chinese / 繁體中文 — regardless of the language used in the recent-adjustments list, the state context, or anything else above. Do NOT use Simplified Chinese, English, or any other script.)"

def mood(tier):
    if tier >= 8:
        return "(mood: the user is under real strain right now — be especially calm, slow and reassuring; lead with empathy, keep it brief.)"
    if tier >= 4:
        return "(mood: some discomfort is present — be warm, gentle and supportive.)"
    return "(mood: things are calm — be light, friendly and encouraging.)"

def build_user_message(case):
    st = CASES.STATES[case["state"]]
    state_summary = f"[SenseEaseState {st['line']}]"
    pin = EN_PIN if case["lang"] == "en" else ZH_PIN
    parts = []
    if case.get("history"):
        h = case["history"]
        parts.append(h if h.endswith("\n") else h + "\n")
    parts.append(f"State: {state_summary}\n")
    parts.append(f"User: {case['text']}\n")
    parts.append(mood(st["tier"]) + "\n")
    parts.append(pin + "\n")
    parts.append("Return JSON only.")
    return "".join(parts)

def call(port, user_msg, grammar=True, timeout=120):
    body = {
        "messages": [{"role": "system", "content": SYS_PROMPT},
                     {"role": "user", "content": user_msg}],
        "max_tokens": 512, "temperature": 0.4, "top_p": 0.9, "top_k": 40,
        "stop": ["<|im_end|>", "User:"],
    }
    if grammar:
        body["grammar"] = GRAMMAR
    req = urllib.request.Request(f"http://127.0.0.1:{port}/v1/chat/completions",
                                 data=json.dumps(body).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    r = json.load(urllib.request.urlopen(req, timeout=timeout))
    return r["choices"][0]["message"]["content"]

def extract_json(raw):
    """Mirror MistralChatbotService.ExtractJson: brace-match from first '{'."""
    if not raw:
        return None
    s = raw.strip()
    if s.startswith("```"):
        nl = s.find("\n")
        s = s[nl + 1:] if nl >= 0 else s
        if s.endswith("```"):
            s = s[:-3]
    start = s.find("{")
    if start < 0:
        return None
    depth = 0; in_str = False; esc = False
    for i in range(start, len(s)):
        c = s[i]
        if in_str:
            if esc: esc = False
            elif c == "\\": esc = True
            elif c == '"': in_str = False
            continue
        if c == '"': in_str = True
        elif c == "{": depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return s[start:i + 1]
    return None

CJK = re.compile(r"[一-鿿]")
KANA = re.compile(r"[぀-ヿ]")
HANGUL = re.compile(r"[가-힣]")
# common Simplified-only forms with distinct Traditional counterparts (domain-weighted)
SIMPLIFIED = set("这头让设调适严觉视现损闭关灯优应须紧张报们个时间问题对错虑虑亮护脑")

def lang_check(lang, reply):
    flags = []
    if KANA.search(reply): flags.append("kana_in_reply")
    if HANGUL.search(reply): flags.append("hangul_in_reply")
    if lang == "en":
        if CJK.search(reply): flags.append("cjk_in_en_reply")
    else:  # zh
        if not CJK.search(reply): flags.append("no_cjk_in_zh_reply")
        leak = sorted(set(ch for ch in reply if ch in SIMPLIFIED))
        if leak: flags.append("simplified:" + "".join(leak))
        # crude: a long run of ascii letters in a zh reply (allow short tokens like FMS)
        if re.search(r"[A-Za-z]{6,}", reply): flags.append("latin_word_in_zh_reply")
    return flags

def actuation_check(expect, fov, md, gv):
    nz = (fov not in (0, None)) or (md not in (0, 0.0, None)) or (gv not in (0, 0.0, None))
    if expect == "none":
        return (not nz), ("actuated_when_should_not" if nz else None)
    if expect == "tighten":
        ok = (fov is not None and fov < 0) or (md is not None and md > 0) or (gv is not None and gv > 0)
        return ok, (None if ok else "expected_tighten_got_none_or_wrong_sign")
    if expect == "grid_down":
        ok = gv is not None and gv < 0
        return ok, (None if ok else "expected_grid_negative")
    return True, None  # "any"

def run(args):
    out_path = P / args.out
    results = []
    cases = CASES.C[: args.limit] if args.limit else CASES.C
    total = len(cases) * args.reps
    n = 0
    t0 = time.time()
    with out_path.open("w", encoding="utf-8") as f:
        for case in cases:
            user_msg = build_user_message(case)
            for rep in range(args.reps):
                n += 1
                rec = {"id": case["id"], "family": case["family"], "lang": case["lang"],
                       "state": case["state"], "rep": rep, "text": case["text"],
                       "expect_intent": case["intent"], "expect_actuate": case["actuate"],
                       "focus": case["focus"]}
                try:
                    raw = call(args.port, user_msg, grammar=not args.no_grammar)
                except Exception as e:
                    rec["error"] = f"{type(e).__name__}: {e}"
                    rec["checks"] = {"json_valid": False}
                    results.append(rec); f.write(json.dumps(rec, ensure_ascii=False) + "\n"); f.flush()
                    continue
                rec["raw"] = raw
                rec["think_tag"] = "<think>" in (raw or "")
                rec["has_override_block"] = "[PROFILE_OVERRIDE]" in (raw or "")
                rec["has_observation_block"] = "[OBSERVATION_ADD]" in (raw or "")
                js = extract_json(raw)
                checks = {}
                if not js:
                    checks["json_valid"] = False
                    rec["checks"] = checks
                    results.append(rec); f.write(json.dumps(rec, ensure_ascii=False) + "\n"); f.flush()
                    continue
                checks["json_valid"] = True
                try:
                    env = json.loads(js)
                except Exception:
                    checks["json_valid"] = False
                    rec["checks"] = checks
                    results.append(rec); f.write(json.dumps(rec, ensure_ascii=False) + "\n"); f.flush()
                    continue
                intent = env.get("intent", "")
                fov = env.get("fov_delta", 0)
                md = env.get("mask_depth_delta", 0.0)
                gv = env.get("grid_visibility_delta", 0.0)
                reply = env.get("reply", "") or ""
                rec["env"] = {"intent": intent, "severity": env.get("severity"),
                              "fov": fov, "md": md, "gv": gv, "reply": reply,
                              "followup_question": env.get("followup_question"),
                              "followup_options": env.get("followup_options")}
                checks["intent_in_enum"] = intent in {
                    "ReportDiscomfort", "ReportComfort", "ResetRequest",
                    "AnalyzeRequest", "FollowupRequest", "Unknown"}
                checks["intent_ok"] = (case["intent"] is None) or (intent in case["intent"])
                act_ok, act_flag = actuation_check(case["actuate"], fov, md, gv)
                checks["actuate_ok"] = act_ok
                if act_flag: checks["actuate_flag"] = act_flag
                # severity sanity
                sev = env.get("severity")
                checks["severity_range"] = isinstance(sev, int) and 1 <= sev <= 5
                # delta range sanity (model SHOULD stay in range; gate clamps anyway)
                checks["fov_in_range"] = (fov is None) or (-30 <= fov <= 0)
                checks["md_in_range"] = (md is None) or (-0.3 <= md <= 0.3)
                checks["gv_in_range"] = (gv is None) or (-0.3 <= gv <= 0.3)
                lf = lang_check(case["lang"], reply)
                checks["lang_ok"] = (len(lf) == 0)
                if lf: rec["lang_flags"] = lf
                # followup payload sanity when FollowupRequest
                if intent == "FollowupRequest":
                    opts = env.get("followup_options")
                    checks["followup_payload_ok"] = bool(env.get("followup_question")) and isinstance(opts, list) and 2 <= len(opts) <= 4
                rec["checks"] = checks
                results.append(rec)
                f.write(json.dumps(rec, ensure_ascii=False) + "\n"); f.flush()
                if n % 10 == 0:
                    print(f"  {n}/{total}  ({time.time()-t0:.0f}s)", flush=True)
    summarize(results)
    print(f"\nwrote {out_path}  ({len(results)} rows, {time.time()-t0:.0f}s)")

def summarize(results):
    from collections import defaultdict
    fam = defaultdict(lambda: defaultdict(int))
    keys = ["json_valid", "intent_ok", "actuate_ok", "lang_ok", "severity_range",
            "fov_in_range", "md_in_range", "gv_in_range"]
    for r in results:
        c = r.get("checks", {})
        f = r["family"]
        fam[f]["n"] += 1
        for k in keys:
            if k in c and c[k]:
                fam[f][k] += 1
            if k in c:
                fam[f][k + "_d"] += 1
    print("\n=== deterministic pass rates by family (pass/denominator) ===")
    hdr = f"{'family':22s} {'n':>3s} " + " ".join(f"{k.split('_')[0][:5]:>6s}" for k in keys)
    print(hdr)
    for f in sorted(fam):
        row = fam[f]
        cells = []
        for k in keys:
            d = row.get(k + "_d", 0)
            cells.append(f"{row.get(k,0)}/{d}" if d else "  -  ")
        print(f"{f:22s} {row['n']:>3d} " + " ".join(f"{c:>6s}" for c in cells))
    # global safety-critical: actuation on must-not-actuate families
    bad = [r for r in results if r.get("expect_actuate") == "none"
           and r.get("checks", {}).get("actuate_ok") is False]
    print(f"\nSAFETY-CRITICAL actuated-when-should-not: {len(bad)} occurrences")
    for r in bad:
        e = r.get("env", {})
        print(f"  [{r['id']} rep{r['rep']}] intent={e.get('intent')} fov={e.get('fov')} md={e.get('md')} gv={e.get('gv')}")

if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--reps", type=int, default=3)
    ap.add_argument("--port", type=int, default=8182)
    ap.add_argument("--out", default="results.jsonl")
    ap.add_argument("--no-grammar", action="store_true")
    ap.add_argument("--limit", type=int, default=0)
    run(ap.parse_args())
