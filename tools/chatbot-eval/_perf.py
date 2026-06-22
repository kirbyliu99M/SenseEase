# -*- coding: utf-8 -*-
"""Response-time / token analysis of a results jsonl (needs latency_ms + *_tokens). Usage: _perf.py RESULTS"""
import json
import sys
from collections import defaultdict

rows = [json.loads(l) for l in open(sys.argv[1], encoding="utf-8")]
lat = [r["latency_ms"] for r in rows if r.get("latency_ms")]
ct = [r["completion_tokens"] for r in rows if r.get("completion_tokens") is not None]
pt = [r["prompt_tokens"] for r in rows if r.get("prompt_tokens") is not None]


def pct(a, p):
    a = sorted(a)
    return a[min(len(a) - 1, int(len(a) * p / 100))] if a else 0


print(f"calls: {len(rows)}   with-latency: {len(lat)}")
if lat:
    print(f"latency ms       : mean={sum(lat)//len(lat)} p50={pct(lat,50)} p95={pct(lat,95)} max={max(lat)}  (eval server = Vulkan iGPU)")
if pt:
    print(f"prompt_tokens    : mean={sum(pt)//len(pt)} min={min(pt)} max={max(pt)}  (~system prompt + user turn; the PREFILL cost)")
if ct:
    print(f"completion_tokens: mean={sum(ct)//len(ct)} p50={pct(ct,50)} p95={pct(ct,95)} max={max(ct)}  (reply length = the DECODE cost)")

fam = defaultdict(lambda: {"lat": [], "ct": []})
for r in rows:
    if r.get("latency_ms"):
        fam[r["family"]]["lat"].append(r["latency_ms"])
    if r.get("completion_tokens") is not None:
        fam[r["family"]]["ct"].append(r["completion_tokens"])
print("\nper-family        avg_latency_ms   avg_reply_tok")
for f in sorted(fam):
    L, C = fam[f]["lat"], fam[f]["ct"]
    print(f"  {f:28s} {sum(L)//len(L) if L else 0:>6}        {sum(C)//len(C) if C else 0:>4}")

print("\nlongest replies (decode-heaviest):")
for r in sorted([r for r in rows if r.get("completion_tokens")], key=lambda r: -r["completion_tokens"])[:10]:
    print(f"  {r['completion_tokens']:>3}tok [{r['family']}/{r['id']}]  {(r.get('env',{}).get('reply') or '')[:64]}")
