@echo off
REM ============================================================================
REM Start the OpenVINO GenAI host (PyInstaller bundle - no Python install needed).
REM   Usage:  run-host.bat [device]      device = NPU (default) | GPU | CPU
REM ============================================================================
setlocal
set "DEV=%~1"
if "%DEV%"=="" set "DEV=NPU"
set "HERE=%~dp0"
if "%OVHOST_MODEL%"=="" set "OVHOST_MODEL=%HERE%..\models\Qwen3-4B-Instruct-2507-ov-int4"
set "OVHOST_DEVICE=%DEV%"
set "OVHOST_PORT=8181"
set "OVHOST_MODELID=qwen3-4b"
set "OVHOST_CACHE=%LOCALAPPDATA%\SenseEase\ov_cache"
echo.
echo  GenAI host  device=%OVHOST_DEVICE%  port=%OVHOST_PORT%
echo  model=%OVHOST_MODEL%
echo  cache=%OVHOST_CACHE%
echo  (first NPU compile is ~237s ONE-TIME; warm start ~5s. Wait for "server READY".)
echo.
if not exist "%OVHOST_MODEL%\openvino_model.xml" (
  echo [!] IR not found at "%OVHOST_MODEL%".
  echo     Set OVHOST_MODEL to the IR folder, then re-run.
  exit /b 1
)
"%HERE%host-python\ovgenai-host.exe"
endlocal
