// ovgenai_host.cpp — OpenAI-compatible HTTP host backed by OpenVINO GenAI LLMPipeline.
//
// Production C++ sidecar for SenseEase's Intel OpenVINO NPU/iGPU LLM backend. This is a
// 1:1 behavioural port of the validated reference `genai_host.py` (in the Intel deliverable
// _NPU_DELIVERABLES/scripts/), which passed the 622-case NLU eval. It speaks the same OpenAI
// contract the app's external-server HTTP client already uses, on 127.0.0.1:<port>.
//
// NPU has no batching -> requests are serialized. Greedy decoding on NPU. On a persistent
// NPU DEVICE_LOST (proven unrecoverable by a same-device reload), it falls back to the iGPU.
//
// Env (identical to genai_host.py):
//   OVHOST_MODEL   : path to the OpenVINO IR model dir (required)
//   OVHOST_DEVICE  : NPU (default) | GPU | CPU
//   OVHOST_PORT    : default 8181
//   OVHOST_MODELID : model id reported in responses (default 'qwen3-4b')
//   OVHOST_CACHE   : CACHE_DIR (default C:\ovgenai\ov_cache)
//   OVHOST_LOG     : log file (default %LOCALAPPDATA%\SenseEase\llama-server-genai.log)

#include <openvino/genai/llm_pipeline.hpp>

#include "httplib.h"
#include "json.hpp"

#include <atomic>
#include <chrono>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <memory>
#include <mutex>
#include <set>
#include <string>
#include <thread>

using json = nlohmann::json;

// ----------------------------------------------------------------------------- config
static std::string env_or(const char* key, const std::string& fallback) {
    const char* v = std::getenv(key);
    return (v && *v) ? std::string(v) : fallback;
}

static std::string default_log_path() {
    const char* lad = std::getenv("LOCALAPPDATA");
    std::filesystem::path base = (lad && *lad) ? std::filesystem::path(lad) : std::filesystem::path(".");
    return (base / "SenseEase" / "llama-server-genai.log").string();
}

static const std::string MODEL   = env_or("OVHOST_MODEL", "");
static std::string       DEVICE  = env_or("OVHOST_DEVICE", "NPU");   // mutated NPU->GPU on device-loss
static const int         PORT    = std::stoi(env_or("OVHOST_PORT", "8181"));
static const std::string MODELID = env_or("OVHOST_MODELID", "qwen3-4b");
static const std::string CACHE   = env_or("OVHOST_CACHE", "C:\\ovgenai\\ov_cache");
static const std::string LOGPATH = env_or("OVHOST_LOG", default_log_path());
// OV 2025.4+ NPU prefix-KV reuse across turns (PR #31669); opt-in. OVHOST_PREFIX_CACHE=NO disables it for the A/B.
static const std::string PREFIX_CACHE = env_or("OVHOST_PREFIX_CACHE", "YES");

// ----------------------------------------------------------------------------- state
static std::atomic<bool> g_ready{false};
static std::atomic<bool> g_compiling{true};
static std::string       g_error;                 // set once on fatal load failure
static std::mutex        g_gen_mutex;             // NPU has no batching: one generate at a time
static std::unique_ptr<ov::genai::LLMPipeline> g_pipe;
static ov::genai::Tokenizer g_tok;

// ----------------------------------------------------------------------------- logging
static void log_line(const std::string& msg) {
    auto now = std::chrono::system_clock::to_time_t(std::chrono::system_clock::now());
    std::tm tm{};
#if defined(_WIN32)
    localtime_s(&tm, &now);
#else
    localtime_r(&now, &tm);
#endif
    char ts[16];
    std::strftime(ts, sizeof(ts), "%H:%M:%S", &tm);
    std::string line = std::string(ts) + " " + msg;
    std::cout << line << std::endl;
    try {
        std::filesystem::create_directories(std::filesystem::path(LOGPATH).parent_path());
        std::ofstream f(LOGPATH, std::ios::app);
        f << line << "\n";
    } catch (...) { /* logging must never throw */ }
}

// ----------------------------------------------------------------------------- prompt
// Apply the model's chat template; fall back to ChatML — mirrors genai_host.build_prompt().
static std::string build_prompt(const json& messages) {
    try {
        ov::genai::ChatHistory history;
        for (const auto& m : messages) {
            std::string role = m.value("role", std::string("user"));
            std::string content = m.value("content", std::string(""));
            history.push_back({{"role", role}, {"content", content}});
        }
        return g_tok.apply_chat_template(history, /*add_generation_prompt=*/true);
    } catch (...) {
        std::string out;
        for (const auto& m : messages) {
            out += "<|im_start|>" + m.value("role", std::string("user")) + "\n" +
                   m.value("content", std::string("")) + "<|im_end|>\n";
        }
        out += "<|im_start|>assistant\n";
        return out;
    }
}

