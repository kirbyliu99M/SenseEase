"""OpenAI-compatible HTTP host backed by OpenVINO GenAI LLMPipeline.

Serves /v1/chat/completions, /v1/models, /health on 127.0.0.1:<port>.
Designed as a drop-in for the SenseEase app's local llama-server contract so the
app can be pointed at it via SENSEEASE_LLM_BASEURL. NPU has no batching -> requests
are serialized. Greedy decoding on NPU.

Env:
  OVHOST_MODEL  : path to the OpenVINO IR model dir (required)
  OVHOST_DEVICE : NPU (default) | GPU | CPU
  OVHOST_PORT   : default 8181
  OVHOST_MODELID: model id reported in responses (default 'qwen3-4b')
  OVHOST_CACHE  : CACHE_DIR (default C:\\ovgenai\\ov_cache)
  OVHOST_LOG    : log file (default %LOCALAPPDATA%\\SenseEase\\llama-server-genai.log)
"""
import json, os, sys, time, threading, traceback
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import openvino_genai as ov_genai

MODEL   = os.environ.get("OVHOST_MODEL")
DEVICE  = os.environ.get("OVHOST_DEVICE", "NPU")
PORT    = int(os.environ.get("OVHOST_PORT", "8181"))
MODELID = os.environ.get("OVHOST_MODELID", "qwen3-4b")
CACHE   = os.environ.get("OVHOST_CACHE", r"C:\ovgenai\ov_cache")
PREFIX_CACHE = os.environ.get("OVHOST_PREFIX_CACHE", "YES")  # OV 2025.4+ NPU prefix-KV reuse; =NO to A/B-disable
LOGPATH = os.environ.get("OVHOST_LOG",
            os.path.join(os.environ.get("LOCALAPPDATA", "."), "SenseEase", "llama-server-genai.log"))

STATE = {"ready": False, "compiling": True, "error": None}
LOCK = threading.Lock()
pipe = None
tok = None

def log(msg):
    line = f"{time.strftime('%H:%M:%S')} {msg}"
    print(line, flush=True)
    try:
        os.makedirs(os.path.dirname(LOGPATH), exist_ok=True)
        with open(LOGPATH, "a", encoding="utf-8") as f:
            f.write(line + "\n")
    except Exception:
        pass

def build_prompt(messages):
    """Apply the model's chat template; fall back to ChatML."""
    try:
        return tok.apply_chat_template(messages, add_generation_prompt=True)
    except Exception:
        parts = []
        for m in messages:
            parts.append(f"<|im_start|>{m.get('role','user')}\n{m.get('content','')}<|im_end|>\n")
        parts.append("<|im_start|>assistant\n")
        return "".join(parts)

def load_pipeline():
    global pipe, tok
    cfg = {"CACHE_DIR": CACHE}
    if DEVICE == "NPU":
        cfg.update({"MAX_PROMPT_LEN": 6144, "MIN_RESPONSE_LEN": 512, "GENERATE_HINT": "BEST_PERF"})
        # OV 2025.4+ NPU prefix caching (PR #31669): reuse the identical system-prompt prefix KV across turns
        # -> cut TTFT (2026.2.1 is downstream; plugin default OFF). OVHOST_PREFIX_CACHE=NO disables for the A/B.
        if PREFIX_CACHE.upper() in ("YES", "1", "TRUE"):
            cfg["NPUW_LLM_ENABLE_PREFIX_CACHING"] = "YES"
    log(f"loading model={MODEL} device={DEVICE} cfg={cfg}")
    t0 = time.time()
    p = ov_genai.LLMPipeline(MODEL, DEVICE, **cfg)
    log(f"model loaded + compiled in {time.time()-t0:.1f}s")
    pipe = p
    tok = p.get_tokenizer()
    # warm up: encode a representative prompt so first real request is fast
    try:
        g = ov_genai.GenerationConfig(); g.max_new_tokens = 1
        t0 = time.time()
        pipe.generate("Hello", g)
        log(f"warmup generate in {time.time()-t0:.1f}s")
    except Exception as e:
        log(f"warmup failed (non-fatal): {e}")
    STATE["compiling"] = False
    STATE["ready"] = True
    log(f"server READY on http://127.0.0.1:{PORT} device={DEVICE}")

