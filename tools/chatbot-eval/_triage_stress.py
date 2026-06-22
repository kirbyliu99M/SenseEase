# -*- coding: utf-8 -*-
"""Dump the stress-sweep failures (text + model reply + deltas) grouped, for triage."""
import json
import sys

src = sys.argv[1]
rows = [json.loads(l) for l in open(src, encoding="utf-8")]


def show(r, what):
    e = r.get("env", {})
    print(f"[{r['family']}/{r['id']}] {what}")
    print(f"   text : {r['text'][:90]}")
    print(f"   ->     intent={e.get('intent')} fov={e.get('fov')} md={e.get('md')} gv={e.get('gv')} flags={r.get('lang_flags')}")
    print(f"   reply: {(e.get('reply') or '')[:140]}")


print("=== SAFETY-CRITICAL: actuated when should NOT (one line per case) ===")
seen = set()
for r in rows:
    if r.get("expect_actuate") == "none" and r.get("checks", {}).get("actuate_ok") is False and r["id"] not in seen:
        seen.add(r["id"]); show(r, "ACTUATE-FAIL")

print("\n=== LANGUAGE FAILS (one per case) ===")
seen = set()
for r in rows:
    if r.get("checks", {}).get("lang_ok") is False and r["id"] not in seen:
        seen.add(r["id"]); show(r, "LANG-FAIL")

print("\n=== INTENT FAILS (one per case, non-safety) ===")
seen = set()
for r in rows:
    c = r.get("checks", {})
    if c.get("intent_ok") is False and c.get("actuate_ok") is not False and r["id"] not in seen:
        seen.add(r["id"]); show(r, f"want={r['expect_intent']}")
