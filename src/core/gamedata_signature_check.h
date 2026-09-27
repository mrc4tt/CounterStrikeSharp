#pragma once

#include <functional>
#include <string>
#include <unordered_map>
#include <vector>

#include <nlohmann/json.hpp>

namespace counterstrikesharp::update {

// Compares the signatures of a downloaded gamedata file against the current one, using the
// running binaries as the judge. Kept free of engine types (the resolver is a callback) so the
// decision logic can be exercised without a game server.
//
// A signature that the current file resolves and the incoming file breaks is a regression:
// taking the update would turn a working binding into a failing one. Only keys whose
// signature text changed are scanned -- identical text resolves identically -- which keeps
// the check to a handful of scans per update.
struct SignatureCheckResult
{
    std::vector<std::string> regressions; // "Key: why", each one blocks the update
    std::vector<std::string> stillBroken; // changed, but neither version resolves (not a regression)
    int changed = 0; // keys whose signature text differs or was removed
    int scans = 0;
};

// library ("server", "engine", ...) + signature text ("55 48 ..", or "@symbol") -> resolves?
using SignatureResolver = std::function<bool(const std::string& library, const std::string& signature)>;

namespace detail {
struct SignatureEntry
{
    std::string library;
    std::string signature;
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
        if (!entry.signature.empty()) out.emplace(key, std::move(entry));
    }
    return out;
}
} // namespace detail

inline SignatureCheckResult CheckSignatureRegressions(const nlohmann::json& current,
                                                      const nlohmann::json& incoming,
                                                      const char* platform,
                                                      const SignatureResolver& resolves)
{
    SignatureCheckResult result;
    auto currentSigs = detail::CollectSignatures(current, platform);
    auto incomingSigs = detail::CollectSignatures(incoming, platform);

    auto scan = [&](const detail::SignatureEntry& entry) {
        ++result.scans;
        return resolves(entry.library, entry.signature);
    };

    for (const auto& [key, cur] : currentSigs)
    {
        auto it = incomingSigs.find(key);
        if (it != incomingSigs.end() && it->second.library == cur.library && it->second.signature == cur.signature) continue;

        ++result.changed;

        if (it == incomingSigs.end())
        {
            if (scan(cur)) result.regressions.push_back(key + ": removed, but the current signature resolves");
            continue;
        }

        if (scan(it->second)) continue;

        if (scan(cur)) result.regressions.push_back(key + ": new signature does not resolve, the current one does");
        else
            result.stillBroken.push_back(key);
    }

    return result;
}

} // namespace counterstrikesharp::update
