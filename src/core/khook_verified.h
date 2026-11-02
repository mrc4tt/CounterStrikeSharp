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

#include <algorithm>
#include <unordered_map>
#include <vector>

namespace counterstrikesharp {
namespace hooks {

// Reporting is a free function, not a direct logger call, so this header stays
// free of spdlog -- src/core/tests links the templates below against its own
// definition without dragging in the plugin's logging stack.
// Implemented in khook_verified.cpp.
void ReportHookRegistration(const char* name, void** vtable, int slot, bool ok, const char* reason);

// Logs one Info line with how many hooks ReportHookRegistration has seen so far.
// Implemented in khook_verified.cpp.
void LogHookSummary();

// Every hooks::Virtual, so Unload can detach them all (DetachAll) without each manager
// having to list its members. Header-only: src/core/tests links this header without
// khook_verified.cpp.
class Detachable
{
  public:
    Detachable() { Registry().push_back(this); }
    virtual ~Detachable() { std::erase(Registry(), this); }
    Detachable(const Detachable&) = delete;
    Detachable& operator=(const Detachable&) = delete;

    virtual void Detach() = 0;

    // Function-local: the hooks::Virtual members of static managers register during static
    // initialisation, in no particular order relative to any other file.
    static std::vector<Detachable*>& Registry()
    {
        static std::vector<Detachable*> registry;
        return registry;
    }
};

// Removes the KHook detour of every hooks::Virtual and forgets its ids. Last thing in Unload.
//
// Remove()/RemoveGlobal() only take the instance out of KHook::Virtual's gate; the detour
// itself is removed by Metamod after Unload returns, asynchronously, through the plugin's
// KHookImpl. KHook::Virtual keeps its vtable-slot -> hook-id map until that removal reports
// back, and _Setup skips a slot that is still in the map. So a `meta load` right after
// `meta unload` (in one command line, or before the removal was processed) found every slot
// "already hooked" and installed nothing, then the pending removal took the old detour away:
// every virtual hook was dead (console commands, GameFrame, events...). Detach forgets the
// ids right away, so the next Add installs a fresh hook while the old one is still being
// removed; whichever of Detach's and Metamod's removals of an id runs second completes at once
// (KHook answers immediately for an id it no longer has).
inline void DetachAll()
{
    for (auto* detachable : Detachable::Registry())
        detachable->Detach();
}

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
template <class CLASS, class RETURN, class... ARGS> class Virtual : public KHook::Virtual<CLASS, RETURN, ARGS...>, public Detachable
{
    using Base = KHook::Virtual<CLASS, RETURN, ARGS...>;

  public:
    using Base::Base;

    void Detach() override
    {
        std::unordered_map<KHook::HookID_t, void*> ids;
        {
            std::lock_guard guard(this->_hooks_stored);
            ids.swap(this->_hook_ids_addr);
            this->_addr_hook_ids.clear();
        }
        // Asynchronous: `meta unload` runs inside our own ICvar::DispatchConCommand detour, and
        // a synchronous removal waits for the detour it is running in to exit (KHook only
        // try-locks the capsule and retries) - a watchdog timeout. The worker thread removes
        // these once the capsules are free; with the ids gone from the maps above, its
        // _KHook_RemovedHook callback finds nothing and leaves a re-added hook's entry alone.
        // Outside the lock: an id KHook no longer has calls _KHook_RemovedHook at once.
        for (auto& [id, address] : ids)
            ::KHook::RemoveHook(id, true);
    }

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