def generate(messages, max_tokens, temperature, stop):
    global DEVICE
    prompt = build_prompt(messages)
    g = ov_genai.GenerationConfig()
    g.max_new_tokens = int(max_tokens) if max_tokens else 256
    g.apply_chat_template = False  # we already applied it in build_prompt()
    # NPU is greedy-only; only set sampling on non-NPU
    if DEVICE != "NPU" and temperature and float(temperature) > 0:
        g.do_sample = True
        g.temperature = float(temperature)
    if stop:
        try:
            g.stop_strings = set(stop if isinstance(stop, list) else [stop])
            g.include_stop_str_in_output = False
        except Exception:
            pass
    with LOCK:
        t0 = time.time()
        try:
            res = pipe.generate(prompt, g)
        except Exception as e:
            # The NPU can enter a persistent ZE_RESULT_ERROR_DEVICE_LOST under sustained
            # load that a same-device reload CANNOT clear (the NPU pipeline load then hangs;
            # only a device/driver reset recovers it). So on device-loss, FALL BACK to the
            # iGPU (healthy, same IR, equivalent quality) rather than reloading the NPU.
            if "DEVICE_LOST" in str(e) or "device hung" in str(e) or "infer_request" in str(e):
                if DEVICE == "NPU":
                    log(f"NPU device-lost; falling back to iGPU (NPU needs a reset to recover)...")
                    DEVICE = "GPU"
                else:
                    log(f"device-lost on {DEVICE}; reloading...")
                STATE["ready"] = False
                load_pipeline()
                res = pipe.generate(prompt, g)
            else:
                raise
        dt = time.time() - t0
    text = str(res)
    ntok = None
    try:
        m = res.perf_metrics
        ntok = m.get_num_input_tokens()
        log(f"gen done in {dt:.2f}s | PREFILL prompt_tok={ntok} ttft_ms={m.get_ttft().mean:.0f} "
            f"tpot_ms={m.get_tpot().mean:.0f} gen_tok_s={1000.0/max(m.get_tpot().mean,1e-9):.2f}")
    except Exception:
        log(f"gen done in {dt:.2f}s")
    return text, ntok

class H(BaseHTTPRequestHandler):
    def _send(self, code, obj):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *a):
        pass

    def do_GET(self):
        if self.path.startswith("/health"):
            code = 200 if STATE["ready"] else 503
            self._send(code, {"status": "ready" if STATE["ready"] else ("compiling" if STATE["compiling"] else "error"),
                              "device": DEVICE, "error": STATE["error"]})
        elif self.path.startswith("/v1/models"):
            self._send(200, {"object": "list", "data": [{"id": MODELID, "object": "model", "owned_by": "openvino"}]})
        else:
            self._send(404, {"error": "not found"})

    def do_POST(self):
        if not self.path.startswith("/v1/chat/completions"):
            self._send(404, {"error": "not found"}); return
        try:
            # The .NET HttpClient posts with Transfer-Encoding: chunked (no Content-Length); the bare
            # http.server only reads Content-Length, so de-chunk the body manually (Intel field bug 2026-06-25:
            # empty body -> off-prompt generation -> the app fell back to a lexicon "which-symptom" card).
            te = (self.headers.get("Transfer-Encoding") or "").lower()
            if "chunked" in te:
                raw = b""
                while True:
                    szline = self.rfile.readline().strip()
                    if szline == b"":
                        continue
                    size = int(szline.split(b";")[0], 16)
                    if size == 0:
                        self.rfile.readline()  # trailing CRLF
                        break
                    raw += self.rfile.read(size)
                    self.rfile.readline()      # CRLF after each chunk
            else:
                n = int(self.headers.get("Content-Length", "0"))
                raw = self.rfile.read(n)
            raw = raw or b"{}"
            req = json.loads(raw)
        except Exception as e:
            self._send(400, {"error": f"bad json: {e}"}); return
        # capture the full incoming request envelope (for eval fidelity)
        try:
            with open(r"C:\ovgenai\last_request.json", "wb") as f:
                f.write(raw)
        except Exception:
            pass
        if not STATE["ready"]:
            self._send(503, {"error": "model not ready"}); return
        try:
            text, ntok = generate(req.get("messages", []), req.get("max_tokens"),
                                  req.get("temperature"), req.get("stop"))
            self._send(200, {
                "id": "chatcmpl-ovgenai", "object": "chat.completion",
                "created": 0, "model": req.get("model", MODELID),
                "choices": [{"index": 0, "message": {"role": "assistant", "content": text},
                             "finish_reason": "stop"}],
                "usage": {"prompt_tokens": ntok or 0, "completion_tokens": 0, "total_tokens": ntok or 0},
            })
        except Exception as e:
            log(f"ERROR: {traceback.format_exc()}")
            self._send(500, {"error": f"{type(e).__name__}: {e}"})

def main():
    if not MODEL:
        print("OVHOST_MODEL not set"); sys.exit(2)
    threading.Thread(target=lambda: _safe_load(), daemon=True).start()
    srv = ThreadingHTTPServer(("127.0.0.1", PORT), H)
    log(f"listening on 127.0.0.1:{PORT} (loading model in background)")
    srv.serve_forever()

def _safe_load():
    global DEVICE
    try:
        load_pipeline()
    except Exception as e:
        # Decision #1 (auto-probe NPU -> iGPU): a NPU compile/load failure is not necessarily fatal — the
        # iGPU runs the SAME IR at equivalent quality. Retry the load ONCE on the iGPU before reporting error,
        # mirroring the generate-path device-loss fallback. Only NPU falls back; a GPU/CPU load failure is
        # terminal. STATE["compiling"] stays True across the retry so /health reports "compiling", not "error".
        log(f"LOAD FAILED on {DEVICE}: {traceback.format_exc()}")
        if DEVICE == "NPU":
            log("auto-probe: NPU load failed; retrying on iGPU...")
            DEVICE = "GPU"
            try:
                load_pipeline()
            except Exception as e2:
                STATE["error"] = f"{type(e2).__name__}: {e2}"
                STATE["compiling"] = False
                log(f"LOAD FAILED on iGPU too: {traceback.format_exc()}")
        else:
            STATE["error"] = f"{type(e).__name__}: {e}"
            STATE["compiling"] = False

if __name__ == "__main__":
    main()
