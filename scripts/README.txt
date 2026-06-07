SenseEase v0.11 — Pre-Alpha POC build
======================================

Date: 2026-05-31
Target: Windows 10/11 x64
Build mode: self-contained (no .NET runtime install needed)
Size unpacked: ~2.9 GB (incl. Ministral 3B Q4_K_M model + CUDA runtime)

POC-SCOPE ONLY. This build bundles a community Ministral 3B GGUF for
demonstration. The real product will ship with a licensed model.

------------------------------------------------------------------
SETUP — 4 steps
------------------------------------------------------------------

1. ★ Verify the download (integrity check). Each release publishes a
   companion .sha256 text file next to the installer. Confirm the file
   you downloaded matches the published hash BEFORE running it:

   Open PowerShell in your download folder and run:

       Get-FileHash .\SenseEase-Setup-v0.11.1.exe -Algorithm SHA256

   Compare the printed hash against the contents of the published
   SenseEase-Setup-v0.11.1.exe.sha256 file (case-insensitive). They
   must match exactly. If they differ, the download is corrupt or
   tampered — delete it and download again from the official source.

   NOTE: this Pre-Alpha build is not yet code-signed, so Windows
   SmartScreen / your AV may warn that the publisher is unknown. The
   SHA-256 check above is how you confirm authenticity in the meantime;
   Authenticode signing is planned for a later milestone.

2. Run the installer (SenseEase-Setup-v0.11.1.exe). It is a per-user
   install (no admin prompt) into %LOCALAPPDATA%\Programs\SenseEase.

3. Double-click SenseEase.App.exe to launch. First launch:
     - Cold start ~25-40s while Mistral loads on background thread
     - Main console window appears once model is ready
     - If startup hangs >60s, see TROUBLESHOOTING below

4. (Optional) GPU: this build is self-contained with the CUDA 12.x
   runtime bundled (~700 MB at runtimes/win-x64/native/cuda12/). The
   chatbot will use GPU if you have an NVIDIA card with CUDA 12.x
   drivers; otherwise it falls back to CPU automatically. CPU-only
   first-load is ~30s; GPU first-load is ~10-15s.

------------------------------------------------------------------
PERFORMANCE EXPECTATIONS — NVIDIA GPU vs CPU-only
------------------------------------------------------------------

The chatbot runs on the bundled Ministral 3B model (2 GB GGUF, Q4_K_M
quantization). Performance depends entirely on whether you have an
NVIDIA GPU with CUDA 12.x drivers:

                       NVIDIA GPU         CPU-only (modern)    CPU-only (older)
                       (RTX-class)        (Intel/AMD AVX2+)    (no AVX2)
                       ----------------   ------------------   -----------------
  Cold-start load      ~10s               60-120s              2-3 minutes
  Per-message reply    <2s                5-30s                30-90s
  Tokens/sec           60-100+            3-10                 1-3

★ IMPORTANT for CPU-only testers:

  - The first time you open the chatbot, the 2 GB model loads into RAM.
    This takes 60-120 seconds on a modern CPU and may APPEAR HUNG.
    Watch the chatbot status text in the main window -- it changes
    from "Loading Ministral 3B..." to "Ministral 3B ready" when done.

  - Per-message replies take noticeably longer on CPU than on GPU.
    A 20-second wait for a reply is normal; the app is not broken.

  - The bundled CUDA runtime DLLs (~250 MB) load into the process
    even on CPU-only machines but never actually run -- harmless RAM
    overhead. We will trim them in a future build once GPU detection
    is wired in.

  - All major Intel + AMD CPU generations from the last 10-15 years
    are covered (Sandy Bridge through Sapphire Rapids; Piledriver
    through Zen 4). On CPU-only machines, llama.cpp auto-selects the
    best-matching ggml-cpu-*.dll for your CPU's SIMD level.

