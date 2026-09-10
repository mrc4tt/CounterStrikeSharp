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

#include <map>
#include <memory>
#include <vector>

#include "core/global_listener.h"
#include "core/globals.h"
#include "scripting/script_engine.h"

namespace counterstrikesharp {
class ScriptCallback;

typedef void (*HostSay)(CEntityInstance*, CCommand&, bool, int, const char*);

class ChatCommandInfo
{
    friend class ChatManager;

  public:
    ChatCommandInfo() {}

  public:
    ScriptCallback* GetCallback() { return callback_pre; }

  private:
    std::string command;
    ScriptCallback* callback_pre;
    ScriptCallback* callback_post;
};

class ChatManager : public GlobalClass
{
  public:
    ChatManager();
    ~ChatManager();
    void OnAllInitialized() override;
    void OnShutdown() override;
    // Removes the Host_Say detour before this plugin's .so is unloaded on Metamod
    // unload, so a later chat message does not reach our freed callbacks (crash).
    void RemoveDetours();

    bool OnSayCommandPre(CEntityInstance* pController, CCommand& args);
    void OnSayCommandPost(CEntityInstance* pController, CCommand& args);

    ScriptCallback* on_player_chat_callback = nullptr;

  private:
    void InternalDispatch(CEntityInstance* pPlayerController, const char* szTriggerPhrase, CCommand& pFullCommand);

    std::vector<ChatCommandInfo*> m_cmd_list;
    std::map<std::string, ChatCommandInfo*> m_cmd_lookup;

    // KHook detour on Host_Say. Held by pointer so RemoveDetours() can destroy it (and
    // with it, remove the hook) on unload.
    std::unique_ptr<KHook::Function<void, CEntityInstance*, CCommand&, bool, int, const char*>> m_hostSayHook;
};

KHook::Return<void> OnHostSay(CEntityInstance* pController, CCommand& args, bool teamonly, int unk1, const char* unk2);
KHook::Return<void> OnHostSayPost(CEntityInstance* pController, CCommand& args, bool teamonly, int unk1, const char* unk2);
static HostSay m_pHostSay = nullptr;

} // namespace counterstrikesharp
