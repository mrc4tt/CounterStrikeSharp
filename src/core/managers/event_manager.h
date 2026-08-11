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

#pragma once

class CUtlString;

#include <igameeventsystem.h>
#include <public/igameevents.h>

#include <map>
#include <mutex>
#include <stack>
#include <string>
#include <string_view>

#include "core/global_listener.h"
#include "core/globals.h"
#include "scripting/script_engine.h"

namespace counterstrikesharp {
class ScriptCallback;
class PluginFunction;
} // namespace counterstrikesharp

struct EventHook
{
    EventHook()
    {
        m_pPreHook = nullptr;
        m_pPostHook = nullptr;
    }
    counterstrikesharp::ScriptCallback* m_pPreHook;
    counterstrikesharp::ScriptCallback* m_pPostHook;
    std::string m_Name;
};

struct EventOverride
{
    bool m_bDontBroadcast;
};

struct PendingEventHook
{
    std::string m_Name;
    counterstrikesharp::CallbackT m_fnCallback;
    bool m_bPost;
};

namespace counterstrikesharp {

class EventManager : public IGameEventListener2, public GlobalClass
{
  public:
    EventManager();
    ~EventManager() override;

    // GlobalClass
    void OnShutdown() override;
    void OnAllInitialized() override;
    void OnAllInitialized_Post() override;
    void OnStartup() override;
    void OnGameLoopInitialized() override;

    // IGameEventListener2
    void FireGameEvent(IGameEvent* pEvent) override;

    bool UnhookEvent(const char* szName, CallbackT fnCallback, bool bPost);
    bool HookEvent(const char* szName, CallbackT fnCallback, bool bPost);

  private:
    bool OnFireEvent(IGameEvent* pEvent, bool bDontBroadcast);
    bool OnFireEventPost(IGameEvent* pEvent, bool bDontBroadcast);

    // Transparent comparator (std::less<>): OnFireEvent runs for EVERY game event
    // the engine fires -- bullet_impact, player_footstep, weapon_fire, ... -- many
    // times per tick, and it only ever has a `const char*` name in hand. With the
    // default std::less<std::string> every one of those lookups materialised a
    // temporary std::string first, which heap-allocates once the name exceeds the
    // 15-char SSO buffer (most CS2 event names do). is_transparent lets
    // find(std::string_view) compare in place: no temporary, no allocation.
    //
    // std::map and NOT std::unordered_map on purpose: heterogeneous lookup for the
    // unordered containers is C++20 (P0919) and libstdc++ only implements it from
    // GCC 11. The release build runs in the Steam Runtime sniper image, which ships
    // GCC 10.3 -- an unordered_map + is_transparent hash compiles locally on a newer
    // toolchain and then fails the Docker build with "no matching function for call
    // to ... find(std::string_view)". Transparent comparators on the ordered
    // containers are C++14 and work everywhere we build.
    std::map<std::string, EventHook*, std::less<>> m_hooksMap;

    std::stack<EventHook*> m_EventStack;
    std::stack<IGameEvent*> m_EventCopies;
    std::stack<PendingEventHook> m_PendingHooks;
    // Guards m_PendingHooks and the defer-vs-hook decision in HookEvent against the
    // OnGameLoopInitialized drain. Closes the TOCTOU race where HookEvent reads
    // gameLoopInitialized == false and pushes after the drain has already run.
    std::mutex m_PendingHooksMutex;
};

} // namespace counterstrikesharp
