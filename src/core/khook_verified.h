/*
 *  This file is part of CounterStrikeSharp.
 *  CounterStrikeSharp is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  CounterStrikeSharp is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with CounterStrikeSharp.  If not, see <https://www.gnu.org/licenses/>. *
 */

#pragma once

#include <khook.hpp>

namespace counterstrikesharp {
namespace hooks {

// Reporting is a free function, not a direct logger call, so this header stays
// free of spdlog -- src/core/tests links the templates below against its own
// definition without dragging in the plugin's logging stack.
// Implemented in khook_verified.cpp.
void ReportHookRegistration(const char* name, void** vtable, int slot, bool ok, const char* reason);

// KHook can refuse to install a hook, and says so only through a return value the
// plain API hides from us.
//
// KHook::SetupHook returns INVALID_HOOK when the underlying detour cannot be set up
// -- safetyhook::InlineHook::create() failing on the target, or the target range
// overlapping one KHook has already patched (khook/src/detour.cpp, __SetupHook:
// a failed setup_hook erases the capsule and returns INVALID_HOOK). KHook::Virtual
// swallows that: _Setup() records an id in _addr_hook_ids only on success and
// returns void either way. So a hook that never installed is indistinguishable from
// a working one until the game calls the function and our callbacks simply never
// run -- which looks like "the server started fine and then behaved wrong", the
// worst possible failure shape to diagnose from a server log.
//
// These wrappers check whether the hook actually registered and log loudly if not.
// They deliberately log rather than throw: a missing hook degrades one feature,
// while an exception out of a manager's OnAllInitialized takes down the whole
// plugin load.
template <class CLASS, class RETURN, class... ARGS> class Virtual : public KHook::Virtual<CLASS, RETURN, ARGS...>
{
    using Base = KHook::Virtual<CLASS, RETURN, ARGS...>;

  public:
    using Base::Base;

    // Hooks this instance's vtable. Returns false (and logs) if KHook did not take it.
    bool Add(CLASS* instance, const char* name)
    {
        if (instance == nullptr)
        {
            ReportHookRegistration(name, nullptr, -1, false, "the interface pointer is null");
            return false;
        }

        Base::Add(instance);

        return VerifyRegistration(*reinterpret_cast<void***>(instance), name);
    }

    // Hooks every object sharing this vtable (the DVP equivalent). Note that, like
    // KHook itself, this dereferences its argument to reach the vtable, so a bare
    // vtable pointer must be passed as &thatPointer.
    bool AddGlobal(CLASS* instance, const char* name)
    {
        if (instance == nullptr)
        {
            ReportHookRegistration(name, nullptr, -1, false, "the interface pointer is null");
            return false;
        }

        Base::AddGlobal(instance);

        return VerifyRegistration(*reinterpret_cast<void***>(instance), name);
    }

  private:
    bool VerifyRegistration(void** vtable, const char* name)
    {
        // INVALID_VTBL_INDEX is private to KHook::Virtual, so compare against the
        // value it is initialised to rather than the constant.
        if (this->_vtbl_index < 0)
        {
            ReportHookRegistration(name, vtable, this->_vtbl_index, false,
                                   "no vtable index -- the member function pointer did not resolve, or Configure() was never called "
                                   "with a gamedata offset");
            return false;
        }

        std::lock_guard guard(this->_hooks_stored);

        if (this->_addr_hook_ids.find(vtable + this->_vtbl_index) == this->_addr_hook_ids.end())
        {
            ReportHookRegistration(name, vtable, this->_vtbl_index, false,
                                   "KHook refused the hook -- another plugin may already have patched this function");
            return false;
        }

        ReportHookRegistration(name, vtable, this->_vtbl_index, true, nullptr);

        return true;
    }
};

} // namespace hooks
} // namespace counterstrikesharp
