add_definitions(-D_LINUX -DPOSIX -DLINUX -DGNUC -DCOMPILER_GCC -DPLATFORM_64BITS -D_FILE_OFFSET_BITS=64 -D_GLIBCXX_USE_CXX11_ABI=0)

set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -Dstricmp=strcasecmp -D_stricmp=strcasecmp -D_strnicmp=strncasecmp")
set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -Dstrnicmp=strncasecmp -D_snprintf=snprintf")
set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -D_vsnprintf=vsnprintf -D_alloca=alloca -Dstrcmpi=strcasecmp")

# Warnings
set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -Wall -Wno-uninitialized -Wno-switch -Wno-unused")
set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -Wno-non-virtual-dtor -Wno-overloaded-virtual")
set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -Wno-conversion-null -Wno-write-strings")
set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -Wno-invalid-offsetof -Wno-reorder")

# Others
set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -mfpmath=sse -msse -fno-strict-aliasing")
set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -fno-threadsafe-statics -fvisibility=default")

# `-v` (dump the full cc1plus/as invocation, include search list and spec dump for
# EVERY translation unit) used to be unconditional here. It buried real compiler
# errors under ~120 lines of noise per file, which made a failing build effectively
# unreadable -- the one "error:" line was thousands of lines up the scrollback.
# Opt in only when actually debugging include paths or toolchain selection:
#   cmake -DCSSHARP_VERBOSE_COMPILE=ON ..
option(CSSHARP_VERBOSE_COMPILE "Pass -v to the compiler (dumps toolchain/include search per file)" OFF)
if(CSSHARP_VERBOSE_COMPILE)
    set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -v")
endif()

# Cap the error spew per file. Without this a single bad type produces a cascade of
# follow-on parse errors ("expected primary-expression before '>'") from every later
# line that used the failed type -- the FIRST error is the real one, the rest are
# noise. Colour makes the error lines findable when scrolling.
set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -fmax-errors=5 -fdiagnostics-color=always")

# Fix executable stack requirement for Debian 13+ compatibility
# Apply noexecstack to both compilation and linking stages
set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -Wa,--noexecstack")
set(CMAKE_C_FLAGS "${CMAKE_C_FLAGS} -Wa,--noexecstack")
set(CMAKE_ASM_FLAGS "${CMAKE_ASM_FLAGS} -Wa,--noexecstack")
set(CMAKE_SHARED_LINKER_FLAGS "${CMAKE_SHARED_LINKER_FLAGS} -Wl,--exclude-libs=libprotobuf.a -Wl,-z,noexecstack")
set(CMAKE_MODULE_LINKER_FLAGS "${CMAKE_MODULE_LINKER_FLAGS} -Wl,-z,noexecstack")
set(CMAKE_EXE_LINKER_FLAGS "${CMAKE_EXE_LINKER_FLAGS} -Wl,-z,noexecstack")

set(
    COUNTER_STRIKE_SHARP_LINK_LIBRARIES
    ${SOURCESDK_LIB}/linux64/libtier0.so
    ${SOURCESDK_LIB}/linux64/tier1.a
    ${SOURCESDK_LIB}/linux64/interfaces.a
    ${SOURCESDK_LIB}/linux64/mathlib.a
    spdlog
    dynload_s
    dyncall_s
    distorm
    funchook-static
    dynohook
)