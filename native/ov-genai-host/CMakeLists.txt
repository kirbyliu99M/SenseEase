cmake_minimum_required(VERSION 3.21)
project(ovgenai_host LANGUAGES CXX)

# Production HTTP sidecar for SenseEase's Intel OpenVINO GenAI LLM backend.
# Requires the OpenVINO GenAI 2026.2.1 dev archive on disk; point CMake at it by
# sourcing its setupvars.bat first (see build.bat), or pass -DOpenVINOGenAI_DIR=
# <archive>/runtime/cmake.

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()

# find_package(OpenVINOGenAI) transitively brings in OpenVINO runtime.
find_package(OpenVINOGenAI REQUIRED)
find_package(Threads REQUIRED)

add_executable(ovgenai-host src/ovgenai_host.cpp)

# Header-only third-party deps (fetched by build.bat into third_party/):
#   third_party/httplib.h   (yhirose/cpp-httplib)
#   third_party/json.hpp    (nlohmann/json, single-include)
target_include_directories(ovgenai-host PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/third_party")

target_link_libraries(ovgenai-host PRIVATE openvino::genai Threads::Threads)

if(WIN32)
  target_compile_definitions(ovgenai-host PRIVATE
    _WIN32_WINNT=0x0601   # Windows 7+ sockets API for cpp-httplib
    NOMINMAX
    WIN32_LEAN_AND_MEAN
    _CRT_SECURE_NO_WARNINGS)
  # cpp-httplib pulls ws2_32 via #pragma comment on MSVC; no explicit link needed.
endif()

if(MSVC)
  target_compile_options(ovgenai-host PRIVATE /EHsc /utf-8 /O2)
endif()

install(TARGETS ovgenai-host RUNTIME DESTINATION .)
