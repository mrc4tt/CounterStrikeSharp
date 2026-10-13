#pragma once

#include <functional>
#include <optional>
#include <string>
#include <unordered_map>
#include <vector>

#include <nlohmann/json.hpp>

#include "core/gamedata_vtable.h"

namespace counterstrikesharp::update {

// Compares the signatures of a downloaded gamedata file against the current one, using the
// running binaries as the judge. Kept free of engine types (the resolver is a callback) so the
// decision logic can be exercised without a game server.
//
// A signature that the current file resolves and the incoming file breaks is a regression:
// taking the update would turn a working binding into a failing one. Only keys whose
// signature text (or vtable slot) changed are scanned -- identical text resolves identically
// -- which keeps the check to a handful of scans per update. An incoming entry whose pattern
// fails but whose vtable slot resolves counts as resolving: the incoming file is for this game
// build, so the runtime will fall back to that slot (core/vtable_fallback.h).
struct SignatureCheckResult
{
    std::vector<std::string> regressions; // "Key: why", each one blocks the update
    std::vector<std::string> stillBroken; // changed, but neither version resolves (not a regression)
    int changed = 0; // keys whose signature text differs or was removed
    int scans = 0;
};

// library ("server", "engine", ...) + signature text ("55 48 ..", or "@symbol") -> resolves?
using SignatureResolver = std::function<bool(const std::string& library, const std::string& signature)>;

// library + vtable slot -> resolves to a function?
using VtableSlotResolver = std::function<bool(const std::string& library, const gamedata::VtableSlot& slot)>;

namespace detail {
struct SignatureEntry
{
    std::string library;
    std::string signature;
    std::optional<gamedata::VtableSlot> slot;

    bool SameAs(const SignatureEntry& other) const
    {
        return library == other.library && signature == other.signature && slot == other.slot;
    }
};

inline std::unordered_map<std::string, SignatureEntry> CollectSignatures(const nlohmann::json& gamedata, const char* platform)
{
    std::unordered_map<std::string, SignatureEntry> out;
    if (!gamedata.is_object()) return out;

    for (const auto& [key, value] : gamedata.items())
    {
        if (!value.is_object() || !value.contains("signatures")) continue;
        const auto& sigs = value["signatures"];
        if (!sigs.is_object() || !sigs.contains(platform) || !sigs[platform].is_string()) continue;

        SignatureEntry entry;
        entry.signature = sigs[platform].get<std::string>();
        if (sigs.contains("library") && sigs["library"].is_string()) entry.library = sigs["library"].get<std::string>();
        entry.slot = gamedata::ParseVtableSlot(value, platform);
        if (!entry.signature.empty()) out.emplace(key, std::move(entry));
    }
    return out;
}
} // namespace detail

inline SignatureCheckResult CheckSignatureRegressions(const nlohmann::json& current,
                                                      const nlohmann::json& incoming,
                                                      const char* platform,
                                                      const SignatureResolver& resolves,
                                                      const VtableSlotResolver& slotResolves = {})
{
    SignatureCheckResult result;
    auto currentSigs = detail::CollectSignatures(current, platform);
    auto incomingSigs = detail::CollectSignatures(incoming, platform);

    auto scan = [&](const detail::SignatureEntry& entry, bool incoming) {
        ++result.scans;
        if (resolves(entry.library, entry.signature)) return true;
        // Only the incoming file's slot counts: it was checked against this game build before the
        // download, so the runtime will trust it. The current file's slot may be from an older
        // build and would not be used as a fallback.
        return incoming && entry.slot && slotResolves && slotResolves(entry.library, *entry.slot);
    };

    for (const auto& [key, cur] : currentSigs)
    {
        auto it = incomingSigs.find(key);
        if (it != incomingSigs.end() && it->second.SameAs(cur)) continue;

        ++result.changed;

        if (it == incomingSigs.end())
        {
            if (scan(cur, false)) result.regressions.push_back(key + ": removed, but the current signature resolves");
            continue;
        }

        if (scan(it->second, true)) continue;

        if (scan(cur, false)) result.regressions.push_back(key + ": new signature does not resolve, the current one does");
        else
            result.stillBroken.push_back(key);
    }

    return result;
}

} // namespace counterstrikesharp::update
