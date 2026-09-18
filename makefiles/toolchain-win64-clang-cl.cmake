# Cross-compile the Windows (win64) plugin from Linux with clang-cl + lld-link,
# against an MSVC CRT / Windows SDK laid down by xwin (https://github.com/Jake-Shadle/xwin):
#
#   xwin --accept-license --arch x86_64 splat --output ~/.xwin
#   cmake -S . -B build-win64 \
#         -DCMAKE_TOOLCHAIN_FILE=makefiles/toolchain-win64-clang-cl.cmake \
#         -DCMAKE_BUILD_TYPE=Release
#
# This exists so the Windows half of a change can be verified without a Windows
# machine. It is NOT the release toolchain -- release artifacts still come from
# the MSVC build in .github/workflows/build-windows.yml.

set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_PROCESSOR AMD64)

set(XWIN_ROOT "$ENV{HOME}/.xwin" CACHE PATH "Directory xwin splatted the CRT and SDK into")
if(NOT EXISTS "${XWIN_ROOT}/crt/include")
    message(FATAL_ERROR "No MSVC CRT at ${XWIN_ROOT}/crt -- run xwin splat first, or pass -DXWIN_ROOT=...")
endif()

# Every LLVM install carries these tools under /usr/lib/llvm-<N>/bin with plain
# unversioned names; what varies by distro is whether /usr/bin symlinks exist for
# them. Debian's clang/llvm packages create them, the GitHub ubuntu runner images
# do not, so point at a bin directory rather than trusting PATH:
#     -DLLVM_TOOLCHAIN_BIN=/usr/lib/llvm-21/bin
# Left empty, the bare names are used and PATH decides.
set(LLVM_TOOLCHAIN_BIN "" CACHE PATH "bin directory of the LLVM install to build with")

function(_xwin_tool outputVariable toolName)
    if(LLVM_TOOLCHAIN_BIN)
        set(${outputVariable} "${LLVM_TOOLCHAIN_BIN}/${toolName}" PARENT_SCOPE)
    else()
        set(${outputVariable} "${toolName}" PARENT_SCOPE)
    endif()
endfunction()

_xwin_tool(_xwin_clang_cl clang-cl)
_xwin_tool(_xwin_ml64 llvm-ml64)
_xwin_tool(_xwin_rc llvm-rc)
_xwin_tool(_xwin_lib llvm-lib)
_xwin_tool(_xwin_link lld-link)
_xwin_tool(_xwin_mt llvm-mt)

set(CMAKE_C_COMPILER   "${_xwin_clang_cl}")
set(CMAKE_CXX_COMPILER "${_xwin_clang_cl}")
set(CMAKE_ASM_MASM_COMPILER "${_xwin_ml64}")
set(CMAKE_RC_COMPILER  "${_xwin_rc}")
set(CMAKE_AR           "${_xwin_lib}")
set(CMAKE_LINKER       "${_xwin_link}")
set(CMAKE_MT           "${_xwin_mt}")

# clang-cl needs the target spelled out; it defaults to the host triple otherwise.
set(_xwin_target "--target=x86_64-pc-windows-msvc")

# /imsvc keeps the SDK headers on the system-include path, so their warnings stay
# out of the build log the way they would under a real MSVC install.
set(_xwin_includes
    "/imsvc${XWIN_ROOT}/crt/include"
    "/imsvc${XWIN_ROOT}/sdk/include/ucrt"
    "/imsvc${XWIN_ROOT}/sdk/include/um"
    "/imsvc${XWIN_ROOT}/sdk/include/shared"
)
string(JOIN " " _xwin_include_flags ${_xwin_includes})

# xwin splats only the release CRT unless asked for --include-debug-libs, and
# CMake's own compiler probe would otherwise reach for msvcrtd.lib. The plugin
# links the static release CRT in every config anyway (see CMakeLists.txt).
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")

# See xwin-crt-shim.h: declares one internal CRT entry point this SDK omits.
set(_xwin_shim "/FI${CMAKE_CURRENT_LIST_DIR}/xwin-crt-shim.h")

set(CMAKE_C_FLAGS_INIT   "${_xwin_target} ${_xwin_shim} ${_xwin_include_flags}")
set(CMAKE_CXX_FLAGS_INIT "${_xwin_target} ${_xwin_shim} ${_xwin_include_flags}")

set(_xwin_libpaths
    "-libpath:${XWIN_ROOT}/crt/lib/x86_64"
    "-libpath:${XWIN_ROOT}/sdk/lib/ucrt/x86_64"
    "-libpath:${XWIN_ROOT}/sdk/lib/um/x86_64"
)
string(JOIN " " _xwin_link_flags ${_xwin_libpaths})

set(CMAKE_EXE_LINKER_FLAGS_INIT    "${_xwin_link_flags}")
set(CMAKE_SHARED_LINKER_FLAGS_INIT "${_xwin_link_flags}")
set(CMAKE_MODULE_LINKER_FLAGS_INIT "${_xwin_link_flags}")

# Only look for target libraries under the sysroot; host /usr/lib is not Windows.
set(CMAKE_FIND_ROOT_PATH "${XWIN_ROOT}")
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_PACKAGE ONLY)