// ----------------------------------------------------------------------------- pipeline load
static void load_pipeline() {
    ov::AnyMap cfg;
    cfg["CACHE_DIR"] = CACHE;
    if (DEVICE == "NPU") {
        // Validated NPU props (genai_host.py). §D also lists PREFILL_HINT="DYNAMIC" as optional;
        // the validated host omitted it, so we mirror the validated set exactly.
        cfg["MAX_PROMPT_LEN"]   = 6144;
        cfg["MIN_RESPONSE_LEN"] = 512;
        cfg["GENERATE_HINT"]    = std::string("BEST_PERF");
        // OV 2025.4+ NPU prefix caching (PR #31669): reuse the identical system-prompt prefix KV across turns
        // -> cut TTFT (our 2026.2.1 is downstream; plugin default is OFF). Opt-in; OVHOST_PREFIX_CACHE=NO
        // disables it for the A/B. A rejected property throws at NPU load -> the load-time NPU->iGPU fallback
        // catches it, so this can't wedge the host.
        if (PREFIX_CACHE == "YES" || PREFIX_CACHE == "yes" || PREFIX_CACHE == "1")
            cfg["NPUW_LLM_ENABLE_PREFIX_CACHING"] = std::string("YES");
    }
    log_line("loading model=" + MODEL + " device=" + DEVICE + " cache=" + CACHE);
    auto t0 = std::chrono::steady_clock::now();
    g_pipe = std::make_unique<ov::genai::LLMPipeline>(std::filesystem::path(MODEL), DEVICE, cfg);
    auto secs = std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();
    log_line("model loaded + compiled in " + std::to_string(secs) + "s");
    g_tok = g_pipe->get_tokenizer();
    // Warm up: build the first KV so the first real request is fast (host self-warms before /health=ready).
    try {
        ov::genai::GenerationConfig g;
        g.max_new_tokens = 1;
        auto w0 = std::chrono::steady_clock::now();
        g_pipe->generate("Hello", g);
        auto wsecs = std::chrono::duration<double>(std::chrono::steady_clock::now() - w0).count();
        log_line("warmup generate in " + std::to_string(wsecs) + "s");
    } catch (const std::exception& e) {
        log_line(std::string("warmup failed (non-fatal): ") + e.what());
    }
    g_compiling = false;
    g_ready = true;
    log_line("server READY on http://127.0.0.1:" + std::to_string(PORT) + " device=" + DEVICE);
}

// ----------------------------------------------------------------------------- generate
struct GenResult { std::string text; long input_tokens; };

static GenResult generate(const json& body) {
    const json messages = body.contains("messages") ? body["messages"] : json::array();
    std::string prompt = build_prompt(messages);

    ov::genai::GenerationConfig g;
    long max_tokens = 0;
    if (body.contains("max_tokens") && body["max_tokens"].is_number_integer())
        max_tokens = body["max_tokens"].get<long>();
    g.max_new_tokens = max_tokens > 0 ? static_cast<size_t>(max_tokens) : 256;
    g.apply_chat_template = false;  // already applied in build_prompt()

    // NPU is greedy-only; only enable sampling off-NPU (mirrors the Python host).
    if (DEVICE != "NPU" && body.contains("temperature") && body["temperature"].is_number()) {
        double temp = body["temperature"].get<double>();
        if (temp > 0.0) { g.do_sample = true; g.temperature = static_cast<float>(temp); }
    }
    if (body.contains("stop") && !body["stop"].is_null()) {
        std::set<std::string> stops;
        if (body["stop"].is_string()) stops.insert(body["stop"].get<std::string>());
        else if (body["stop"].is_array())
            for (const auto& s : body["stop"]) if (s.is_string()) stops.insert(s.get<std::string>());
        if (!stops.empty()) { g.stop_strings = stops; g.include_stop_str_in_output = false; }
    }

    std::lock_guard<std::mutex> lock(g_gen_mutex);
    auto t0 = std::chrono::steady_clock::now();
    ov::genai::DecodedResults res;
    try {
        res = g_pipe->generate(prompt, g);
    } catch (const std::exception& e) {
        // A persistent NPU ZE_RESULT_ERROR_DEVICE_LOST cannot be cleared by a same-device reload
        // (the NPU pipeline load then hangs; only a device/driver reset recovers). So on device-loss
        // FALL BACK to the iGPU (healthy, same IR, equivalent quality) rather than reloading the NPU.
        std::string es = e.what();
        if (es.find("DEVICE_LOST") != std::string::npos ||
            es.find("device hung") != std::string::npos ||
            es.find("infer_request") != std::string::npos) {
            if (DEVICE == "NPU") {
                log_line("NPU device-lost; falling back to iGPU (NPU needs a reset to recover)...");
                DEVICE = "GPU";
            } else {
                log_line(std::string("device-lost on ") + DEVICE + "; reloading...");
            }
            g_ready = false;
            load_pipeline();
            res = g_pipe->generate(prompt, g);
        } else {
            throw;
        }
    }
    double dt = std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();

    std::string text = res.texts.empty() ? std::string() : res.texts.front();
    long ntok = -1;
    try {
        ntok = static_cast<long>(res.perf_metrics.get_num_input_tokens());
        log_line("gen done in " + std::to_string(dt) + "s | PREFILL prompt_tok=" + std::to_string(ntok));
    } catch (...) {
        log_line("gen done in " + std::to_string(dt) + "s");
    }
    return {text, ntok};
}

