@echo off
REM ============================================================================
REM Build the OpenVINO GenAI HTTP sidecar (ovgenai-host.exe).
REM
REM Prereqs:
REM   1. OpenVINO GenAI 2026.2.1 dev archive extracted somewhere, e.g.
REM      C:\openvino_genai_2026.2.1  (must contain setupvars.bat + runtime\cmake).
REM   2. Visual Studio 2022/2026 C++ toolchain + CMake on PATH.
REM
REM Usage:
REM   build.bat "C:\path\to\openvino_genai_2026.2.1"
REM ============================================================================
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "OV_GENAI_DIR=%~1"
if "%OV_GENAI_DIR%"=="" set "OV_GENAI_DIR=%OV_GENAI_DIR_ENV%"
if "%OV_GENAI_DIR%"=="" (
  echo [ERROR] Pass the OpenVINO GenAI archive root as arg 1, or set OV_GENAI_DIR_ENV.
  exit /b 2
)
if not exist "%OV_GENAI_DIR%\setupvars.bat" (
  echo [ERROR] "%OV_GENAI_DIR%\setupvars.bat" not found - wrong archive path?
  exit /b 2
)

REM --- fetch header-only third-party deps (pinned) -----------------------------
if not exist third_party mkdir third_party
if not exist third_party\httplib.h (
  echo Fetching cpp-httplib...
  curl -L -o third_party\httplib.h https://raw.githubusercontent.com/yhirose/cpp-httplib/v0.15.3/httplib.h || (echo [ERROR] httplib download failed & exit /b 1)
)
if not exist third_party\json.hpp (
  echo Fetching nlohmann/json...
  curl -L -o third_party\json.hpp https://github.com/nlohmann/json/releases/download/v3.11.3/json.hpp || (echo [ERROR] json download failed & exit /b 1)
)

REM --- OpenVINO env (sets OpenVINO_DIR / OpenVINOGenAI_DIR, adds DLLs to PATH) --
call "%OV_GENAI_DIR%\setupvars.bat"

REM --- configure + build -------------------------------------------------------
cmake -S . -B build -A x64 || (echo [ERROR] cmake configure failed & exit /b 1)
cmake --build build --config Release -j || (echo [ERROR] build failed & exit /b 1)

echo.
echo [OK] Built build\Release\ovgenai-host.exe
endlocal
