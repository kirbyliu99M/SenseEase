# -*- coding: utf-8 -*-
"""Pull cases out of a generation-workflow output file -> a cases JSON. Usage: _aggregate.py SRC DST"""
import json
import sys
from collections import Counter

SRC, DST = sys.argv[1], sys.argv[2]
data = json.load(open(SRC, encoding="utf-8"))
root = data.get("result", data)
cases = root["cases"]

REQ = {"id", "family", "lang", "state", "text", "intent", "actuate", "focus"}
STATES = {"calm", "driver", "discomfort", "high"}
seen, out = set(), []
for c in cases:
    if not REQ.issubset(c):
        continue
    if c["id"] in seen:
        c["id"] += "_d"
    if c["state"] not in STATES:
        c["state"] = "calm"
    if c["lang"] not in ("en", "zh"):
        c["lang"] = "zh"
    if not isinstance(c.get("intent"), list) or not c["intent"]:
        c["intent"] = ["Unknown"]
    if c["actuate"] not in ("none", "tighten", "grid_down", "any"):
        c["actuate"] = "none"
    c.setdefault("history", None)
    seen.add(c["id"])
    out.append(c)

json.dump(out, open(DST, "w", encoding="utf-8"), ensure_ascii=False, indent=0)
print(f"{len(out)} cases -> {DST}")
for f, n in sorted(Counter(c["family"] for c in out).items()):
    print(f"  {f:26s} {n}")
print("langs:", dict(Counter(c["lang"] for c in out)))
