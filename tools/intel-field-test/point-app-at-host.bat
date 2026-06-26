@echo off
REM ============================================================================
REM Point the portable SenseEase app at the running GenAI host and launch it.
REM
REM This BLOCKS until the host reports /health = ready, so the app never connects
REM mid-compile. (If the app connects while the host is still compiling, it marks
REM itself "loaded" off /v1/models — which is 200 immediately — then every turn
REM silently falls back to the deterministic lexicon with no error. See README §3.)
REM
REM   NOTE: the SenseEase.App.exe in the parent folder was REBUILT 2026-06-24 with
REM   the safety hardening (84def68) + reliability fixes (0f873e1), so this DOES
REM   exercise the hardened NLU prompt on the NPU. (Pre-fix DLLs backed up in
REM   ..\_pre-safetyfix-dll-backup\.) See README §5 for jailbreak prompts to try.
REM ============================================================================
setlocal
set "HERE=%~dp0"
set "SENSEEASE_LLM_BASEURL=http://127.0.0.1:8181"
set "SENSEEASE_LLM_MODEL=qwen3-4b"
set "APP=%HERE%..\SenseEase.App.exe"

echo Waiting for the host to be READY at %SENSEEASE_LLM_BASEURL%/health
echo (a cold NPU compile is ~237s the first time; warm ~5s)...
powershell -NoProfile -Command "$d=(Get-Date).AddMinutes(8); while((Get-Date) -lt $d){ try{ if((Invoke-RestMethod 'http://127.0.0.1:8181/health' -TimeoutSec 4).status -eq 'ready'){ Write-Host '  -> READY'; exit 0 } else { Write-Host '  -> compiling...' } }catch{ Write-Host '  -> host not up yet' }; Start-Sleep 3 }; exit 1"
if errorlevel 1 (
  echo.
  echo [!] Host never became ready. Start it first with run-host.bat and wait for "server READY".
  exit /b 1
)
if not exist "%APP%" (
  echo [!] SenseEase.App.exe not found at "%APP%".
  echo     Point %%APP%% at your portable build, or run manually after the host is ready:
  echo         set SENSEEASE_LLM_BASEURL=http://127.0.0.1:8181
  echo         set SENSEEASE_LLM_MODEL=qwen3-4b
  echo         start SenseEase.App.exe
  exit /b 1
)
echo Launching app pointed at %SENSEEASE_LLM_BASEURL% (model %SENSEEASE_LLM_MODEL%)...
start "" "%APP%"
endlocal
