// Force-included by makefiles/toolchain-win64-clang-cl.cmake for the xwin
// cross-build only. Never reaches the MSVC build.
//
// hl2sdk's public/tier0/memoverride.cpp calls _recalloc_base() (line 227) but
// leaves its own definition inside an #if 0 block, relying on the CRT headers to
// declare it. Windows SDK 10.0.26100's corecrt_malloc.h declares _malloc_base,
// _calloc_base and _realloc_base but not _recalloc_base, so clang-cl rejects the
// call. The symbol is still exported by the ucrt, so a declaration is all that is
// missing -- the signature below is the _MSC_VER >= 1900 form memoverride.cpp
// calls it with.
#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

void* __cdecl _recalloc_base(void* block, size_t count, size_t size);

#ifdef __cplusplus
}
#endif
