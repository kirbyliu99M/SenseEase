using System.Threading.Tasks;
using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Ultrabook plan #5 — the external-backend seam: SenseEase connects to an already-running OpenAI server
/// (e.g. FastFlowLM on the AMD NPU) instead of launching the bundled Vulkan server. Full success needs a
/// live server (validated in-app); this pins the GRACEFUL-FAILURE contract deterministically — an
/// unreachable external server must leave the chatbot unloaded (→ deterministic lexicon fallback) and
/// never throw, exactly like a local launch failure.
/// </summary>
public class LocalLlmChatbotServiceExternalBackendTests
{
    [Fact]
    public async Task ExternalMode_UnreachableServer_StaysUnloaded_NoThrow()
    {
        // External mode is selected purely by the base URL — the local GGUF need not exist.
        using var svc = new LocalLlmChatbotService(
            modelPath: "C:/nonexistent/senseease-test-model.gguf",
            externalBaseUrl: "http://127.0.0.1:1",     // nothing listens on port 1
            externalModelName: "qwen3:4b");

        await svc.LoadAsync();                          // connect attempt — must not throw

        Assert.False(svc.IsModelLoaded);                // unreachable → not loaded
        Assert.NotNull(svc.LoadError);                  // and a diagnostic is recorded
    }
}