If the chatbot status stays at "Loading Ministral 3B..." for more
than 3 minutes, that is a real load failure -- check
%LOCALAPPDATA%\SenseEase\llama-server.log for the actual error.

------------------------------------------------------------------
WHERE USER DATA LIVES (per-user, per-machine)
------------------------------------------------------------------

%LOCALAPPDATA%\SenseEase\  (typically C:\Users\<you>\AppData\Local\SenseEase\)
  - user-preferences.json        (Settings toggles, atomic write)
  - habituation_profile.json     (VIMS habituation state)
  - chat_history.jsonl           (chatbot transcript)
  - model_trace.jsonl            (raw model I/O per turn -- dev audit trail)
  - llama-server.log             (Mistral server stdout/stderr)
  - crash.log                    (unhandled exceptions, append-only)
  - first-run-complete.flag      (empty marker)

%TEMP%\
  - wgc_diag.txt                 (diagnostic sink, append-only with
                                  [N=xxxxxxx] monotonic sequence prefix)
  - wgc_diag.lost.log            (orthogonal loss sink -- absence is
                                  the steady state; presence means
                                  primary log had silent loss)
  - wgc_ofi_trace.txt            (env-gated OFI per-tick trace; only
                                  written when SENSEEASE_WGC_OFI_TRACE=1)

To FULLY reset to a fresh install state:
    Remove-Item $env:LOCALAPPDATA\SenseEase -Recurse -Force

(Uninstalling the app -- i.e. deleting the extracted folder -- does
NOT delete %LOCALAPPDATA%\SenseEase\; this is typical Windows
behavior and is intentional so preferences persist across re-installs.)

------------------------------------------------------------------
KNOWN ISSUES (Pre-Alpha)
------------------------------------------------------------------

  1. Two dev/QA hotkeys are non-functional:
       - Ctrl+Shift+H (intended: 20s mock head-pose sweep)
       - Double-tap Ctrl within 400ms (intended: overlay escape hatch)
     Root cause diagnosed; deferred to Beta. The Settings tab's dev
     hotkey reference table is HIDDEN to avoid misleading testers.

  2. No auto-update mechanism. Updates = re-download zip + replace
     folder. (Beta will move to Velopack delta updates.)

  3. Uninstall doesn't clean %LOCALAPPDATA%\SenseEase\. Manual cleanup
     via the PowerShell command above.

  4. crash.log has no rotation; old content from prior versions can
     accumulate.

------------------------------------------------------------------
TROUBLESHOOTING
------------------------------------------------------------------

If the app fails to start, check (in this order):

  1. %LOCALAPPDATA%\SenseEase\crash.log -- top-level handlers log
     unhandled exceptions here. Most-recent entry at bottom.

  2. %LOCALAPPDATA%\SenseEase\llama-server.log -- Mistral subprocess
     stdout/stderr. If it says "model not found" or "load failed",
     verify the model file is at:
         <install>\models\Ministral-3-3B-Instruct-2512-GGUF\Ministral-3-3B-Instruct-2512-Q4_K_M.gguf

  3. %TEMP%\wgc_diag.txt -- primary diagnostic sink for tray / sensing
     / sidecar startup. Tail the last ~50 lines.

  4. Windows Event Viewer -> Windows Logs -> Application -> look for
     ".NET Runtime" or "Application Error" entries.

If the chatbot replies with a lexicon fallback ("(no GGUF found at:
...)" status), the model file isn't being found at the expected path
-- verify step (2) above.

------------------------------------------------------------------
REPORT BACK
------------------------------------------------------------------

For Pre-Alpha cross-machine smoke testing, please report:

  - Setup step where any prompt / error appeared (did the SHA-256 hash
    match? did SmartScreen / AV warn about an unknown publisher? GPU
    detected?)
  - Cold start time from double-click to MainWindow visible
  - Whether the chatbot loaded (status text in main window)
  - Any entries in crash.log after first ~5 minutes of use
  - The full %TEMP%\wgc_diag.txt if anything looks off
