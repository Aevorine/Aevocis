cmake_minimum_required(VERSION 3.20)

project(AevocisNativeCpp VERSION 0.4.4 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

if(MSVC)
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    add_compile_options(/W4 /permissive- /EHsc /utf-8)
else()
    add_compile_options(-Wall -Wextra -Wpedantic -Wconversion -Wshadow)
endif()

add_library(aevocis_core STATIC
    src/core/audio_gate.cpp
    src/core/chunked_recognition.cpp
    src/core/noise_gate.cpp
    src/core/recognizer.cpp
    src/core/task_scheduler.cpp
    src/core/text_pipeline.cpp
    src/core/voice.cpp
)
target_include_directories(aevocis_core PUBLIC include)

add_library(aevocis_windows STATIC
    src/platform/windows/app_log.cpp
    src/platform/windows/global_hotkey.cpp
    src/platform/windows/autostart.cpp
    src/platform/windows/composition_host.cpp
    src/platform/windows/history_export.cpp
    src/platform/windows/command_pipe.cpp
    src/platform/windows/crash_reporter.cpp
    src/platform/windows/external_pipeline.cpp
    src/platform/windows/keyboard_hook.cpp
    src/platform/windows/single_instance.cpp
    src/platform/windows/sensevoice_recognizer.cpp
    src/platform/windows/storage.cpp
    src/platform/windows/target_window.cpp
    src/platform/windows/text_injector.cpp
    src/platform/windows/tray_icon.cpp
    src/platform/windows/update_manager.cpp
    src/platform/windows/wasapi_recorder.cpp
    src/ui/command_palette.cpp
    src/ui/main_window.cpp
    src/ui/recording_overlay.cpp
    src/ui/style.cpp
)
target_include_directories(aevocis_windows PUBLIC include)
target_link_libraries(aevocis_windows PUBLIC
    aevocis_core
    avrt
    bcrypt
    comctl32
    d2d1
    d3d11
    dcomp
    dwmapi
    dwrite
    dxgi
    ole32
    shlwapi
    shell32
    advapi32
    user32
    uxtheme
    winhttp
    ksuser
)
target_compile_definitions(aevocis_windows PUBLIC UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN _WIN32_WINNT=0x0A00)

set(SHERPA_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/.cache/sherpa-onnx-v1.13.7-win-x64-shared-MT-Release" CACHE PATH "Verified sherpa-onnx development package")
if(EXISTS "${SHERPA_ROOT}/include/sherpa-onnx/c-api/cxx-api.h" AND EXISTS "${SHERPA_ROOT}/lib/sherpa-onnx-cxx-api.lib" AND NOT CMAKE_BUILD_TYPE STREQUAL "Debug")
    target_compile_definitions(aevocis_windows PRIVATE AEVOCIS_HAS_SHERPA=1)
    target_include_directories(aevocis_windows PRIVATE "${SHERPA_ROOT}/include")
    target_link_libraries(aevocis_windows PRIVATE "${SHERPA_ROOT}/lib/sherpa-onnx-c-api.lib")
endif()

add_executable(aevocis WIN32 src/app/main.cpp)
target_link_libraries(aevocis PRIVATE aevocis_windows)
target_compile_definitions(aevocis PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN _WIN32_WINNT=0x0A00)
if(NOT MSVC)
    target_link_options(aevocis PRIVATE -municode)
endif()

if(EXISTS "${SHERPA_ROOT}/include/sherpa-onnx/c-api/cxx-api.h" AND NOT CMAKE_BUILD_TYPE STREQUAL "Debug")
    foreach(SHERPA_DLL
            "${SHERPA_ROOT}/lib/onnxruntime.dll"
            "${SHERPA_ROOT}/lib/onnxruntime_providers_shared.dll"
            "${SHERPA_ROOT}/lib/sherpa-onnx-c-api.dll"
            "${SHERPA_ROOT}/lib/sherpa-onnx-cxx-api.dll")
        if(EXISTS "${SHERPA_DLL}")
            add_custom_command(TARGET aevocis POST_BUILD COMMAND ${CMAKE_COMMAND} -E copy_if_different
                               "${SHERPA_DLL}" "$<TARGET_FILE_DIR:aevocis>")
        endif()
    endforeach()
endif()

add_executable(aevocis_model_check src/app/model_check.cpp)
target_link_libraries(aevocis_model_check PRIVATE aevocis_windows)
target_compile_definitions(aevocis_model_check PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN _WIN32_WINNT=0x0A00)
if(NOT MSVC)
    target_link_options(aevocis_model_check PRIVATE -municode)
endif()

add_executable(aevocis_cli src/app/cli_main.cpp)
target_link_libraries(aevocis_cli PRIVATE aevocis_windows)

if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/Models")
    add_custom_command(TARGET aevocis POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_directory
                "${CMAKE_CURRENT_SOURCE_DIR}/Models" "$<TARGET_FILE_DIR:aevocis>/Models")
endif()

if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/assets/Aevocis.ico")
    add_custom_command(TARGET aevocis POST_BUILD COMMAND ${CMAKE_COMMAND} -E copy_if_different
                       "${CMAKE_CURRENT_SOURCE_DIR}/assets/Aevocis.ico" "$<TARGET_FILE_DIR:aevocis>/Aevocis.ico")
endif()
if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/assets/Aevocis.png")
    add_custom_command(TARGET aevocis POST_BUILD COMMAND ${CMAKE_COMMAND} -E copy_if_different
                       "${CMAKE_CURRENT_SOURCE_DIR}/assets/Aevocis.png" "$<TARGET_FILE_DIR:aevocis>/Aevocis.png")
endif()

enable_testing()
add_executable(aevocis_core_tests tests/core_tests.cpp)
target_link_libraries(aevocis_core_tests PRIVATE aevocis_core)
target_include_directories(aevocis_core_tests PRIVATE include)
add_test(NAME aevocis_core_tests COMMAND aevocis_core_tests)

install(TARGETS aevocis RUNTIME DESTINATION .)
