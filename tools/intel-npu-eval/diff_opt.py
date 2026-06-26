"""Compare optimized (prefix-cached) vs naive outputs to confirm KV-reuse does
not change response quality, and report optimized latency."""
import json, statistics, sys

def load(p):
    return {json.loads(l)["id"]: json.loads(l) for l in open(p, encoding="utf-8") if l.strip()}

opt = load(sys.argv[1])   # eval_gpu_cbp.jsonl (prefix-caching)
base = load(sys.argv[2])  # eval_gpu.jsonl   (naive)

def hd(p): return any(isinstance((p or {}).get(k), (int, float)) and (p or {}).get(k) != 0 for k in ("fov_delta","mask_depth_delta","grid_visibility_delta"))
def intent(r): return ((r.get("parsed") or {}).get("intent"))
def reply(r): return str(((r.get("parsed") or {}).get("reply", "")))

ids = [i for i in opt if i in base]
both = same_intent = same_act = exact = 0
diffs = []
for i in ids:
    o, b = opt[i], base[i]
    if not o.get("parse_ok") or not b.get("parse_ok"): continue
    both += 1
    si = intent(o) == intent(b); sa = hd(o.get("parsed")) == hd(b.get("parsed")); ex = reply(o) == reply(b)
    same_intent += si; same_act += sa; exact += ex
    if not si or not sa:
        diffs.append((i, intent(b), intent(o)))

print(f"=== OPTIMIZED (prefix-cache) vs NAIVE — quality invariance (n={both}) ===")
print(f"same intent:     {same_intent}/{both} = {100*same_intent/both:.1f}%")
print(f"same actuation:  {same_act}/{both} = {100*same_act/both:.1f}%")
print(f"exact reply text:{exact}/{both} = {100*exact/both:.1f}%")
if diffs[:10]:
    print("intent/actuation diffs (id, naive->opt):", diffs[:10])

lat = [r["latency_s"] for r in opt.values() if isinstance(r.get("latency_s"), (int, float))]
lat.sort()
parse_ok = sum(1 for r in opt.values() if r.get("parse_ok"))
print(f"\n=== OPTIMIZED run stats (n={len(opt)}) ===")
print(f"deliverability: {parse_ok}/{len(opt)} = {100*parse_ok/len(opt):.1f}%")
# exclude the first (cold prefill) to show warm per-case latency
warm = lat[1:]
print(f"latency s (all): p50={lat[len(lat)//2]:.2f} p95={lat[int(len(lat)*0.95)]:.2f} max={lat[-1]:.2f}")
print(f"latency s (warm, excl. coldest): p50={warm[len(warm)//2]:.2f} mean={statistics.mean(warm):.2f} min={warm[0]:.2f}")
