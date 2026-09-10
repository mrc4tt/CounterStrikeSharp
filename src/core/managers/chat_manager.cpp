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

#include <igameevents.h>
#include <public/eiface.h>

#include "characterset.h"
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

    m_hostSayHook = std::make_unique<decltype(m_hostSayHook)::element_type>(&OnHostSay, &OnHostSayPost);
    m_hostSayHook->Configure((void*)m_pHostSay);

    on_player_chat_callback = globals::callbackManager.CreateCallback("OnPlayerChat");
}

void ChatManager::OnShutdown() { globals::callbackManager.ReleaseCallback(on_player_chat_callback); }

void ChatManager::RemoveDetours() { m_hostSayHook.reset(); }

namespace {
// Trigger classification computed in the pre callback and consumed by the post
// callback.
//
// The funchook version was one function: classify, conditionally call the original,
// then do the command/chat work. KHook calls the original for us, so the "before" and
// "after" halves are now two callbacks and the classification has to be carried
// between them -- recomputing it in post would re-read args after the engine has had
// them. Kept as a stack purely to stay correct if a chat message ever nests.
struct HostSayFrame
{
    std::string prefix;
    bool isCommand;
};

thread_local std::vector<HostSayFrame> s_hostSayFrames;
} // namespace

KHook::Return<void> OnHostSay(CEntityInstance* pController, CCommand& args, bool teamonly, int unk1, const char* unk2)
{
    std::string prefix;
    bool bSilent = globals::coreConfig->IsSilentChatTrigger(args[1], prefix);
    bool bCommand = globals::coreConfig->IsPublicChatTrigger(args[1], prefix) || bSilent;

    s_hostSayFrames.push_back({ prefix, bCommand });

    // A silent trigger must not reach the engine, or the "!command" shows up in
    // everyone's chat. Superseding is the KHook equivalent of the funchook version
    // simply not calling the original.
    if (bSilent)
    {
        return { KHook::Action::Supersede };
    }

    return { KHook::Action::Ignore };
}

KHook::Return<void> OnHostSayPost(CEntityInstance* pController, CCommand& args, bool teamonly, int unk1, const char* unk2)
{
    // Post runs even when the pre superseded, so the frame is always here to pop.
    // Guard anyway rather than indexing an empty vector.
    if (s_hostSayFrames.empty())
    {
        return { KHook::Action::Ignore };
    }

    const HostSayFrame frame = std::move(s_hostSayFrames.back());
    s_hostSayFrames.pop_back();

    const std::string& prefix = frame.prefix;

    if (frame.isCommand)
    {
        // Messagemode (typing in the chat box) wraps the whole message in
        // surrounding quotes, but `say`/`say_team` invoked from a key bind
        // (e.g. bind c "say !throw") does not. The old code assumed the quote
        // via a hardcoded `+ 1`, so the bind path lost its first real char
        // (`!throw` -> `hrow` -> css_hrow, an invalid command that silently
        // no-ops). Strip an optional leading/trailing quote instead so both
        // paths parse identically.
        //
        // ArgS() can return nullptr on a malformed/empty say; constructing a
        // std::string from nullptr is UB (segfault). Guard it — the old raw
        // pointer-arithmetic path tolerated a null here, so preserve that.
        const char* argS = args.ArgS();
        std::string message = argS ? argS : "";
        if (!message.empty() && message.front() == '"')
        {
            message.erase(0, 1);
        }
        if (!message.empty() && message.back() == '"')
        {
            message.pop_back();
        }

        // Drop the trigger prefix (e.g. "!" or "/").
        message.erase(0, prefix.length());

        CCommand cmd;
        cmd.Tokenize(message.c_str());

        auto prefixedPhrase = std::string("css_") + cmd.Arg(0);
        auto bValidWithPrefix = globals::conCommandManager.IsValidValveCommand(prefixedPhrase.c_str());

        if (bValidWithPrefix)
        {
            // Re-tokenize with a `css_` prefix if we have found that its a valid command.
            cmd.Tokenize(("css_" + message).c_str());
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
            callback->ScriptContext().Push(args.Arg(1));
            callback->ScriptContext().Push(teamonly);
            callback->Execute();
        }

        auto pEvent = globals::gameEventManager->CreateEvent("player_chat", true);
        if (pEvent)
        {
            pEvent->SetBool("teamonly", teamonly);
            pEvent->SetInt("userid", pController->GetEntityIndex().Get() - 1);
            pEvent->SetString("text", args[1]);

            globals::gameEventManager->FireEvent(pEvent, false);
        }
    }

    return { KHook::Action::Ignore };
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
