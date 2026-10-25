# Stop at configure time when a submodule checkout is not the commit this tree
# records. `git pull` moves the gitlinks but not the checkouts, so after an SDK
# bump the build otherwise dies on a file the old SDK never had (e.g.
# "Cannot find source file: libraries/hl2sdk-cs2/tier0_perproject/convar.cpp").
# Skipped outside a git checkout (source tarballs) and with
# -DCSS_SKIP_SUBMODULE_CHECK=ON for deliberate local SDK work.
option(CSS_SKIP_SUBMODULE_CHECK "Do not compare submodule checkouts with the recorded commits" OFF)

find_package(Git QUIET)
if(NOT CSS_SKIP_SUBMODULE_CHECK AND GIT_FOUND AND EXISTS "${PROJECT_SOURCE_DIR}/.git")
    execute_process(
        COMMAND "${GIT_EXECUTABLE}" submodule status --recursive
        WORKING_DIRECTORY "${PROJECT_SOURCE_DIR}"
        OUTPUT_VARIABLE _css_submodule_status
        RESULT_VARIABLE _css_submodule_result
        ERROR_QUIET)
    if(_css_submodule_result EQUAL 0)
        string(REPLACE "\n" ";" _css_submodule_lines "${_css_submodule_status}")
        set(_css_stale "")
        foreach(_line IN LISTS _css_submodule_lines)
            # "-" = not initialised, "+" = checked out at another commit, "U" = conflicted.
            if(_line MATCHES "^([-+U])[0-9a-f]+ ([^ ]+)")
                list(APPEND _css_stale "  ${CMAKE_MATCH_1} ${CMAKE_MATCH_2}")
            endif()
        endforeach()
        if(_css_stale)
            string(REPLACE ";" "\n" _css_stale "${_css_stale}")
            message(FATAL_ERROR
                "Submodules do not match the commits this tree records:\n${_css_stale}\n"
                "Update them, then configure again:\n"
                "  git submodule sync --recursive\n"
                "  git submodule update --init --recursive\n"
                "(-DCSS_SKIP_SUBMODULE_CHECK=ON skips this check.)")
        endif()
    endif()
endif()