// ----------------------------------------------------------------------------- HTTP
int main() {
    if (MODEL.empty()) {
        std::cerr << "OVHOST_MODEL not set" << std::endl;
        return 2;
    }

    // Background load so the socket is up immediately and /health reports compiling.
    std::thread([] {
        try {
            load_pipeline();
        } catch (const std::exception& e) {
            // Decision #1 (auto-probe NPU -> iGPU): a NPU compile/load failure is not necessarily fatal — the
            // iGPU runs the SAME IR at equivalent quality. Retry the load ONCE on the iGPU before reporting
            // error, mirroring the generate-path device-loss fallback. Only NPU falls back; a GPU/CPU load
            // failure is terminal (the app's selector then drops to the Vulkan/CPU server). g_compiling stays
            // true across the retry, so /health reports "compiling" (still working), not "error".
            log_line(std::string("LOAD FAILED on ") + DEVICE + ": " + e.what());
            if (DEVICE == "NPU") {
                log_line("auto-probe: NPU load failed; retrying on iGPU...");
                DEVICE = "GPU";
                try {
                    load_pipeline();
                } catch (const std::exception& e2) {
                    g_error = std::string(typeid(e2).name()) + ": " + e2.what();
                    g_compiling = false;
                    log_line(std::string("LOAD FAILED on iGPU too: ") + e2.what());
                }
            } else {
                g_error = std::string(typeid(e).name()) + ": " + e.what();
                g_compiling = false;
            }
        }
    }).detach();

    httplib::Server svr;

    svr.Get("/health", [](const httplib::Request&, httplib::Response& res) {
        bool ready = g_ready.load();
        std::string status = ready ? "ready" : (g_compiling.load() ? "compiling" : "error");
        json j = {{"status", status}, {"device", DEVICE}};
        j["error"] = g_error.empty() ? json(nullptr) : json(g_error);
        res.status = ready ? 200 : 503;
        res.set_content(j.dump(), "application/json");
    });

    svr.Get("/v1/models", [](const httplib::Request&, httplib::Response& res) {
        json j = {{"object", "list"},
                  {"data", json::array({{{"id", MODELID}, {"object", "model"}, {"owned_by", "openvino"}}})}};
        res.set_content(j.dump(), "application/json");
    });

    svr.Post("/v1/chat/completions", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try {
            body = json::parse(req.body.empty() ? "{}" : req.body);
        } catch (const std::exception& e) {
            res.status = 400;
            res.set_content(json({{"error", std::string("bad json: ") + e.what()}}).dump(), "application/json");
            return;
        }
        // Intel field bug (2026-06-25): the .NET HttpClient posts with Transfer-Encoding: chunked (no
        // Content-Length). cpp-httplib de-chunks that into req.body natively, so unlike the Python bare
        // http.server (which read only Content-Length → empty body → ~28 s of off-prompt text the app then
        // rendered as a lexicon card) this host reads it correctly. This guard is belt-and-braces: an
        // empty/missing-messages body must FAIL LOUDLY (400), never generate from nothing.
        if (!body.contains("messages") || !body["messages"].is_array() || body["messages"].empty()) {
            res.status = 400;
            res.set_content(R"({"error":"no messages in request body"})", "application/json");
            return;
        }
        if (!g_ready.load()) {
            res.status = 503;
            res.set_content(R"({"error":"model not ready"})", "application/json");
            return;
        }
        try {
            GenResult r = generate(body);
            long ntok = r.input_tokens < 0 ? 0 : r.input_tokens;
            json out = {
                {"id", "chatcmpl-ovgenai"},
                {"object", "chat.completion"},
                {"created", 0},
                {"model", body.value("model", MODELID)},
                {"choices", json::array({{{"index", 0},
                                          {"message", {{"role", "assistant"}, {"content", r.text}}},
                                          {"finish_reason", "stop"}}})},
                {"usage", {{"prompt_tokens", ntok}, {"completion_tokens", 0}, {"total_tokens", ntok}}},
            };
            res.set_content(out.dump(), "application/json");
        } catch (const std::exception& e) {
            log_line(std::string("ERROR: ") + e.what());
            res.status = 500;
            res.set_content(json({{"error", std::string(typeid(e).name()) + ": " + e.what()}}).dump(),
                            "application/json");
        }
    });

    log_line("listening on 127.0.0.1:" + std::to_string(PORT) + " (loading model in background)");
    if (!svr.listen("127.0.0.1", PORT)) {
        log_line("FATAL: could not bind 127.0.0.1:" + std::to_string(PORT));
        return 1;
    }
    return 0;
}
