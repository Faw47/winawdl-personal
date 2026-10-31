# SPDX-License-Identifier: GPL-3.0-or-later
# AWDL daemon: OWL's library and event core, with the fixes in patches/, on a
# Windows I/O layer. OWL is GNU C, hence a MinGW build apart from the MSVC
# radio side.
cmake_minimum_required(VERSION 3.15)
project(winawdl_awdl C)

# The file names in log lines and in the programs themselves start at the
# repository, not at the folder of the disk it was cloned into.
get_filename_component(REPOSITORY "${CMAKE_CURRENT_SOURCE_DIR}/.." ABSOLUTE)
add_compile_options("-ffile-prefix-map=${REPOSITORY}=.")

set(WINAWDL_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/..")
set(OWL_UPSTREAM "${WINAWDL_ROOT}/upstream/owl")

# The submodule stays as upstream has it. Its sources are copied into the
# build tree and patches/ applied there, at every configure, so the copy is
# never edited by hand and a changed patch or submodule redoes it.
set(OWL "${CMAKE_CURRENT_BINARY_DIR}/owl")
file(GLOB OWL_PATCHES "${CMAKE_CURRENT_SOURCE_DIR}/patches/*.patch")
list(SORT OWL_PATCHES)
file(GLOB_RECURSE OWL_UPSTREAM_FILES
    "${OWL_UPSTREAM}/src/*" "${OWL_UPSTREAM}/daemon/*" "${OWL_UPSTREAM}/radiotap/*.[ch]")
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS
    ${OWL_PATCHES} ${OWL_UPSTREAM_FILES})
find_package(Git REQUIRED)
file(REMOVE_RECURSE "${OWL}")
file(MAKE_DIRECTORY "${OWL}")
file(COPY "${OWL_UPSTREAM}/src" "${OWL_UPSTREAM}/daemon" DESTINATION "${OWL}")
file(COPY "${OWL_UPSTREAM}/radiotap" DESTINATION "${OWL}" PATTERN ".git" EXCLUDE)
foreach(patch IN LISTS OWL_PATCHES)
    # The ceiling keeps git from finding this repository above the copy, which
    # would make it read the patch's paths from the repository's top.
    execute_process(
        COMMAND "${CMAKE_COMMAND}" -E env "GIT_CEILING_DIRECTORIES=${CMAKE_CURRENT_BINARY_DIR}"
                "${GIT_EXECUTABLE}" apply --whitespace=nowarn "${patch}"
        WORKING_DIRECTORY "${OWL}"
        RESULT_VARIABLE patch_result
        ERROR_VARIABLE patch_error)
    if(NOT patch_result EQUAL 0)
        message(FATAL_ERROR "OWL patch ${patch} does not apply:\n${patch_error}")
    endif()
endforeach()

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_EXTENSIONS ON)

# compat/ comes first so OWL's Unix includes resolve to the shims.
# -mno-ms-bitfields lays packed structs out as GCC does on Linux.
add_library(owl_build INTERFACE)
target_include_directories(owl_build INTERFACE
    compat "${OWL}/src" "${OWL}/radiotap" "${OWL}/daemon" win
    "${WINAWDL_ROOT}/radio")
target_compile_options(owl_build INTERFACE
    -include "${CMAKE_CURRENT_SOURCE_DIR}/compat/owl_win.h" -mno-ms-bitfields)

add_library(radiotap STATIC "${OWL}/radiotap/radiotap.c")
target_link_libraries(radiotap PRIVATE owl_build)
# Its bounds checks subtract pointers through unsigned long, which is 32 bits
# on Windows. The differences are small, so the truncation cancels out.
target_compile_options(radiotap PRIVATE -Wno-pointer-to-int-cast)

set(OWL_SOURCES log state election sync channel schedule tx rx frame crc32 wire
    peers version hashmap siphash24 circular_buffer)
list(TRANSFORM OWL_SOURCES PREPEND "${OWL}/src/")
list(TRANSFORM OWL_SOURCES APPEND ".c")
add_library(awdl STATIC ${OWL_SOURCES})
target_link_libraries(awdl PUBLIC owl_build radiotap)

# The host adapter is kept clear of owl_build: -mno-ms-bitfields must not
# reach the Windows networking structures it fills in.
add_library(host_if STATIC win/host_if.c win/host_frame.c)
target_include_directories(host_if PUBLIC win)
target_compile_options(host_if PRIVATE -Wall -Wextra -Werror)
target_link_libraries(host_if PRIVATE iphlpapi)

set(WIN_SOURCES
    win/main.c win/ev_win.c win/io_win.c win/netutils_win.c win/radio_client.c
    win/redact.c win/frame_fixup.c win/tcp_merge.c win/service_bridge.c
    compat/ether.c)
add_executable(awdld "${OWL}/daemon/core.c" ${WIN_SOURCES})
set_source_files_properties(${WIN_SOURCES} PROPERTIES
    COMPILE_OPTIONS "-Wall;-Wextra;-Werror")
target_link_libraries(awdld PRIVATE awdl host_if bcrypt)
# Static, so the daemon does not need the MinGW runtime DLLs beside it.
# The wrap routes OWL's log calls through main.c, which can redact them.
target_link_options(awdld PRIVATE -static -Wl,--wrap=log_log)

add_executable(host_frame_test tests/host_frame_test.c)
target_compile_options(host_frame_test PRIVATE -Wall -Wextra -Werror)
target_link_libraries(host_frame_test PRIVATE host_if)
target_link_options(host_frame_test PRIVATE -static)

add_executable(tcp_merge_test tests/tcp_merge_test.c win/tcp_merge.c)
target_include_directories(tcp_merge_test PRIVATE win)
target_compile_options(tcp_merge_test PRIVATE -Wall -Wextra -Werror)
target_link_options(tcp_merge_test PRIVATE -static)

add_executable(frame_fixup_test tests/frame_fixup_test.c win/frame_fixup.c)
target_include_directories(frame_fixup_test PRIVATE win)
target_compile_options(frame_fixup_test PRIVATE -Wall -Wextra -Werror)
target_link_options(frame_fixup_test PRIVATE -static)

add_executable(service_bridge_test tests/service_bridge_test.c win/service_bridge.c
    win/frame_fixup.c)
target_include_directories(service_bridge_test PRIVATE win)
target_compile_options(service_bridge_test PRIVATE -Wall -Wextra -Werror)
target_link_options(service_bridge_test PRIVATE -static)

add_executable(redact_test tests/redact_test.c win/redact.c)
target_include_directories(redact_test PRIVATE win)
target_compile_options(redact_test PRIVATE -Wall -Wextra -Werror)
target_link_options(redact_test PRIVATE -static)

# Security regression for the OWL A-MSDU output-capacity check.
add_executable(rx_amsdu_bounds_test tests/rx_amsdu_bounds_test.c compat/ether.c)
target_link_libraries(rx_amsdu_bounds_test PRIVATE awdl)
target_compile_options(rx_amsdu_bounds_test PRIVATE -Wall -Wextra -Werror)
target_link_options(rx_amsdu_bounds_test PRIVATE -static)

# Regression for the election order in patches/0002.
add_executable(election_order_test tests/election_order_test.c compat/ether.c)
target_link_libraries(election_order_test PRIVATE awdl)
target_compile_options(election_order_test PRIVATE -Wall -Wextra -Werror)
target_link_options(election_order_test PRIVATE -static)
