SenseEase v0.11.3 — Pre-Alpha Test Build
========================================

Artifact: SenseEase_Setup-Alpha_Test.0609.exe
Date:     2026-06-09
Target:   Windows 10/11 x64 (build 19041 or later)
Build:    Self-contained (no .NET runtime install required)
Size:     ~2.9 GB installed (includes Ministral 3B Q4_K_M model + Vulkan backend)

POC-SCOPE ONLY. This build bundles a community Ministral 3B GGUF for
demonstration. The real product will ship with a licensed model.

------------------------------------------------------------------
SETUP — 4 steps
------------------------------------------------------------------

1. ★ Verify the download (integrity check). Each release publishes a
   companion .sha256 text file next to the installer. Confirm the file
   you downloaded matches the published hash BEFORE running it:

   Open PowerShell in your download folder and run:

       Get-FileHash .\SenseEase_Setup-Alpha_Test.0609.exe -Algorithm SHA256

   Compare the printed hash against the contents of the published
   SenseEase_Setup-Alpha_Test.0609.exe.sha256 file (case-insensitive).
   They must match exactly. If they differ, the download is corrupt or
   tampered — delete it and download again from the official source.

   (For future builds: substitute the artifact name shown at the top of
   this file for the filename in the command above.)

   NOTE: this Pre-Alpha build is not yet code-signed, so Windows
   SmartScreen / your AV may warn that the publisher is unknown. The
   SHA-256 check above is how you confirm authenticity in the meantime;
   Authenticode signing is planned for a later milestone.

2. Run the installer (SenseEase_Setup-Alpha_Test.0609.exe). It is a
   per-user install (no admin prompt) into
   %LOCALAPPDATA%\Programs\SenseEase.

3. Double-click SenseEase.App.exe to launch. First launch:
     - Cold start ~25-40 s while the Ministral model loads on a
       background thread
     - Main window appears once the model is ready
     - If startup hangs >60 s, see TROUBLESHOOTING below

4. GPU: this build uses the Vulkan backend for inference (llama.cpp
   Vulkan). It works on NVIDIA, AMD, and Intel GPU hardware — no CUDA
   driver required. If no supported GPU is detected, inference falls
   back to CPU automatically. Vulkan drivers ship with current NVIDIA,
   AMD, and Intel graphics drivers; no separate install is normally
   needed.

------------------------------------------------------------------
UPDATING TO A NEWER BUILD
------------------------------------------------------------------

Run the new installer over the existing installation. The installer is
an in-place upgrade; it replaces the application files and preserves
your data in %LOCALAPPDATA%\SenseEase\.

★ Do NOT manually copy or replace files inside the install folder.
  The app verifies the integrity of its critical binaries and model
  weights at startup. Manually replacing files will trip the integrity
  gate and prevent the app from launching. If this happens, reinstall
  using the official installer.

------------------------------------------------------------------
PERFORMANCE EXPECTATIONS — GPU vs CPU-only
------------------------------------------------------------------

The chatbot runs on the bundled Ministral 3B model (2 GB GGUF, Q4_K_M
quantization). Performance depends on whether a Vulkan-capable GPU is
available:

                       Vulkan GPU              CPU-only (modern)    CPU-only (older)
                       (NVIDIA/AMD/Intel)      (Intel/AMD AVX2+)    (no AVX2)
                       ------------------      ------------------   -----------------
  Cold-start load      ~10-20 s                60-120 s             2-3 minutes
  Per-message reply    <2 s                    5-30 s               30-90 s
  Tokens/sec           40-100+                 3-10                 1-3

★ IMPORTANT for CPU-only testers:

  - The first time you open the chatbot, the 2 GB model loads into RAM.
    This takes 60-120 seconds on a modern CPU and may APPEAR HUNG.
    Watch the chatbot status text — it changes from "Loading Ministral
    3B..." to "Ministral 3B ready" when done.

  - Per-message replies take noticeably longer on CPU than on GPU.
    A 20-second wait for a reply is normal; the app is not broken.

  - All major Intel + AMD CPU generations from the last 10-15 years
    are covered. llama.cpp auto-selects the best SIMD path for your CPU.

