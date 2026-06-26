import sys, time
import openvino_genai as ov

model = r"C:\ovgenai\models\Qwen3-4B-Instruct-2507-ov-int4"
device = sys.argv[1] if len(sys.argv) > 1 else "NPU"
cfg = {"CACHE_DIR": r"C:\ovgenai\ov_cache"}
if device == "NPU":
    cfg.update({"MAX_PROMPT_LEN": 6144, "MIN_RESPONSE_LEN": 512, "GENERATE_HINT": "BEST_PERF"})

t0 = time.time()
pipe = ov.LLMPipeline(model, device, **cfg)
print(f"LOAD_SEC={time.time()-t0:.1f}  device={device}")
tok = pipe.get_tokenizer()

# --- coherence + decode speed (short) ---
msgs = [{"role": "user", "content": "What is the capital of France? Answer in one short sentence."}]
prompt = tok.apply_chat_template(msgs, add_generation_prompt=True)
g1 = ov.GenerationConfig(); g1.max_new_tokens = 1; g1.apply_chat_template = False
t0 = time.time(); pipe.generate(prompt, g1); tpf = time.time() - t0
N = 64
g2 = ov.GenerationConfig(); g2.max_new_tokens = N; g2.apply_chat_template = False
t0 = time.time(); out = pipe.generate(prompt, g2); tt = time.time() - t0
dec = max(tt - tpf, 1e-9)
print(f"SHORT prefill_s={tpf:.2f} total_s={tt:.2f} DECODE_TOK_S={(N-1)/dec:.2f}")
print("COHERENCE_OUT:", repr(str(out)[:300]))

# --- long ~4400-token prompt prefill ---
long = open(r"C:\ovgenai\long_prompt.txt", encoding="utf-8").read()
lp = tok.apply_chat_template([{"role": "user", "content": long}], add_generation_prompt=True)
try:
    ntok = len(tok.encode(lp).input_ids.data[0])
except Exception:
    ntok = -1
g3 = ov.GenerationConfig(); g3.max_new_tokens = 24; g3.apply_chat_template = False
t0 = time.time(); o3 = pipe.generate(lp, g3); tl = time.time() - t0
print(f"LONG prompt_tok={ntok} total_s={tl:.2f}  prefill_tok_s~={ntok/max(tl,1e-9):.0f}")
print("LONG_OUT:", repr(str(o3)[:160]))
print("=== done")
