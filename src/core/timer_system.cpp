/**
 * =============================================================================
 * SourceMod
 * Copyright (C) 2004-2016 AlliedModders LLC.  All rights reserved.
 * =============================================================================
 *
 * This program is free software; you can redistribute it and/or modify it under
 * the terms of the GNU General Public License, version 3.0, as published by the
 * Free Software Foundation.
 *
 * This program is distributed in the hope that it will be useful, but WITHOUT
 * ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
 * FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more
 * details.
 *
 * You should have received a copy of the GNU General Public License along with
 * this program.  If not, see <http://www.gnu.org/licenses/>.
 *
 * As a special exception, AlliedModders LLC gives you permission to link the
 * code of this program (as well as its derivative works) to "Half-Life 2," the
 * "Source Engine," the "SourcePawn JIT," and any Game MODs that run on software
 * by the Valve Corporation.  You must obey the GNU General Public License in
 * all respects for all other code used.  Additionally, AlliedModders LLC grants
 * this exception to all derivative works.  AlliedModders LLC defines further
 * exceptions, found in LICENSE.txt (as of this writing, version JULY-31-2007),
 * or <http://www.sourcemod.net/license.php>.
 *
 * This file has been modified from its original form, under the GNU General
 * Public License, version 3.0.
 */

#include "core/timer_system.h"

#include <engine2/isource2server.h>
#include <engine2/ivengineserver2.h>

#include <algorithm>
#include <unordered_map>

#include "core/globals.h"
#include "core/log.h"
#include "core/managers/player_manager.h"
#include "core/timer_system_startup_logic.h"
#include "scripting/callback_manager.h"