If the chatbot status stays at "Loading Ministral 3B..." for more
than 3 minutes, that is a real load failure — check
%LOCALAPPDATA%\SenseEase\llama-server.log for the actual error.

------------------------------------------------------------------
WHERE USER DATA LIVES (per-user, per-machine)
------------------------------------------------------------------

%LOCALAPPDATA%\SenseEase\  (typically C:\Users\<you>\AppData\Local\SenseEase\)
  - user-preferences.json        (Settings toggles, atomic write)
  - habituation_profile.json     (VIMS habituation state)
  - chat_history.jsonl           (chatbot transcript)
  - model_trace.jsonl            (raw model I/O per turn — dev audit trail)
  - llama-server.log             (Ministral server stdout/stderr)
  - crash.log                    (unhandled exceptions, append-only)
  - first-run-complete.flag      (empty marker)

%LOCALAPPDATA%\SenseEase\diag\
  - wgc_diag.txt                 (diagnostic sink, append-only with
                                  [N=xxxxxxx] monotonic sequence prefix)
  - wgc_diag.lost.log            (orthogonal loss sink — absence is
                                  the steady state; presence means
                                  primary log had silent loss)
  - wgc_ofi_trace.txt            (env-gated OFI per-tick trace; only
                                  written when SENSEEASE_WGC_OFI_TRACE=1)

To FULLY reset to a fresh install state:
    Remove-Item $env:LOCALAPPDATA\SenseEase -Recurse -Force

------------------------------------------------------------------
UNINSTALLING
------------------------------------------------------------------

Use Add/Remove Programs (Settings > Apps) or the Start-menu
"Uninstall SenseEase" shortcut. During uninstall you will be asked
whether to delete your personal data (settings, chat and calibration
history, user model, and logs). Choosing Yes performs a full clean
removal. Choosing No keeps your data so a future reinstall picks up
where you left off.

------------------------------------------------------------------
KNOWN ISSUES (Pre-Alpha)
------------------------------------------------------------------

  1. Two dev/QA hotkeys are non-functional:
       - Ctrl+Shift+H (intended: 20 s mock head-pose sweep)
       - Double-tap Ctrl within 400 ms (intended: overlay escape hatch)
     Root cause diagnosed; deferred to Beta. The Settings tab's dev
     hotkey reference table is hidden to avoid misleading testers.

  2. No auto-update mechanism. To update: run the new installer
     (see UPDATING above).

  3. crash.log has no rotation; old content from prior versions can
     accumulate.

------------------------------------------------------------------
TROUBLESHOOTING
------------------------------------------------------------------

If the app fails to start, check (in this order):

  1. %LOCALAPPDATA%\SenseEase\crash.log — top-level handlers log
     unhandled exceptions here. Most-recent entry at bottom.

  2. %LOCALAPPDATA%\SenseEase\llama-server.log — Ministral subprocess
     stdout/stderr. If it says "model not found" or "load failed",
     verify the model file is at:
         <install>\models\Ministral-3-3B-Instruct-2512-GGUF\Ministral-3-3B-Instruct-2512-Q4_K_M.gguf

  3. %LOCALAPPDATA%\SenseEase\diag\wgc_diag.txt — primary diagnostic
     sink for tray / sensing / sidecar startup. Tail the last ~50 lines.

  4. Windows Event Viewer -> Windows Logs -> Application -> look for
     ".NET Runtime" or "Application Error" entries.

If the chatbot replies with a lexicon fallback ("(no GGUF found at:
...)" status), the model file isn't being found at the expected path —
verify step (2) above.

------------------------------------------------------------------
REPORT BACK
------------------------------------------------------------------

For Pre-Alpha cross-machine smoke testing, please report:

  - Setup step where any prompt / error appeared (did the SHA-256 hash
    match? did SmartScreen / AV warn about an unknown publisher? GPU
    detected?)
  - Cold start time from double-click to main window visible
  - Whether the chatbot loaded (status text in main window)
  - Any entries in crash.log after first ~5 minutes of use
  - The full %LOCALAPPDATA%\SenseEase\diag\wgc_diag.txt if anything
    looks off
