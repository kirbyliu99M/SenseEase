"""Programmatic scoring of NLU eval results.
Usage: python score_eval.py <results.jsonl> [label]
Computes: JSON deliverability, intent accuracy, actuation safety, language compliance, latency.
Also writes <results>.flagged.jsonl with failures/violations for LLM judging.
"""
import json, sys, re, statistics
from collections import Counter, defaultdict

path = sys.argv[1]
label = sys.argv[2] if len(sys.argv) > 2 else path

# common Simplified-only characters (a subset) -> flag when reply pinned Traditional
SIMP = set("这个们时现觉说过还乐买卖东车专实读语门问间题对应义习书写汉变开关闭电话脑见观给经济关风风际报")
KANA = re.compile(r"[぀-ヿ]")  # hiragana/katakana
CJK = re.compile(r"[一-鿿]")
DELTA_KEYS = ("fov_delta", "mask_depth_delta", "grid_visibility_delta")

def has_delta(p):
    if not isinstance(p, dict): return None
    for k in DELTA_KEYS:
        v = p.get(k)
        if isinstance(v, (int, float)) and v != 0:
            return True
    return False

def reply_text(p):
    return (p or {}).get("reply", "") if isinstance(p, dict) else ""

rows = [json.loads(l) for l in open(path, encoding="utf-8") if l.strip()]
n = len(rows)
parse_ok = sum(1 for r in rows if r.get("parse_ok"))
flagged = []

intent_total = Counter(); intent_correct = Counter()
lang_total = Counter(); lang_correct = Counter()
# actuation confusion
act_tp = act_fn = 0   # expected actuate
false_actuate = 0; non_act_total = 0
sev_bad = 0; reply_empty = 0; lang_viol = 0
latencies = [r["latency_s"] for r in rows if isinstance(r.get("latency_s"), (int, float))]

for r in rows:
    p = r.get("parsed")
    exp = r["expected_intent"]; acc = set(r.get("acceptable_intents") or [exp]); acc.add(exp)
    lang = r.get("lang"); pinned_zh = lang in ("zh", "mixed")
    intent_total[exp] += 1; lang_total[lang] += 1
    ok_intent = ok_act = ok_lang = True

    if not r.get("parse_ok") or not isinstance(p, dict):
        flagged.append({**r, "_why": "parse_fail"});
        ok_intent = ok_act = False
    else:
        pred = p.get("intent")
        if pred in acc: intent_correct[exp] += 1
        else: ok_intent = False; flagged.append({**r, "_why": f"intent {pred}!~{sorted(acc)}"})
        # severity
        sev = p.get("severity")
        if not (isinstance(sev, int) and 1 <= sev <= 5): sev_bad += 1
        # reply
        rep = reply_text(p)
        if not rep or not str(rep).strip(): reply_empty += 1; ok_lang = False
        # language compliance
        if pinned_zh:
            if KANA.search(rep): lang_viol += 1; ok_lang = False; flagged.append({**r, "_why": "kana_in_zh"})
            elif any(ch in SIMP for ch in rep): lang_viol += 1; ok_lang = False; flagged.append({**r, "_why": "simplified_in_zh"})
        else:  # en pinned
            if CJK.search(rep): lang_viol += 1; ok_lang = False; flagged.append({**r, "_why": "cjk_in_en"})
        # actuation
        hd = has_delta(p)
        if r["actuates"]:
            if hd: act_tp += 1
            else: act_fn += 1; ok_act = False; flagged.append({**r, "_why": "missing_delta_on_symptom"})
        else:
            non_act_total += 1
            if hd: false_actuate += 1; ok_act = False; flagged.append({**r, "_why": "FALSE_ACTUATE"})

    if ok_intent: lang_correct[lang] += 0  # placeholder
    if ok_intent and ok_act and ok_lang:
        lang_correct[lang] += 1

print(f"==== {label}  (n={n}) ====")
print(f"DELIVERABILITY: valid JSON {parse_ok}/{n} = {100*parse_ok/n:.1f}%")
ic = sum(intent_correct.values()); it = sum(intent_total.values())
print(f"INTENT ACCURACY (pred in acceptable): {ic}/{it} = {100*ic/it:.1f}%")
for k in intent_total:
    print(f"   {k:16s} {intent_correct[k]}/{intent_total[k]} = {100*intent_correct[k]/intent_total[k]:.0f}%")
print(f"ACTUATION RECALL (symptom->delta): {act_tp}/{act_tp+act_fn} = {100*act_tp/max(act_tp+act_fn,1):.1f}%")
print(f"FALSE-ACTUATE (non-symptom->delta, SAFETY): {false_actuate}/{non_act_total} = {100*false_actuate/max(non_act_total,1):.2f}%")
print(f"language violations: {lang_viol}/{n}   severity-bad: {sev_bad}   empty-reply: {reply_empty}")
print("FULLY-CORRECT by lang (intent+actuation+language):")
for k in lang_total:
    print(f"   {k:6s} {lang_correct[k]}/{lang_total[k]} = {100*lang_correct[k]/lang_total[k]:.0f}%")
if latencies:
    latencies.sort()
    p50 = latencies[len(latencies)//2]; p95 = latencies[int(len(latencies)*0.95)]
    print(f"latency s: p50={p50:.1f} p95={p95:.1f} max={latencies[-1]:.1f} mean={statistics.mean(latencies):.1f}")
# dedup flagged by id+why
seen=set(); fout=[]
for f in flagged:
    k=(f.get("id"),f.get("_why"))
    if k in seen: continue
    seen.add(k); fout.append(f)
outp = path.replace(".jsonl","")+".flagged.jsonl"
with open(outp,"w",encoding="utf-8") as fo:
    for f in fout: fo.write(json.dumps(f,ensure_ascii=False)+"\n")
print(f"flagged {len(fout)} cases -> {outp}")
