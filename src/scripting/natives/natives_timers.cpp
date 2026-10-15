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

#include <scripting/autonative.h>
#include <scripting/script_engine.h>

#include "core/timer_system.h"

namespace counterstrikesharp {

// The managed Timer.Handle is the timer's serial, not its address. A pointer handle let a
// late Kill() (after the one-shot fired, or after a map change freed it) hit whatever timer
// the allocator had since put at that address, e.g. another plugin's.
void* CreateTimer(ScriptContext& script_context)
{
    auto interval = script_context.GetArgument<float>(0);
    auto callback = script_context.GetArgument<CallbackT>(1);
    auto flags = script_context.GetArgument<int>(2);

    auto timer = globals::timerSystem.CreateTimer(interval, callback, flags);
    return reinterpret_cast<void*>(static_cast<uintptr_t>(timer->m_serial));
}

void KillTimer(ScriptContext& script_context)
{
    auto serial = reinterpret_cast<uintptr_t>(script_context.GetArgument<void*>(0));
    globals::timerSystem.KillTimerBySerial(serial);
}

REGISTER_NATIVES(timers, {
    ScriptEngine::RegisterNativeHandler("CREATE_TIMER", CreateTimer);
    ScriptEngine::RegisterNativeHandler("KILL_TIMER", KillTimer);
})
} // namespace counterstrikesharp
