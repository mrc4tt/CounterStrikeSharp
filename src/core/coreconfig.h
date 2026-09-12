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

#include <nlohmann/json.hpp>

#include <string>

#include "core/globals.h"

namespace counterstrikesharp {

class CCoreConfig
{
  public:
    std::vector<std::string> PublicChatTrigger = { std::string("!") };
    std::vector<std::string> SilentChatTrigger = { std::string("/") };
    bool FollowCS2ServerGuidelines = true;
    bool PluginHotReloadEnabled = true;
    bool PluginAutoLoadEnabled = true;
    std::string ServerLanguage = "en";
    bool UnlockConCommands = true;
    bool UnlockConVars = true;
    bool AutoUpdateEnabled = true;
    std::string AutoUpdateURL = std::string("http://gamedata.cssharp.dev");
    std::string LogVerbosity = "information";

    // Crash dumps are ON by default. The variables that drive them are set on the
    // process before the .NET runtime boots (mm_plugin), so no launch-wrapper edit
    // and no per-server setup is needed -- which matters when the fleet is large
    // enough that "just set an env var everywhere" is the expensive part.
    bool CrashDumpsEnabled = true;
    // 1=Mini, 2=Heap, 3=Triage, 4=Full. Heap keeps `dumpheap`/`gcroot` working
    // without the multi-GB size of a full dump of a CS2 server.
    int CrashDumpType = 2;
    // Dumps are large and crashes repeat. Keep the newest N and delete the rest at
    // startup, so a crash loop cannot fill the disk. 0 disables pruning.
    int CrashDumpRetention = 5;

    using json = nlohmann::json;
    CCoreConfig(const std::string& path);
    ~CCoreConfig();

    bool Init(char* conf_error, int conf_error_size);
    const std::string GetPath() const;

    bool IsSilentChatTrigger(const std::string& message, std::string& prefix) const;
    bool IsPublicChatTrigger(const std::string& message, std::string& prefix) const;

  private:
    bool IsTriggerInternal(std::vector<std::string> triggers, const std::string& message, std::string& prefix) const;

  private:
    std::string m_sPath;
    json m_json;
};

} // namespace counterstrikesharp
