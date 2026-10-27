#pragma once

#include <cctype>
#include <string>
#include <vector>

#include <nlohmann/json.hpp>

#include "core/gamedata_signature_check.h"

namespace counterstrikesharp::gamedata {

// gamedata/custom/*.json: operator overrides layered on top of core gamedata.json, the same idea
// as SourceMod's gamedata/custom/. The auto-updater only ever writes gamedata.json, so whatever
// lives in custom/ survives every feed update. Plugin gamedata files (gamedata/<plugin>.json)
// are not touched by this: they keep merging on the managed side exactly as before.
//
//   {
//     "_gameVersion": "1.41.9.2",
//     "Host_Say": { "signatures": { "linux": "55 48 89 E5 ..." } }
//   }
//
// - Files apply in file-name order, later files win.
// - Merged per field: the sections below merge member by member, so overriding only the linux
//   signature keeps the core windows signature and library. Any other member is replaced whole.
// - A key not in core is an addition.
// - Top-level keys starting with '_' are file metadata, never gamedata entries. The managed
//   loader deserializes entries as objects, so a string-valued key would fail the whole file.
// - "_gameVersion" (optional) pins the file to one game build. On any other build the whole
//   file is skipped and core gamedata.json applies: an override written for an old build is
//   exactly what points a hook at the wrong function after a game update.
// - An overridden signature that does not resolve, where the core one does, is not applied for
//   that key; the core signature stays and a warning names the file.
//
// Engine-free so the merge can be tested without a game server; the resolver is a callback.

inline constexpr const char* kPerFieldSections[] = { "signatures", "offsets", "patches", "vtable" };

inline bool IsMetadataKey(const std::string& key) { return !key.empty() && key[0] == '_'; }

// "1.41.9.2", "14192" and "14192b" all name game build 14192: digits only, a trailing build letter
// (VibeSignatures' rebuild suffix) dropped. Same tag ServerGameVersionTag reads from steam.inf.
inline std::string NormalizeGameVersion(const std::string& version)
{
    std::string out;
    for (char c : version)
    {
        if (std::isdigit(static_cast<unsigned char>(c))) out += c;
        else if (c == '.' || c == ' ')
            continue;
        else
            break;
    }
    return out;
}

struct CustomFile
{
    std::string name; // "custom/fix.json", for log lines
    nlohmann::json json;
};

struct AppliedOverride
{
    std::string key;
    std::string file;
    bool added = false; // key was not in core
    std::vector<std::string> fields; // "signatures.linux", "offsets.windows", ...
};

struct CustomMergeResult
{
    std::vector<AppliedOverride> applied;
    std::vector<std::string> skippedFiles; // "custom/x.json: why"
    std::vector<std::string> warnings;
};

namespace detail {
inline std::string LibraryOf(const nlohmann::json& entry)
{
    if (entry.is_object() && entry.contains("signatures") && entry["signatures"].is_object())
    {
        const auto& sigs = entry["signatures"];
        if (sigs.contains("library") && sigs["library"].is_string()) return sigs["library"].get<std::string>();
    }
    return "server";
}

inline std::string PlatformSignatureOf(const nlohmann::json& entry, const char* platform)
{
    if (entry.is_object() && entry.contains("signatures") && entry["signatures"].is_object())
    {
        const auto& sigs = entry["signatures"];
        if (sigs.contains(platform) && sigs[platform].is_string()) return sigs[platform].get<std::string>();
    }
    return {};
}

inline bool IsPerFieldSection(const std::string& name)
{
    for (const auto* section : kPerFieldSections)
        if (name == section) return true;
    return false;
}

// Merges `patch` into `entry` and records which fields it set.
inline void MergeEntry(nlohmann::json& entry, const nlohmann::json& patch, std::vector<std::string>& fields)
{
    for (const auto& [name, value] : patch.items())
    {
        if (IsPerFieldSection(name) && value.is_object() && entry.contains(name) && entry[name].is_object())
        {
            for (const auto& [field, fieldValue] : value.items())
            {
                entry[name][field] = fieldValue;
                fields.push_back(name + "." + field);
            }
            continue;
        }
        entry[name] = value;
        fields.push_back(name);
    }
}
} // namespace detail

// Applies `files` to `core` in the given order. `serverTag` is the running build as
// NormalizeGameVersion spells it (empty when unknown). `resolves` judges signatures against the
// running binaries; without one, overrides are applied unchecked.
inline CustomMergeResult ApplyCustomFiles(nlohmann::json& core,
                                          const std::vector<CustomFile>& files,
                                          const std::string& serverTag,
                                          const char* platform,
                                          const update::SignatureResolver& resolves = {})
{
    CustomMergeResult result;
    if (!core.is_object()) core = nlohmann::json::object();

    for (const auto& file : files)
    {
        if (!file.json.is_object())
        {
            result.skippedFiles.push_back(file.name + ": not a JSON object");
            continue;
        }

        if (file.json.contains("_gameVersion"))
        {
            const auto& pin = file.json["_gameVersion"];
            const auto pinned = pin.is_string() ? NormalizeGameVersion(pin.get<std::string>()) : std::string();
            if (pinned.empty())
            {
                result.skippedFiles.push_back(file.name + ": _gameVersion is not a game version string");
                continue;
            }
            if (serverTag.empty())
            {
                result.skippedFiles.push_back(file.name + ": pinned to " + pinned + ", but this server's game version is unknown");
                continue;
            }
            if (pinned != serverTag)
            {
                result.skippedFiles.push_back(file.name + ": pinned to game build " + pinned + ", this server runs " + serverTag);
                continue;
            }
        }

        for (const auto& [key, patch] : file.json.items())
        {
            if (IsMetadataKey(key)) continue;
            if (!patch.is_object())
            {
                result.warnings.push_back(file.name + ": " + key + " is not an object, ignored");
                continue;
            }

            const bool added = !core.contains(key) || !core[key].is_object();
            nlohmann::json merged = added ? nlohmann::json::object() : core[key];
            const nlohmann::json original = merged;
            const auto before = detail::PlatformSignatureOf(original, platform);

            AppliedOverride applied{ key, file.name, added, {} };
            detail::MergeEntry(merged, patch, applied.fields);

            const auto after = detail::PlatformSignatureOf(merged, platform);
            if (resolves && !after.empty() && after != before && !resolves(detail::LibraryOf(merged), after))
            {
                if (!before.empty() && resolves(detail::LibraryOf(original), before))
                {
                    // Keep the core signature and library for this key; the rest of the patch still applies.
                    const auto& originalSigs = original["signatures"];
                    merged["signatures"][platform] = before;
                    if (originalSigs.contains("library")) merged["signatures"]["library"] = originalSigs["library"];
                    else
                        merged["signatures"].erase("library");

                    const auto platformField = std::string("signatures.") + platform;
                    std::vector<std::string> kept;
                    for (auto& field : applied.fields)
                        if (field != platformField && field != "signatures.library") kept.push_back(std::move(field));
                    applied.fields = std::move(kept);

                    result.warnings.push_back(file.name + ": " + key + " " + platform +
                                              " signature does not resolve, keeping the core gamedata.json one");
                }
                else
                {
                    result.warnings.push_back(file.name + ": " + key + " " + platform + " signature does not resolve");
                }
            }

            if (applied.fields.empty()) continue;
            core[key] = std::move(merged);
            result.applied.push_back(std::move(applied));
        }
    }

    return result;
}

} // namespace counterstrikesharp::gamedata
