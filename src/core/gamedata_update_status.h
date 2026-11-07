#pragma once

#include <string>
#include <vector>

#include <nlohmann/json.hpp>

#include "core/gamedata_plugin_update.h"

// What the gamedata updaters did on this start, for the "patched" block of
// logs/gamedata-status.json (update::WriteGamedataStatus). The updaters run in Load() before the
// status is written, so this is an in-memory record of the same start; it never reads back an
// older start's. Engine-free so the JSON shape can be tested.
namespace counterstrikesharp::update::status {

// The core updater's outcome (TryUpdateGameConfig).
struct CoreUpdate
{
    // "updated", "up-to-date", "skipped" (build gate, rolled-back file, nothing to compare),
    // "rejected" (the download failed a check), "failed" (network/disk), "disabled".
    std::string result = "disabled";
    std::string detail;
    std::string feed; // the URL it was fetched from (latest/ or builds/<build>/)
    std::string build; // the manifest gameVersion it was checked against
    std::string sha256; // of the file written, when result == "updated"
    std::string backup;
};

// One plugin gamedata file the plugin updater wrote (TryUpdatePluginGamedata).
struct FilePatch
{
    std::string file; // relative to addons/counterstrikesharp, e.g. "gamedata/matchzy.json"
    // "feed": the signed VibeSignatures feed; "upload": this server's own uploaded copy on the
    // GamedataReportURL host (unsigned, so signatures only).
    std::string source = "feed";
    // "signatures": broken signatures swapped one by one (gamedata_plugin_update.h);
    // "file": an unmodified older published copy replaced by this build's file, offsets included.
    std::string mode = "signatures";
    std::string feed; // "<plugin>/<fileName>" for the feed, the read URL for an upload
    std::string build; // the feed build the values are from; empty for an upload
    std::string fromBuild; // mode "file": the earlier build the local file was published for
    std::string matchedBy; // plugin_gamedata::MatchName, or "upload"
    std::string platform; // the platform whose signatures were checked
    std::string backup;
    std::string sha256; // of the file as written
    std::vector<plugin_gamedata::KeyChange> keys;
};

struct StartRecord
{
    CoreUpdate core;
    std::vector<FilePatch> files;
    std::vector<std::string> rolledBack; // files the crash-loop guard restored at this start
};

inline nlohmann::json ToJson(const CoreUpdate& core)
{
    nlohmann::json out = { { "file", "gamedata/gamedata.json" }, { "result", core.result } };
    if (!core.detail.empty()) out["detail"] = core.detail;
    if (!core.feed.empty()) out["feed"] = core.feed;
    if (!core.build.empty()) out["build"] = core.build;
    if (!core.sha256.empty()) out["sha256"] = core.sha256;
    if (!core.backup.empty()) out["backup"] = core.backup;
    return out;
}

inline nlohmann::json ToJson(const FilePatch& patch)
{
    auto keys = nlohmann::json::array();
    for (const auto& key : patch.keys)
        keys.push_back({ { "name", key.name }, { "changed", key.changed } });

    nlohmann::json out = { { "file", patch.file },     { "source", patch.source },       { "mode", patch.mode },
                           { "feed", patch.feed },     { "matchedBy", patch.matchedBy }, { "platform", patch.platform },
                           { "keys", std::move(keys) } };
    if (!patch.build.empty()) out["build"] = patch.build;
    if (!patch.fromBuild.empty()) out["fromBuild"] = patch.fromBuild;
    if (!patch.backup.empty()) out["backup"] = patch.backup;
    if (!patch.sha256.empty()) out["sha256"] = patch.sha256;
    return out;
}

// The "patched" block: { "core": {...}, "files": [...one per file written...], "rolledBack": [...] }.
// A file the updater did not write this start is not listed.
inline nlohmann::json ToJson(const StartRecord& record)
{
    auto files = nlohmann::json::array();
    for (const auto& patch : record.files)
        files.push_back(ToJson(patch));
    return { { "core", ToJson(record.core) }, { "files", std::move(files) }, { "rolledBack", record.rolledBack } };
}

// Signature-only patches name one platform: each patched key changed "signatures.<platform>".
inline std::vector<plugin_gamedata::KeyChange> SignatureChanges(const std::vector<std::string>& keys, const std::string& platform)
{
    std::vector<plugin_gamedata::KeyChange> out;
    for (const auto& key : keys)
        out.push_back({ key, { "signatures." + platform } });
    return out;
}

} // namespace counterstrikesharp::update::status
