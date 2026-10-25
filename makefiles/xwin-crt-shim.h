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
//
// The same file spells _msize_base's exception spec as _CRT_NOEXCEPT, a macro
// newer MSVC CRTs define as noexcept. The xwin CRT (MSVC 14.4x, UCRT 10.0.26100)
// has no such macro, so the definition does not parse; define it the way MSVC
// does. Before the s2sdk rework the file wrote `noexcept` itself for
// _MSC_VER >= 1930, which clang-cl reports.
#pragma once

#include <stddef.h>

#if defined(__cplusplus) && !defined(_CRT_NOEXCEPT)
#define _CRT_NOEXCEPT noexcept
#endif

#ifdef __cplusplus
extern "C" {
#endif

void* __cdecl _recalloc_base(void* block, size_t count, size_t size);

#ifdef __cplusplus
}
#endif
