# Host-architecture unit tests. Built only when -DBUILD_CSS_TESTS=ON.
#
# These tests deliberately do NOT link against the main counterstrikesharp
# target -- that would drag in hl2sdk-cs2, spdlog, protobuf, dotnet host, and
# metamod. Each test mirrors the production logic it exercises and is
# self-contained; see the comment at the top of each test_*.cpp for details.
#
# Catch2 is consumed via its amalgamated single-source shipped in the upstream
# Catch2 repo (libraries/Catch2 submodule, pinned to v3.5.4). That keeps the
# compile a single clang++ invocation -- no need to configure Catch2's full
# CMake project, which would conflict with the dyncall pre-3.5 cmake_minimum.

set(CATCH2_AMALG_DIR ${CMAKE_SOURCE_DIR}/libraries/Catch2/extras)

add_executable(css_unit_tests
    test_timer_system_startup.cpp
    ${CATCH2_AMALG_DIR}/catch_amalgamated.cpp
)

target_include_directories(css_unit_tests PRIVATE
    ${CATCH2_AMALG_DIR}
    ${CMAKE_SOURCE_DIR}/src
)

target_compile_features(css_unit_tests PRIVATE cxx_std_17)

# Run the binary with --reporter=console; CTest will surface failures.
add_test(NAME css_unit_tests COMMAND css_unit_tests)
