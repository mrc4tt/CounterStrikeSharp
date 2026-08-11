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

#include "core/managers/event_manager.h"

#include "core/log.h"
#include "scripting/callback_manager.h"
#include "vprof.h"

SH_DECL_HOOK2(IGameEventManager2, FireEvent, SH_NOATTRIB, 0, bool, IGameEvent*, bool);

namespace counterstrikesharp {

EventManager::EventManager() = default;

EventManager::~EventManager() = default;

void EventManager::OnStartup() {}

void EventManager::OnGameLoopInitialized()
{
    // Drain under the lock by moving the queue out, then hook with the lock released.
    // HookEvent re-acquires the same (non-recursive) mutex, so calling it while holding
    // the lock would deadlock. gameLoopInitialized is already true here, so the drained
    // hooks take the direct-hook path and are not re-deferred.
    std::stack<PendingEventHook> pending;
    {
        std::lock_guard<std::mutex> lock(m_PendingHooksMutex);
        std::swap(pending, m_PendingHooks);
    }

    while (!pending.empty())
    {
        const auto& pendingHook = pending.top();
        HookEvent(pendingHook.m_Name.c_str(), pendingHook.m_fnCallback, pendingHook.m_bPost);
        pending.pop();
    }
}

void EventManager::OnAllInitialized() {}

void EventManager::OnAllInitialized_Post()
{
    SH_ADD_HOOK(IGameEventManager2, FireEvent, globals::gameEventManager, SH_MEMBER(this, &EventManager::OnFireEvent), false);
    SH_ADD_HOOK(IGameEventManager2, FireEvent, globals::gameEventManager, SH_MEMBER(this, &EventManager::OnFireEventPost), true);
}

void EventManager::OnShutdown()
{
    SH_REMOVE_HOOK(IGameEventManager2, FireEvent, globals::gameEventManager, SH_MEMBER(this, &EventManager::OnFireEvent), false);
    SH_REMOVE_HOOK(IGameEventManager2, FireEvent, globals::gameEventManager, SH_MEMBER(this, &EventManager::OnFireEventPost), true);

    globals::gameEventManager->RemoveListener(this);
}

void EventManager::FireGameEvent(IGameEvent* pEvent) {}

bool EventManager::HookEvent(const char* szName, CallbackT fnCallback, bool bPost)
{
    EventHook* pHook;

    // Plugin load is called before game loop (and thus events file is loaded)
    // So we defer hooking until game loop is initialized. The check and push must be
    // atomic with respect to the OnGameLoopInitialized drain, otherwise a hook pushed
    // after the drain ran would never be processed. Lock is released before the actual
    // hooking work below so the re-entrant call from the drain does not deadlock.
    {
        std::lock_guard<std::mutex> lock(m_PendingHooksMutex);
        if (!globals::gameLoopInitialized)
        {
            const PendingEventHook pendingHook{ szName, fnCallback, bPost };
            m_PendingHooks.push(pendingHook);
            return true;
        }
    }

    CSSHARP_CORE_TRACE("[EventManager] Hooking event: {0} with callback pointer: {1}", szName, (void*)fnCallback);

    if (!globals::gameEventManager->FindListener(this, szName))
    {
        globals::gameEventManager->AddListener(this, szName, true);
    }

    auto search = m_hooksMap.find(std::string_view(szName));
    // If hook struct is not found
    if (search == m_hooksMap.end())
    {
        pHook = new EventHook();

        if (bPost)
        {
            pHook->m_pPostHook = globals::callbackManager.CreateCallback(szName);
            pHook->m_pPostHook->AddListener(fnCallback);
        }
        else
        {
            pHook->m_pPreHook = globals::callbackManager.CreateCallback(szName);
            pHook->m_pPreHook->AddListener(fnCallback);
        }

        pHook->m_Name = std::string(szName);

        m_hooksMap[szName] = pHook;

        return true;
    }
    else
    {
        pHook = search->second;
    }

    if (bPost)
    {
        if (!pHook->m_pPostHook)
        {
            pHook->m_pPostHook = globals::callbackManager.CreateCallback("");
        }

        pHook->m_pPostHook->AddListener(fnCallback);
    }
    else
    {
        if (!pHook->m_pPreHook)
        {
            pHook->m_pPreHook = globals::callbackManager.CreateCallback("");
        }

        pHook->m_pPreHook->AddListener(fnCallback);
    }

    return true;
}

bool EventManager::UnhookEvent(const char* szName, CallbackT fnCallback, bool bPost)
{
    EventHook* pHook;
    ScriptCallback* pCallback;

    auto search = m_hooksMap.find(std::string_view(szName));
    if (search == m_hooksMap.end())
    {
        return false;
    }

    pHook = search->second;

    if (bPost)
    {
        pCallback = pHook->m_pPostHook;
    }
    else
    {
        pCallback = pHook->m_pPreHook;
    }

    pCallback->RemoveListener(fnCallback);

    if (pCallback->GetFunctionCount() == 0)
    {
        globals::callbackManager.ReleaseCallback(pCallback);

        if (bPost)
        {
            pHook->m_pPostHook = nullptr;
        }
        else
        {
            pHook->m_pPreHook = nullptr;
        }
    }

    CSSHARP_CORE_TRACE("Unhooking event: {0} with callback pointer: {1}", szName, (void*)fnCallback);

    return true;
}

bool EventManager::OnFireEvent(IGameEvent* pEvent, bool bDontBroadcast)
{
    if (!pEvent)
    {
        RETURN_META_VALUE(MRES_IGNORED, false);
    }

    const char* szName = pEvent->GetName();
    bool bLocalDontBroadcast = bDontBroadcast;
    auto I = m_hooksMap.find(std::string_view(szName));

    if (I != m_hooksMap.end())
    {
        auto pEventHook = I->second;
        m_EventStack.push(pEventHook);
        auto* pCallback = pEventHook->m_pPreHook;
        // A duplicate is only ever consumed by the post hook (OnFireEventPost reads
        // m_EventCopies.top() iff m_pPostHook is set). Previously the copy was pushed
        // unconditionally but popped/freed only when a post hook existed, so any event
        // with a pre-hook but no post-hook leaked one IGameEvent per fire. Gate the
        // duplicate on the post hook: fixes the leak and skips the alloc when unused.
        const bool bHasPostHook = pEventHook->m_pPostHook != nullptr;

        if (pCallback)
        {
            CSSHARP_CORE_TRACE("Pushing event `{}` pointer: {}, dont broadcast: {}, post: {}", szName, (void*)pEvent, bDontBroadcast,
                               false);
            EventOverride override = { bDontBroadcast };
            pCallback->Reset();
            pCallback->ScriptContext().Push(pEvent);
            pCallback->ScriptContext().Push(&override);

            // VPROF_BUDGET("CS#::OnFireEvent", "CS# Event Hooks");
            for (auto fnMethodToCall : pCallback->GetFunctions())
            {
                if (!fnMethodToCall) continue;
                fnMethodToCall(&pCallback->ScriptContextStruct());

                auto result = pCallback->ScriptContext().GetResult<HookResult>();
                bLocalDontBroadcast = override.m_bDontBroadcast;

                if (result >= HookResult::Handled)
                {
                    // Keep m_EventCopies symmetric with the non-null m_EventStack frame
                    // pushed above: push exactly one entry (the duplicate only when a post
                    // hook will consume it, else nullptr). OnFireEventPost pops one entry
                    // per non-null frame, so push/pop always balance regardless of whether
                    // a post hook is added mid-fire.
                    m_EventCopies.push(bHasPostHook ? globals::gameEventManager->DuplicateEvent(pEvent) : nullptr);
                    globals::gameEventManager->FreeEvent(pEvent);
                    RETURN_META_VALUE(MRES_SUPERCEDE, false);
                }
            }
        }
        m_EventCopies.push(bHasPostHook ? globals::gameEventManager->DuplicateEvent(pEvent) : nullptr);
    }
    else
    {
        m_EventStack.push(nullptr);
    }

    if (bLocalDontBroadcast != bDontBroadcast)
    {
        RETURN_META_VALUE_NEWPARAMS(MRES_IGNORED, true, &IGameEventManager2::FireEvent, (pEvent, bLocalDontBroadcast));
    }

    RETURN_META_VALUE(MRES_IGNORED, true);
}

bool EventManager::OnFireEventPost(IGameEvent* pEvent, bool bDontBroadcast)
{
    if (!pEvent)
    {
        RETURN_META_VALUE(MRES_IGNORED, false);
    }

    auto pHook = m_EventStack.top();

    if (pHook)
    {
        // One m_EventCopies entry was pushed for this frame in OnFireEvent (nullptr when
        // no post hook existed). Pop it unconditionally so the stack never desyncs, then
        // run the post callback / free the copy only if there is one.
        IGameEvent* pEventCopy = m_EventCopies.top();
        m_EventCopies.pop();

        auto* pCallback = pHook->m_pPostHook;

        if (pCallback && pEventCopy)
        {
            // VPROF_BUDGET("CS#::OnFireEventPost", "CS# Event Hooks");

            CSSHARP_CORE_TRACE("Pushing event `{}` pointer: {}, dont broadcast: {}, post: {}", pEventCopy->GetName(), (void*)pEventCopy,
                               bDontBroadcast, true);
            EventOverride override = { bDontBroadcast };
            pCallback->Reset();
            pCallback->ScriptContext().Push(pEventCopy);
            pCallback->ScriptContext().Push(&override);
            pCallback->Execute();
        }

        if (pEventCopy)
        {
            globals::gameEventManager->FreeEvent(pEventCopy);
        }
    }

    m_EventStack.pop();

    RETURN_META_VALUE(MRES_IGNORED, true);
}
} // namespace counterstrikesharp
