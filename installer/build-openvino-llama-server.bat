@echo off
REM ============================================================================
REM build-openvino-llama-server.bat
REM ----------------------------------------------------------------------------
REM Builds the OpenVINO `llama-server.exe` for SenseEase's Intel NPU route.
REM See docs/PHASE3_NPU_INSTALLER_PLAN.md section 11 (BUILT + VALIDATED 2026-06-21
REM with VS 2026 + OpenVINO 2026.2 + vcpkg opencl). NOT run by any pipeline — manual.
REM
REM PREREQS (install once):
REM   1. Visual Studio 2022/2026 with the "Desktop development with C++" workload.
REM   2. OpenVINO toolkit (Windows archive). https://docs.openvino.ai
REM   3. vcpkg + OpenCL:  git clone https://github.com/microsoft/vcpkg && .\vcpkg\bootstrap-vcpkg.bat
REM                       .\vcpkg\vcpkg install opencl        (OpenCL is a REQUIRED build dep of ggml-openvino)
REM   4. llama.cpp source: git clone --depth 1 https://github.com/ggml-org/llama.cpp
REM
REM EDIT the four paths below for your machine, then run from a normal cmd window
REM (the script activates the VS + OpenVINO build environments itself).
REM ============================================================================
setlocal

set "VSDEVCMD=C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat"
set "OV_SETUPVARS=C:\path\to\openvino_toolkit\setupvars.bat"
set "VCPKG_ROOT=C:\path\to\vcpkg"
set "LLAMA_SRC=C:\path\to\llama.cpp"

call "%VSDEVCMD%" -arch=amd64 -host_arch=amd64 || (echo VsDevCmd failed & exit /b 1)
call "%OV_SETUPVARS%"                            || (echo setupvars failed & exit /b 1)
cd /d "%LLAMA_SRC%"                              || (echo bad LLAMA_SRC & exit /b 1)

cmake -B build\ReleaseOV -G Ninja -DCMAKE_BUILD_TYPE=Release -DGGML_OPENVINO=ON ^
      -DCMAKE_TOOLCHAIN_FILE=%VCPKG_ROOT%\scripts\buildsystems\vcpkg.cmake ^
      -DLLAMA_CURL=OFF -DGGML_VULKAN=OFF || (echo cmake configure failed & exit /b 1)

cmake --build build\ReleaseOV --target llama-server --parallel || (echo build failed & exit /b 1)

echo.
echo Built: %LLAMA_SRC%\build\ReleaseOV\bin\llama-server.exe
echo Next: assemble the self-contained runtime pack (build bin DLLs + the toolkit's
echo   runtime\bin\intel64\Release\*.dll + runtime\3rdparty\tbb\bin\tbb*.dll + the VC
echo   runtime) into runtimes\win-x64\native\openvino\ -- see PHASE3 doc section 11.
endlocal
