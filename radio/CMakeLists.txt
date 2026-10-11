# SPDX-License-Identifier: GPL-2.0-only
# Radio-side programs. They link devourer, which is GPL-2.0, so these are too.
cmake_minimum_required(VERSION 3.15)

# devourer only picks the vcpkg toolchain when it is the top-level project.
if(WIN32 AND NOT "$ENV{VCPKG_ROOT}" STREQUAL "")
    set(CMAKE_TOOLCHAIN_FILE "$ENV{VCPKG_ROOT}/scripts/buildsystems/vcpkg.cmake")
endif()

project(winawdl_radio CXX C)

set(WINAWDL_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/..")

# Only the RTL8852C, over the Windows register proxy.
foreach(chip JAGUAR1 8814 JAGUAR2_8822B JAGUAR2_8821C JAGUAR3_8822C
             JAGUAR3_8822E 8733B KESTREL_8852B)
    set(DEVOURER_${chip} OFF CACHE BOOL "" FORCE)
endforeach()
set(DEVOURER_KESTREL_8852C ON CACHE BOOL "" FORCE)
set(DEVOURER_PCIE ON CACHE BOOL "" FORCE)
set(DEVOURER_REGPROXY_INCLUDE_DIR "${WINAWDL_ROOT}/driver/regproxy"
    CACHE PATH "" FORCE)

# EXCLUDE_FROM_ALL keeps devourer's own demos and tests out of the build. The
# three used here hang off one target the Visual Studio generator can build.
add_subdirectory("${WINAWDL_ROOT}/upstream/devourer" devourer EXCLUDE_FROM_ALL)
add_custom_target(devourer_demos)
add_dependencies(devourer_demos rxdemo txdemo pcieprobe)

function(winawdl_radio_program name)
    add_executable(${name} ${ARGN})
    target_compile_features(${name} PRIVATE cxx_std_20)
    target_link_libraries(${name} PRIVATE devourer)
    if(MSVC)
        target_compile_options(${name} PRIVATE /W4 /WX)
        # devourer's DeviceConfig.h calls sscanf, which MSVC flags as deprecated.
        target_compile_definitions(${name} PRIVATE _CRT_SECURE_NO_WARNINGS)
    endif()
endfunction()

winawdl_radio_program(awdl_sniff awdl_sniff.cpp)
winawdl_radio_program(winawdl_radio winawdl_radio.cpp radio_link.h)

# Needs no radio, so it runs anywhere.
add_executable(pipe_server_test tests/pipe_server_test.cpp pipe_server.h)
target_compile_features(pipe_server_test PRIVATE cxx_std_20)
target_include_directories(pipe_server_test PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
if(MSVC)
    target_compile_options(pipe_server_test PRIVATE /W4 /WX)
endif()

# Checks devourer's address handling for the Windows backend, also without a
# radio: only its two header-only pieces are compiled.
add_executable(dma_address_test tests/dma_address_test.cpp)
target_compile_features(dma_address_test PRIVATE cxx_std_20)
target_include_directories(dma_address_test PRIVATE
    "${WINAWDL_ROOT}/upstream/devourer/src")
if(MSVC)
    target_compile_options(dma_address_test PRIVATE /W4 /WX)
endif()
