#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <unordered_map>
#include <unordered_set>

#include <nlohmann/json.hpp>

namespace counterstrikesharp::gamedata {

// A gamedata entry may name the vtable slot its function lives in, next to its signature:
//
//   "CBaseTrigger_EndTouch": {
//     "signatures": { "library": "server", "linux": "55 48 ..", "windows": "40 53 .." },
//     "vtable": { "class": "CBaseTrigger", "linux_index": 151, "windows_index": 152 }
//   }
//
// CS2_VibeSignatures emits it for every virtual function it has an RTTI-derived slot for. The
// slot is a second, independent way to the same function. When both resolve they must agree -
// a pattern that is unique but sits on the wrong function is the failure a scan alone cannot
// see. When the pattern fails, the slot stands in only if the file is proven to be for the
// running game build (GameverMarker below): a game update is exactly what shifts an index.
//
// The block deliberately has no "linux"/"windows" keys. Every gamedata reader in the wild
// (ours, the panel, the VibeSignatures audits) treats an object holding platform keys as an
// entry of its own, so platform-keyed values here would surface as a bogus "vtable" entry.
//
// Engine-free so the parsing can be tested without a game server.
struct VtableSlot
{
    std::string className;
    int index = -1;

    bool operator==(const VtableSlot& other) const { return className == other.className && index == other.index; }
    bool operator!=(const VtableSlot& other) const { return !(*this == other); }
};

inline std::optional<VtableSlot> ParseVtableSlot(const nlohmann::json& entry, const char* platform)
{
    if (!entry.is_object() || !entry.contains("vtable")) return std::nullopt;

    const auto& vtable = entry["vtable"];
    if (!vtable.is_object() || !vtable.contains("class") || !vtable["class"].is_string()) return std::nullopt;

    const auto indexKey = std::string(platform) + "_index";
    if (!vtable.contains(indexKey) || !vtable[indexKey].is_number_integer()) return std::nullopt;

    VtableSlot slot;
    slot.className = vtable["class"].get<std::string>();
    slot.index = vtable[indexKey].get<int>();
    if (slot.className.empty() || slot.index < 0) return std::nullopt;
    return slot;
}

struct VtableFallback
{
    std::string key; // gamedata key, for log lines
    VtableSlot slot;
    // From a file proven to be generated for the running game build (see GameverMarker). Only a
    // trusted slot may replace a failed scan; any slot can still cross-check a successful one.
    bool trusted = false;
};

// Signature text -> the slot its entry names. Keyed by the text rather than the gamedata key
// because that is all the scanner is handed: plugins call GameData.GetSignature("Key") and pass
// the string on. Signature text claimed by two entries with different slots is dropped and
// remembered in `conflicting`, so a later file cannot bring it back.
class VtableFallbacks
{
  public:
    void Add(const nlohmann::json& document, const char* platform, bool trusted)
    {
        if (!document.is_object()) return;

        for (const auto& [key, entry] : document.items())
        {
            if (!entry.is_object() || !entry.contains("signatures")) continue;

            const auto& signatures = entry["signatures"];
            if (!signatures.is_object() || !signatures.contains(platform) || !signatures[platform].is_string()) continue;

            auto signature = signatures[platform].get<std::string>();
            if (signature.empty() || signature[0] == '@') continue; // a symbol needs no fallback

            auto slot = ParseVtableSlot(entry, platform);
            if (!slot) continue;

            if (m_conflicting.count(signature)) continue;

            auto it = m_bySignature.find(signature);
            if (it != m_bySignature.end() && it->second.slot != *slot)
            {
                m_bySignature.erase(it);
                m_conflicting.insert(signature);
                continue;
            }

            if (it != m_bySignature.end())
            {
                it->second.trusted = it->second.trusted || trusted;
                continue;
            }

            m_bySignature[signature] = { key, *slot, trusted };
        }
    }

    const VtableFallback* Find(const std::string& signature) const
    {
        auto it = m_bySignature.find(signature);
        return it == m_bySignature.end() ? nullptr : &it->second;
    }

    size_t Size() const { return m_bySignature.size(); }
    const std::unordered_set<std::string>& Conflicting() const { return m_conflicting; }

  private:
    std::unordered_map<std::string, VtableFallback> m_bySignature;
    std::unordered_set<std::string> m_conflicting;
};

// gamedata/gamedata.gamever: "<game build tag> <FNV-1a 64 of gamedata.json, hex>". The updater
// writes it whenever it has confirmed that gamedata.json was generated for the build the server
// runs (manifest gameVersion == steam.inf PatchVersion). Only then may a vtable slot stand in for
// a failed signature: an index from an older build is exactly what a game update shifts, and a
// shifted slot holds a different function. The hash ties the marker to the file it vouched for,
// so a gamedata.json replaced by hand or by a release deploy loses the trust on its own.
inline std::string ContentHash(const std::string& contents)
{
    std::uint64_t hash = 14695981039346656037ull;
    for (unsigned char c : contents)
    {
        hash ^= c;
        hash *= 1099511628211ull;
    }

    static constexpr char digits[] = "0123456789abcdef";
    std::string out(16, '0');
    for (int i = 15; i >= 0; --i, hash >>= 4)
        out[i] = digits[hash & 0xF];
    return out;
}

inline std::string GameverMarker(const std::string& gameTag, const std::string& gamedataContents)
{
    return gameTag + " " + ContentHash(gamedataContents);
}

inline bool GameverMarkerMatches(const std::string& marker, const std::string& gameTag, const std::string& gamedataContents)
{
    return !gameTag.empty() && marker == GameverMarker(gameTag, gamedataContents);
}

} // namespace counterstrikesharp::gamedata
