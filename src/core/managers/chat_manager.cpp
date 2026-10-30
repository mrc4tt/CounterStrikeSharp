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

#include "core/managers/chat_manager.h"

#include <algorithm>

#include <igameevents.h>
#include <engine2/isource2server.h>
#include <engine2/ivengineserver2.h>

#include "core/coreconfig.h"
#include "core/gameconfig.h"
#include "core/log.h"
#include "core/managers/con_command_manager.h"
#include "core/memory.h"
#include "core/memory_module.h"
#include "scripting/callback_manager.h"

namespace counterstrikesharp {

ChatManager::ChatManager() {}

ChatManager::~ChatManager() {}

void ChatManager::OnAllInitialized()
{
    m_pHostSay = reinterpret_cast<HostSay>(modules::server->FindSignature(globals::gameConfig->GetSignature("Host_Say")));

    if (m_pHostSay == nullptr)
    {
        CSSHARP_CORE_ERROR("Failed to find signature for \'Host_Say\'");
        return;
    }

    m_hooks.AddFunction(reinterpret_cast<void*>(m_pHostSay), &DetourHostSay);

    on_player_chat_callback = globals::callbackManager.CreateCallback("OnPlayerChat");
}

void ChatManager::OnShutdown()
{
    m_hooks.Clear();
    globals::callbackManager.ReleaseCallback(on_player_chat_callback);
}

KHook::Return<void> DetourHostSay(CEntityInstance* pController, CCommand& args, bool teamonly, int unk1, const char* unk2)
{
    // Messagemode (typing in the chat box) wraps the whole message in surrounding quotes, but
    // `say`/`say_team` from a key bind (bind c "say !throw arg") does not, and its args[1] is only
    // the first word. Take the text from ArgS() with an optional leading/trailing quote stripped so
    // both paths parse identically. Copied before Host_Say runs, which edits the buffer in place.
    std::string message = args.ArgS();
    if (!message.empty() && message.front() == '"')
    {
        message.erase(0, 1);
    }
    if (!message.empty() && message.back() == '"')
    {
        message.pop_back();
    }

    std::string prefix;
    bool bSilent = globals::coreConfig->IsSilentChatTrigger(args[1], prefix);
    bool bCommand = globals::coreConfig->IsPublicChatTrigger(args[1], prefix) || bSilent;

    if (!bSilent)
    {
        KHook::Recall(m_pHostSay, KHook::Return<void>{ KHook::Action::Ignore }, pController, args, teamonly, unk1, unk2);
    }

    if (bCommand)
    {
        // Drop the trigger prefix (e.g. "!" or "/"). The old `ArgS() + prefix.length() + 1` assumed the
        // messagemode quote, so the bind path lost its first real char (!throw -> css_hrow).
        std::string commandText = message.substr(std::min(prefix.length(), message.length()));

        CCommand cmd;
        cmd.Tokenize(commandText.c_str());

        auto prefixedPhrase = std::string("css_") + cmd.Arg(0);
        auto bValidWithPrefix = globals::conCommandManager.IsValidValveCommand(prefixedPhrase.c_str());

        if (bValidWithPrefix)
        {
            // Re-tokenize with a `css_` prefix if we have found that its a valid command.
            cmd.Tokenize(("css_" + commandText).c_str());
        }

        globals::chatManager.OnSayCommandPost(pController, cmd);
    }

    if (pController)
    {
        auto callback = globals::chatManager.on_player_chat_callback;

        if (callback && callback->GetFunctionCount())
        {
            callback->ScriptContext().Reset();
            callback->ScriptContext().Push(pController);
            callback->ScriptContext().Push(message.c_str());
            callback->ScriptContext().Push(teamonly);
            callback->Execute();
        }

        auto pEvent = globals::gameEventManager->CreateEvent("player_chat", true);
        if (pEvent)
        {
            pEvent->SetBool("teamonly", teamonly);
            pEvent->SetInt("userid", pController->GetEntityIndex().Get() - 1);
            pEvent->SetString("text", message.c_str());

            globals::gameEventManager->FireEvent(pEvent, false);
        }
    }
    return { bSilent ? KHook::Action::Supersede : KHook::Action::Ignore };
}

bool ChatManager::OnSayCommandPre(CEntityInstance* pController, CCommand& command) { return false; }

void ChatManager::OnSayCommandPost(CEntityInstance* pController, CCommand& command)
{
    auto commandStr = command.Arg(0);

    return InternalDispatch(pController, commandStr, command);
}

void ChatManager::InternalDispatch(CEntityInstance* pPlayerController, const char* szTriggerPhase, CCommand& fullCommand)
{
    if (pPlayerController == nullptr)
    {
        globals::conCommandManager.ExecuteCommandCallbacks(fullCommand.Arg(0),
                                                           CCommandContext(CommandTarget_t::CT_NO_TARGET, CPlayerSlot(-1)), fullCommand,
                                                           HookMode::Pre, CommandCallingContext::Chat);
        return;
    }

    auto index = pPlayerController->GetEntityIndex().Get();
    auto slot = CPlayerSlot(index - 1);

    globals::conCommandManager.ExecuteCommandCallbacks(fullCommand.Arg(0), CCommandContext(CommandTarget_t::CT_NO_TARGET, slot),
                                                       fullCommand, HookMode::Pre, CommandCallingContext::Chat);
}
} // namespace counterstrikesharp
