#pragma once

#include <khook.hpp>

namespace counterstrikesharp {
namespace hooks {

// KHook only allocates the original-return storage when the original function
// actually ran: `original_return_ptr` starts at 0 and is filled from
// SaveReturnValue(..., original=true). If any consumer superseded the call, it
// stays null, so KHook::GetOriginalReturn<T>() (a blind `*(T*)ptr`) dereferences
// null and takes the server down. That is not hypothetical for us --
// IServerGameClients::ClientConnect is exactly what a ban/queue plugin
// supersedes to reject a player. Prefer the original value, fall back to whatever
// the superseding hook returned, and only then to the caller's default.
//
// Lives in its own header (not globals.h) so src/core/tests can compile the SAME
// definition the plugin uses without dragging in the SDK. See
// tests/test_khook_semantics.cpp.
template <typename T> inline T OriginalReturnOr(const T& fallback)
{
    if (auto* original = static_cast<T*>(KHook::GetOriginalValuePtr()))
    {
        return *original;
    }

    if (auto* overridden = static_cast<T*>(KHook::GetOverrideValuePtr()))
    {
        return *overridden;
    }

    return fallback;
}

} // namespace hooks
} // namespace counterstrikesharp
