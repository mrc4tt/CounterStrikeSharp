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

#include "core/coreconfig.h"

#include <algorithm>
#include <cctype>
#include <fstream>

#include "core/log.h"

namespace counterstrikesharp {

namespace {

// http(s)://gamedata.cssharp.dev[/...], upstream's gamedata feed and the old AutoUpdateURL default.
bool IsUpstreamGamedataUrl(std::string url)
{
    std::transform(url.begin(), url.end(), url.begin(), [](unsigned char c) {
        return static_cast<char>(std::tolower(c));
    });
    for (const char* prefix : { "http://gamedata.cssharp.dev", "https://gamedata.cssharp.dev" })
    {
        const std::string p(prefix);
        if (url.compare(0, p.size(), p) == 0 && (url.size() == p.size() || url[p.size()] == '/' || url[p.size()] == ':')) return true;
    }
    return false;
}

} // namespace

CCoreConfig::CCoreConfig(const std::string& path) { m_sPath = path; }

CCoreConfig::~CCoreConfig() = default;

bool CCoreConfig::Init(char* conf_error, int conf_error_size)
{
    std::ifstream ifs(std::string(m_sPath + ".json"));

    if (!ifs)
    {
        std::ifstream exampleIfs(std::string(m_sPath + ".example.json"));

        if (!exampleIfs)
        {
            V_snprintf(conf_error, conf_error_size, "CoreConfig file not found.");
            return false;
        }

        CSSHARP_CORE_INFO("CoreConfig file not found, creating one from example.");
        std::ofstream ofs(std::string(m_sPath + ".json"));
        ofs << exampleIfs.rdbuf();
        ofs.close();

        // An unwritable configs dir used to recurse here until the stack overflowed.
        if (!ofs)
        {
            V_snprintf(conf_error, conf_error_size, "CoreConfig file not found and could not be created from the example.");
            return false;
        }

        return Init(conf_error, conf_error_size);
    }

    try
    {
        // Comments allowed, matching the managed reader (JsonCommentHandling.Skip): a commented
        // core.json used to throw here, outside the try, and fail the whole plugin load.
        m_json = json::parse(ifs, nullptr, true, true);

        PublicChatTrigger = m_json.value("PublicChatTrigger", PublicChatTrigger);
        SilentChatTrigger = m_json.value("SilentChatTrigger", SilentChatTrigger);
        FollowCS2ServerGuidelines = m_json.value("FollowCS2ServerGuidelines", FollowCS2ServerGuidelines);
        PluginHotReloadEnabled = m_json.value("PluginHotReloadEnabled", PluginHotReloadEnabled);
        PluginAutoLoadEnabled = m_json.value("PluginAutoLoadEnabled", PluginAutoLoadEnabled);
        ServerLanguage = m_json.value("ServerLanguage", ServerLanguage);
        UnlockConCommands = m_json.value("UnlockConCommands", UnlockConCommands);
        UnlockConVars = m_json.value("UnlockConVars", UnlockConVars);
        AutoUpdateEnabled = m_json.value("AutoUpdateEnabled", AutoUpdateEnabled);
        AutoUpdateURL = m_json.value("AutoUpdateURL", AutoUpdateURL);
        if (IsUpstreamGamedataUrl(AutoUpdateURL))
        {
            // Every core.json written before the fork moved to VibeSignatures carries upstream's
            // feed. Its gamedata is upstream's, not this fork's, so the regression check would
            // reject it at best; point such configs at the fork's feed. AutoUpdateEnabled is
            // still the owner's call.
            ReplacedAutoUpdateURL = AutoUpdateURL;
            AutoUpdateURL = kDefaultAutoUpdateURL;
        }
        LogVerbosity = m_json.value("LogVerbosity", LogVerbosity);
        PluginGamedataAutoUpdate = m_json.value("PluginGamedataAutoUpdate", PluginGamedataAutoUpdate);
        GamedataCrashGuard = m_json.value("GamedataCrashGuard", GamedataCrashGuard);
        GamedataReportEnabled = m_json.value("GamedataReportEnabled", GamedataReportEnabled);
        GamedataReportURL = m_json.value("GamedataReportURL", GamedataReportURL);
        GamedataUploadEnabled = m_json.value("GamedataUploadEnabled", GamedataUploadEnabled);
        if (m_json.contains("GamedataSigningKey") && m_json["GamedataSigningKey"].is_null()) GamedataSigningKey.clear();
        else
            GamedataSigningKey = m_json.value("GamedataSigningKey", GamedataSigningKey);
        if (m_json.contains("PluginGamedataFeeds") && m_json["PluginGamedataFeeds"].is_object())
        {
            for (const auto& [file, feed] : m_json["PluginGamedataFeeds"].items())
                if (feed.is_string()) PluginGamedataFeeds[file] = feed.get<std::string>();
                else if (feed.is_null())
                    PluginGamedataFeeds[file] = "";
        }
    }
    catch (const std::exception& ex)
    {
        V_snprintf(conf_error, conf_error_size, "Failed to parse CoreConfig file: %s", ex.what());
        return false;
    }

    return true;
}

const std::string CCoreConfig::GetPath() const { return m_sPath; }

bool CCoreConfig::IsTriggerInternal(std::vector<std::string> triggers, const std::string& message, std::string& prefix) const
{
    for (std::string& trigger : triggers)
    {
        if (message.rfind(trigger, 0) == 0)
        {
            prefix = trigger;
            CSSHARP_CORE_TRACE("Trigger found, prefix is {}", prefix);
            return true;
        }
    }

    return false;
}

bool CCoreConfig::IsSilentChatTrigger(const std::string& message, std::string& prefix) const
{
    return IsTriggerInternal(SilentChatTrigger, message, prefix);
}

bool CCoreConfig::IsPublicChatTrigger(const std::string& message, std::string& prefix) const
{
    return IsTriggerInternal(PublicChatTrigger, message, prefix);
}
} // namespace counterstrikesharp