namespace counterstrikesharp {
namespace timers {
double universal_time = 0.0f;
double timer_next_think = 0.0f;
} // namespace timers

// Live timers by serial. Maintained by the Timer ctor/dtor so every delete path (fired
// one-shot, KillTimer, RemoveMapChangeTimers) drops its entry.
static std::unordered_map<uint64_t, timers::Timer*>& TimersBySerial()
{
    static std::unordered_map<uint64_t, timers::Timer*> timers;
    return timers;
}

static uint64_t g_next_timer_serial = 1;

timers::Timer::Timer(float interval, float exec_time, CallbackT callback, int flags)
    : m_serial(g_next_timer_serial++), m_interval(interval), m_exec_time(exec_time), m_flags(flags), m_kill_me(false), m_in_exec(false)
{
    m_callback = globals::callbackManager.CreateCallback("Timer");
    m_callback->AddListener(callback);
    TimersBySerial()[m_serial] = this;
}

timers::Timer::~Timer()
{
    TimersBySerial().erase(m_serial);
    globals::callbackManager.ReleaseCallback(m_callback);
}

TimerSystem::TimerSystem()
{
    m_has_map_ticked = false;
    m_has_map_simulated = false;
    m_last_ticked_time = 0.0f;
}

void TimerSystem::OnAllInitialized() { m_on_tick_callback_ = globals::callbackManager.CreateCallback("OnTick"); }
void TimerSystem::OnShutdown()
{
    globals::callbackManager.ReleaseCallback(m_on_tick_callback_);
    m_on_tick_callback_ = nullptr;

    // `meta unload`: plugins kill their own timers while they unload, but anything left would
    // otherwise fire after a later `meta load` into callbacks of the previous load. ~Timer drops
    // the serial, so a stale managed Kill of one of these is a no-op.
    for (auto* timer : m_once_off_timers)
        delete timer;
    for (auto* timer : m_repeat_timers)
        delete timer;
    m_once_off_timers.clear();
    m_repeat_timers.clear();
}

void TimerSystem::OnLevelEnd()
{
    globals::timerSystem.RemoveMapChangeTimers();

    m_has_map_simulated = false;
    m_has_map_ticked = false;
}

void TimerSystem::OnStartupServer(bool levelShutdown)
{
    // Pure decision logic lives in timer_system_startup_logic.h so it can be
    // unit-tested without linking against this .cpp (which pulls in
    // hl2sdk-cs2, spdlog, etc.). The helper ALWAYS resets the tick flags --
    // see the header for the workshop-ss_dead-reload rationale.
    if (timer_startup::ShouldFireLevelEndAndResetTickState(levelShutdown, m_has_map_ticked, m_has_map_simulated))
    {
        CALL_GLOBAL_LISTENER(OnLevelEnd());

        CSSHARP_CORE_TRACE("name={0}", "LevelShutdown");
    }
}

void TimerSystem::OnGameFrame(bool simulating)
{
    if (simulating && m_has_map_ticked)
    {
        timers::universal_time += globals::getGlobalVars()->curtime - m_last_ticked_time;
        if (!m_has_map_simulated)
        {
            m_has_map_simulated = true;
        }
    }
    else
    {
        timers::universal_time += globals::engine_fixed_tick_interval;
    }

    m_last_ticked_time = globals::getGlobalVars()->curtime;
    m_has_map_ticked = true;

    RunFrame();

    if (m_on_tick_callback_->GetFunctionCount())
    {
        m_on_tick_callback_->ScriptContext().Reset();
        m_on_tick_callback_->Execute();
    }

    globals::playerManager.RunAuthChecks();
}

double TimerSystem::CalculateNextThink(double last_think_time, float interval)
{
    if (timers::universal_time - last_think_time - interval <= globals::engine_fixed_tick_interval)
    {
        return last_think_time + interval;
    }
    return timers::universal_time + interval;
}

void TimerSystem::RunFrame()
{
    // The execute phase never erases or deletes: a callback may kill any timer (itself, an older
    // or a newer one) or create new ones, and KillTimer only flags it. Erasing mid-walk shifted
    // the vector under the loop index, so a repeat timer could be visited twice in one frame and
    // fire twice when its interval is shorter than a tick; finding each fired timer again by value
    // also cost a full scan per timer. SweepKilledTimers deletes everything flagged afterwards.
    // The sizes are captured first, so a timer created inside a callback fires on a later frame.
    const size_t once_off_count = m_once_off_timers.size();
    for (size_t i = once_off_count; i-- > 0;)
    {
        auto timer = m_once_off_timers[i];
        if (timer->m_kill_me || timers::universal_time < timer->m_exec_time) continue;

        timer->m_in_exec = true;
        timer->m_callback->ScriptContext().Reset();
        timer->m_callback->Execute();
        timer->m_in_exec = false;
        timer->m_kill_me = true;
    }

    const size_t repeat_count = m_repeat_timers.size();
    for (size_t i = repeat_count; i-- > 0;)
    {
        auto timer = m_repeat_timers[i];
        if (timer->m_kill_me || timers::universal_time < timer->m_exec_time) continue;

        timer->m_in_exec = true;
        timer->m_callback->ScriptContext().Reset();
        timer->m_callback->Execute();
        timer->m_in_exec = false;

        if (!timer->m_kill_me)
        {
            timer->m_exec_time = CalculateNextThink(timer->m_exec_time, timer->m_interval);
        }
    }

    SweepKilledTimers();
}

void TimerSystem::SweepKilledTimers()
{
    auto isKilled = [](timers::Timer* timer) {
        if (!timer->m_kill_me) return false;
        delete timer;
        return true;
    };

    std::erase_if(m_once_off_timers, isKilled);
    std::erase_if(m_repeat_timers, isKilled);
}

void TimerSystem::RemoveMapChangeTimers()
{
    auto isMapChangeTimer = [](timers::Timer* timer) {
        bool shouldRemove = timer->m_kill_me || (timer->m_flags & TIMER_FLAG_NO_MAPCHANGE);
        if (shouldRemove)
        {
            delete timer;
        }
        return shouldRemove;
    };

    std::erase_if(m_once_off_timers, isMapChangeTimer);
    std::erase_if(m_repeat_timers, isMapChangeTimer);
}

timers::Timer* TimerSystem::CreateTimer(float interval, CallbackT callback, int flags)
{
    float exec_time = timers::universal_time + interval;

    auto timer = new timers::Timer(interval, exec_time, callback, flags);

    if (flags & TIMER_FLAG_REPEAT)
    {
        m_repeat_timers.push_back(timer);
        return timer;
    }

    m_once_off_timers.push_back(timer);
    return timer;
}

void TimerSystem::KillTimer(timers::Timer* timer)
{
    // Only flagged: the timer never runs again and SweepKilledTimers (end of RunFrame) or
    // RemoveMapChangeTimers deletes it. Deleting here shifted the vectors RunFrame was walking
    // when the kill came from inside another timer's callback. The serial stays registered until
    // the delete, so a repeated Kill of the same handle just finds the flag already set.
    if (!timer) return;
    timer->m_kill_me = true;
}

void TimerSystem::KillTimerBySerial(uint64_t serial)
{
    auto it = TimersBySerial().find(serial);
    if (it == TimersBySerial().end()) return;
    KillTimer(it->second);
}

double TimerSystem::GetTickedTime() { return timers::universal_time; }
} // namespace counterstrikesharp
