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

#include "core/gamedata_signature.h"

#include <map>
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
    static constexpr const char* kDefaultAutoUpdateURL = "https://sig.miksen.me/latest/CounterStrikeSharp/gamedata.json";
    std::string AutoUpdateURL = std::string(kDefaultAutoUpdateURL);
    // The AutoUpdateURL core.json named when it was upstream's feed (gamedata.cssharp.dev), which
    // the fork's gamedata does not match; AutoUpdateURL then holds the default instead. Empty
    // otherwise. Logged by update::TryUpdateGameConfig.
    std::string ReplacedAutoUpdateURL;
    std::string LogVerbosity = "information";
    // Plugin gamedata auto-update (gameconfig_updater.cpp, core/gamedata_plugin_update.h): swaps
    // signatures that do not resolve in gamedata/*.json (not plugins/*/gamedata/ copies) for the
    // feed's, from the same site as AutoUpdateURL. Needs AutoUpdateEnabled as well.
    bool PluginGamedataAutoUpdate = true;
    // Local file (path relative to addons/counterstrikesharp, or bare file name) -> feed file
    // ("<plugin>/<fileName>" under <site>/latest/), for files the updater cannot identify by
    // itself. An empty value turns the update off for that file.
    std::map<std::string, std::string> PluginGamedataFeeds;
    // Opt-in: tell VibeSignatures which plugin gamedata files this server runs (file names, key
    // names, sha256 and which keys don't resolve; never the file text), so files it does not
    // generate yet can get a generator. See update::ReportGamedata.
    bool GamedataReportEnabled = false;
    std::string GamedataReportURL = std::string("https://sig-api.miksen.me/api/report");
    // Opt-in, and unlike the report it sends file TEXT: a plugin gamedata file the report's answer
    // calls unknown is uploaded to POST /api/files on the GamedataReportURL host (https only), which
    // keeps it up to date per game build; the plugin updater then fixes that file's broken
    // signatures from the upload's read link (signatures only, never offsets: it is not signed).
    // Needs GamedataReportEnabled. See core/gamedata_upload.h.
    bool GamedataUploadEnabled = false;
    // Ed25519 public key(s) (base64, space- or comma-separated) the feed's latest/manifest.json.sig
    // must verify against before the updaters apply anything from it (core/gamedata_signature.h).
    // "" turns the check off, for a self-hosted feed; VibeSignatures' is signed.
    std::string GamedataSigningKey = std::string(update::signing::kDefaultPublicKeys);

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
