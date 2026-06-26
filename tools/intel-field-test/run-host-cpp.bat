@echo off
REM ============================================================================
REM Start the OpenVINO GenAI host - NATIVE C++ sidecar (production build).
REM Same contract/behaviour as the Python host. If antivirus blocks this fresh
REM unsigned exe, allow-list the host-cpp folder, or use the Python host instead.
REM   Usage:  run-host-cpp.bat [device]    device = NPU (default) | GPU | CPU
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
REM the C++ exe finds its OpenVINO DLLs in its own folder
set "PATH=%HERE%host-cpp;%PATH%"
echo.
echo  GenAI host (C++)  device=%OVHOST_DEVICE%  port=%OVHOST_PORT%
echo  model=%OVHOST_MODEL%
echo.
if not exist "%OVHOST_MODEL%\openvino_model.xml" (
  echo [!] IR not found at "%OVHOST_MODEL%". Set OVHOST_MODEL and re-run.
  exit /b 1
)
"%HERE%host-cpp\ovgenai-host.exe"
endlocal
