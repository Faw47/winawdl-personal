# SPDX-License-Identifier: MIT
# A copy of an upstream submodule, in the build tree, with this repository's
# patches applied to it. The submodule itself is never changed, so it can be
# moved to a new upstream revision, or worked on, as it is.
#
# winawdl_patched_copy(<upstream dir> <copy dir> <patch dir> <entry>...)
#
# Copies each entry (a file or a directory of the upstream tree) into the copy
# and applies every *.patch of the patch dir, in name order, with git apply.
# The work is redone only when something it depends on has changed: the
# submodule's commit, its uncommitted changes, or a patch. Otherwise the copy
# keeps its timestamps and nothing in it is rebuilt. A patch that does not
# apply stops the configure.

find_package(Git REQUIRED)

function(winawdl_patched_copy upstream copy patch_dir)
    set(entries ${ARGN})
    file(GLOB patches "${patch_dir}/*.patch")
    list(SORT patches)
    set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS ${patches})

    execute_process(COMMAND "${GIT_EXECUTABLE}" rev-parse HEAD
        WORKING_DIRECTORY "${upstream}" OUTPUT_VARIABLE head
        RESULT_VARIABLE result OUTPUT_STRIP_TRAILING_WHITESPACE)
    if(NOT result EQUAL 0)
        message(FATAL_ERROR "${upstream} is not a checked-out submodule")
    endif()
    execute_process(COMMAND "${GIT_EXECUTABLE}" diff HEAD --binary
        WORKING_DIRECTORY "${upstream}" OUTPUT_VARIABLE changes)
    execute_process(COMMAND "${GIT_EXECUTABLE}" ls-files --others --exclude-standard
        WORKING_DIRECTORY "${upstream}" OUTPUT_VARIABLE untracked)
    string(SHA256 changes_hash "${changes}${untracked}")
    set(key "${head} ${changes_hash} ${entries}")
    foreach(patch IN LISTS patches)
        file(SHA256 "${patch}" patch_hash)
        get_filename_component(patch_name "${patch}" NAME)
        string(APPEND key " ${patch_name}=${patch_hash}")
    endforeach()

    set(stamp "${copy}.stamp")
    set(patched_list "${copy}.patched")
    if(EXISTS "${stamp}" AND EXISTS "${copy}")
        file(READ "${stamp}" previous)
        if(previous STREQUAL key)
            return()
        endif()
    endif()

    message(STATUS "Copying ${upstream} and applying ${patch_dir}")
    # file(COPY) skips a file whose timestamp matches, which a file patched
    # within the same second as its upstream change can. So the files the
    # last patches changed are removed first, to come back as upstream has
    # them. Without that record, as after a configure that stopped half way,
    # the copy is made anew.
    if(EXISTS "${stamp}" AND EXISTS "${patched_list}")
        file(STRINGS "${patched_list}" previously_patched)
        foreach(path IN LISTS previously_patched)
            file(REMOVE "${copy}/${path}")
        endforeach()
    else()
        file(REMOVE_RECURSE "${copy}")
    endif()
    file(REMOVE "${stamp}" "${patched_list}")
    file(MAKE_DIRECTORY "${copy}")
    foreach(entry IN LISTS entries)
        file(COPY "${upstream}/${entry}" DESTINATION "${copy}" PATTERN ".git" EXCLUDE)
    endforeach()
    get_filename_component(copy_parent "${copy}" DIRECTORY)
    set(patched "")
    foreach(patch IN LISTS patches)
        file(STRINGS "${patch}" targets REGEX "^\\+\\+\\+ b/")
        foreach(target IN LISTS targets)
            string(REGEX REPLACE "^\\+\\+\\+ b/" "" target "${target}")
            list(APPEND patched "${target}")
        endforeach()
        # The ceiling keeps git from finding a repository above the copy,
        # which would make it read the patch's paths from that repository's top.
        # Neither upstream fixes its line endings, so Git for Windows checks
        # them out with CRLF, and the patches have LF: --ignore-whitespace
        # lets their context match either.
        execute_process(
            COMMAND "${CMAKE_COMMAND}" -E env "GIT_CEILING_DIRECTORIES=${copy_parent}"
                    "${GIT_EXECUTABLE}" apply --ignore-whitespace --whitespace=nowarn "${patch}"
            WORKING_DIRECTORY "${copy}"
            RESULT_VARIABLE result
            ERROR_VARIABLE error)
        if(NOT result EQUAL 0)
            message(FATAL_ERROR "${patch} does not apply to ${upstream}:\n${error}")
        endif()
    endforeach()
    list(REMOVE_DUPLICATES patched)
    list(JOIN patched "\n" patched_lines)
    file(WRITE "${patched_list}" "${patched_lines}\n")
    file(WRITE "${stamp}" "${key}")
endfunction()
