# -*- coding: utf-8 -*-
"""Post-process results.jsonl: surface concrete failures (ASCII-safe to stdout,
CJK reply text to files) and build the grouped judge payload."""
import json, pathlib, collections
P = pathlib.Path(__file__).resolve().parent
rows = [json.loads(l) for l in (P/"results.jsonl").read_text(encoding="utf-8").splitlines() if l.strip()]

print(f"total rows: {len(rows)}")
print("think_tag rows:", sum(1 for r in rows if r.get("think_tag")))
print("override blocks:", sum(1 for r in rows if r.get("has_override_block")))
print("observation blocks:", sum(1 for r in rows if r.get("has_observation_block")))
print("json invalid:", sum(1 for r in rows if not r.get("checks",{}).get("json_valid")))

print("\n=== INTENT mismatches (expected vs actual) ===")
agg = collections.defaultdict(collections.Counter)
for r in rows:
    c = r.get("checks",{})
    if c.get("intent_ok") is False:
        e = r.get("env",{})
        agg[(r["id"], tuple(r["expect_intent"] or []))][e.get("intent")] += 1
for (id_, exp), ctr in sorted(agg.items()):
    print(f"  {id_:24s} expect={list(exp)} got={dict(ctr)}")

print("\n=== LANGUAGE flags ===")
langfail = [r for r in rows if r.get("checks",{}).get("lang_ok") is False]
for r in langfail:
    print(f"  {r['id']:24s} rep{r['rep']} flags={r.get('lang_flags')}")
# dump their replies to a utf-8 file
with (P/"lang_fail_replies.txt").open("w",encoding="utf-8") as f:
    for r in langfail:
        f.write(f"[{r['id']} rep{r['rep']}] flags={r.get('lang_flags')}\n{r.get('env',{}).get('reply','')}\n\n")

print("\n=== ACTUATE mismatches ===")
for r in rows:
    c = r.get("checks",{})
    if c.get("actuate_ok") is False:
        e = r.get("env",{})
        print(f"  {r['id']:24s} rep{r['rep']} expect={r['expect_actuate']} intent={e.get('intent')} fov={e.get('fov')} md={e.get('md')} gv={e.get('gv')}")

print("\n=== followup payload issues (FollowupRequest w/o valid 2-4 options) ===")
for r in rows:
    c = r.get("checks",{})
    if c.get("followup_payload_ok") is False:
        e = r.get("env",{})
        print(f"  {r['id']:24s} rep{r['rep']} opts={e.get('followup_options')}")

# Build grouped judge payload (trim to what the judge needs)
fams = collections.defaultdict(list)
for r in rows:
    e = r.get("env",{})
    det = []
    c = r.get("checks",{})
    for k in ("intent_ok","actuate_ok","lang_ok","severity_range"):
        if c.get(k) is False: det.append(k)
    if r.get("lang_flags"): det.append("lang:"+",".join(r["lang_flags"]))
    fams[r["family"]].append({
        "ref": f"{r['id']}#{r['rep']}",
        "lang": r["lang"], "text": r["text"], "focus": r["focus"],
        "expect_intent": r["expect_intent"], "expect_actuate": r["expect_actuate"],
        "intent": e.get("intent"), "sev": e.get("severity"),
        "fov": e.get("fov"), "md": e.get("md"), "gv": e.get("gv"),
        "reply": e.get("reply",""),
        "followup_options": e.get("followup_options"),
        "has_override_block": r.get("has_override_block"),
        "det_flags": det,
    })
payload = {"families": [{"family": k, "rows": v} for k,v in sorted(fams.items())]}
(P/"judge_input.json").write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
# per-family files for the judge workflow (each agent reads its own small file)
fdir = P/"judge_fam"; fdir.mkdir(exist_ok=True)
for k,v in fams.items():
    (fdir/f"{k}.json").write_text(json.dumps({"family":k,"rows":v}, ensure_ascii=False), encoding="utf-8")
print(f"\nwrote judge_input.json + judge_fam/*.json: {len(payload['families'])} families, {sum(len(f['rows']) for f in payload['families'])} rows")
print("families:", ",".join(sorted(fams)))
