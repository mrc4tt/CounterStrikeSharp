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

#include "core/khook_verified.h"

#include "core/log.h"

namespace counterstrikesharp {
namespace hooks {

// Running totals for LogHookSummary(). Hooks are installed on the game thread only.
static int s_hooksInstalled = 0;
static int s_hooksFailed = 0;

void ReportHookRegistration(const char* name, void** vtable, int slot, bool ok, const char* reason)
{
    if (ok)
    {
        ++s_hooksInstalled;
        CSSHARP_CORE_DEBUG("Installed hook '{}' (vtable {}, slot {})", name, (void*)vtable, slot);
        return;
    }

    ++s_hooksFailed;

    // ERROR, not WARN: whatever this hook backs is now silently inactive, and the
    // symptom shows up far from here -- an event that never fires, a command that
    // stops working -- with nothing else in the log to connect it back.
    CSSHARP_CORE_ERROR("Failed to install hook '{}' (vtable {}, slot {}): {}", name, (void*)vtable, slot,
                       reason != nullptr ? reason : "unknown reason");
}

void LogHookSummary()
{
    // One Info line instead of one per hook: the per-hook list is at Debug, and every
    // failure already has its own ERROR line above.
    if (s_hooksFailed == 0) CSSHARP_CORE_INFO("Engine hooks installed: {}", s_hooksInstalled);
    else
        CSSHARP_CORE_ERROR("Engine hooks installed: {}, FAILED: {} (see errors above)", s_hooksInstalled, s_hooksFailed);
}

} // namespace hooks
} // namespace counterstrikesharp
