@echo off
REM ============================================================================
REM Start the host from the raw genai_host.py via a Python venv.
REM This is the AV-safe, originally-validated path (runs through python.exe).
REM Needs a venv with: openvino-genai==2026.2.1.0 (pulls openvino 2026.2.1 +
REM openvino-tokenizers). This box's known venv is C:\ovgenai\venv.
REM   Usage:  run-host-pysrc.bat [device]   device = NPU (default) | GPU | CPU
REM   Override the python:  set VENV_PY=C:\path\to\venv\Scripts\python.exe
REM ============================================================================
setlocal
set "DEV=%~1"
if "%DEV%"=="" set "DEV=NPU"
set "HERE=%~dp0"
if "%VENV_PY%"=="" set "VENV_PY=C:\ovgenai\venv\Scripts\python.exe"
if "%OVHOST_MODEL%"=="" set "OVHOST_MODEL=%HERE%..\models\Qwen3-4B-Instruct-2507-ov-int4"
set "OVHOST_DEVICE=%DEV%"
set "OVHOST_PORT=8181"
set "OVHOST_MODELID=qwen3-4b"
set "OVHOST_CACHE=%LOCALAPPDATA%\SenseEase\ov_cache"
if not exist "%VENV_PY%" (
  echo [!] Python venv not found at "%VENV_PY%".
  echo     Create one:  py -3.12 -m venv C:\ovgenai\venv
  echo                  C:\ovgenai\venv\Scripts\python -m pip install openvino-genai==2026.2.1.0
  echo     Or set VENV_PY to an existing one.
  exit /b 1
)
echo.
echo  GenAI host (python src)  device=%OVHOST_DEVICE%  port=%OVHOST_PORT%
echo.
"%VENV_PY%" "%HERE%genai_host.py"
endlocal
