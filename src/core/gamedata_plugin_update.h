#pragma once

#include <algorithm>
#include <cctype>
#include <cstdint>
#include <functional>
#include <map>
#include <set>
#include <string>
#include <utility>
#include <vector>

#include <nlohmann/json.hpp>

// Plugin gamedata auto-update: the engine-free half (identification, patching, hashing), so the
// rules can be tested without a game server. The network and filesystem half lives in
// gameconfig_updater.cpp (TryUpdatePluginGamedata).
//
// gamedata/gamedata.json is replaced whole by the core updater: the feed owns that file. A plugin's
// gamedata file is different. Its author owns it, a server may run a plugin release the feed has
// never seen, and the file can carry keys the feed does not know. So a plugin file is never
// replaced; only the signatures that do not resolve on this server are swapped for the feed's, and
// only when the feed's signature resolves here (same library, same platform). Working keys,
// offsets, unknown keys, key order and formatting all stay as they were.
//
// Which feed file a local file is: the VibeSignatures Pages site publishes
// latest/<plugin>/<fileName> per plugin, and several plugins can share a file name
// (matchzy/matchzy.json and matchzy-original/matchzy.json). In order of strength:
//   1. explicit: PluginGamedataFeeds in core.json names it.
//   2. hash: the local file is byte-identical to a file the site published for some build
//      (gamedata/index.json lists every build's files with their sha256).
//   3. remembered: the feed this updater patched the file from last time (a patched file no
//      longer matches any published hash).
//   4. name: the only feed file with the same file name, or among several, the one that shares
//      the most gamedata keys with the local file. A tie is not guessed.
namespace counterstrikesharp::update::plugin_gamedata {

using ordered_json = nlohmann::ordered_json;

// --- SHA-256 (FIPS 180-4), lower-case hex. The site's manifest and index name files by it. ---
inline std::string Sha256Hex(const std::string& data)
{
    static constexpr std::uint32_t k[64] = {
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be,
        0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa,
        0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85,
        0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3,
        0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f,
        0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
    };
    std::uint32_t h[8] = { 0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19 };

    auto rotr = [](std::uint32_t x, int n) {
        return (x >> n) | (x << (32 - n));
    };

    std::string msg = data;
    const std::uint64_t bitLength = static_cast<std::uint64_t>(data.size()) * 8;
    msg.push_back(static_cast<char>(0x80));
    while (msg.size() % 64 != 56)
        msg.push_back('\0');
    for (int i = 7; i >= 0; --i)
        msg.push_back(static_cast<char>((bitLength >> (i * 8)) & 0xff));

    for (size_t chunk = 0; chunk < msg.size(); chunk += 64)
    {
        std::uint32_t w[64];
        for (int i = 0; i < 16; ++i)
        {
            const auto* p = reinterpret_cast<const unsigned char*>(msg.data() + chunk + i * 4);
            w[i] = (std::uint32_t(p[0]) << 24) | (std::uint32_t(p[1]) << 16) | (std::uint32_t(p[2]) << 8) | std::uint32_t(p[3]);
        }
        for (int i = 16; i < 64; ++i)
        {
            const auto s0 = rotr(w[i - 15], 7) ^ rotr(w[i - 15], 18) ^ (w[i - 15] >> 3);
            const auto s1 = rotr(w[i - 2], 17) ^ rotr(w[i - 2], 19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16] + s0 + w[i - 7] + s1;
        }

        std::uint32_t a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
        for (int i = 0; i < 64; ++i)
        {
            const auto S1 = rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25);
            const auto ch = (e & f) ^ (~e & g);
            const auto t1 = hh + S1 + ch + k[i] + w[i];
            const auto S0 = rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22);
            const auto maj = (a & b) ^ (a & c) ^ (b & c);
            const auto t2 = S0 + maj;
            hh = g;
            g = f;
            f = e;
            e = d + t1;
            d = c;
            c = b;
            b = a;
            a = t1 + t2;
        }
        h[0] += a;
        h[1] += b;
        h[2] += c;
        h[3] += d;
        h[4] += e;
        h[5] += f;
        h[6] += g;
        h[7] += hh;
    }

    static constexpr char hex[] = "0123456789abcdef";
    std::string out;
    out.reserve(64);
    for (auto word : h)
        for (int i = 28; i >= 0; i -= 4)
            out.push_back(hex[(word >> i) & 0xf]);
    return out;
}

inline std::string Lower(std::string text)
{
    for (auto& c : text)
        c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return text;
}

constexpr const char kUtf8Bom[] = "\xEF\xBB\xBF";

// sha256 of `text` and of its other UTF-8 BOM variant (without the BOM when it has one, with one
// when it has none). An editor that adds or drops a BOM changes no byte the gamedata readers
// look at (nlohmann and System.Text.Json both skip it), so a published file saved that way is
// still that published file.
inline std::vector<std::string> ContentSha256s(const std::string& text)
{
    const bool hasBom = text.rfind(kUtf8Bom, 0) == 0;
    return { Sha256Hex(text), Sha256Hex(hasBom ? text.substr(3) : std::string(kUtf8Bom) + text) };
}

inline bool AnyOf(const std::vector<std::string>& shas, const std::string& sha)
{
    return std::find(shas.begin(), shas.end(), Lower(sha)) != shas.end();
}

// Game builds as VibeSignatures names them: "14196", or "14178b" for a rebuild of 14178 (which
// comes after it). <0, 0, >0 like strcmp; a build without digits sorts first.
inline int CompareBuilds(const std::string& a, const std::string& b)
{
    auto split = [](const std::string& build) {
        size_t digits = 0;
        while (digits < build.size() && std::isdigit(static_cast<unsigned char>(build[digits])))
            ++digits;
        std::string number = build.substr(0, digits);
        // Leading zeros off; (std::min) because windows.h may define a min macro.
        number.erase(0, (std::min)(number.find_first_not_of('0'), number.size()));
        return std::make_pair(number, build.substr(digits));
    };
    const auto [numberA, suffixA] = split(a);
    const auto [numberB, suffixB] = split(b);
    if (numberA.size() != numberB.size()) return numberA.size() < numberB.size() ? -1 : 1;
    if (const int c = numberA.compare(numberB); c != 0) return c < 0 ? -1 : 1;
    if (const int c = suffixA.compare(suffixB); c != 0) return c < 0 ? -1 : 1;
    return 0;
}

// --- Site metadata ---

struct FeedFile
{
    std::string url; // "<plugin>/<fileName>", relative to <site>/latest/
    std::string fileName;
    std::string sha256;
};

// The JSON files of latest/manifest.json. Other formats (.txt KeyValues, .jsonc) belong to
// non-CounterStrikeSharp plugins and are not ours to patch.
inline std::vector<FeedFile> ParseManifestFiles(const nlohmann::json& manifest)
{
    std::vector<FeedFile> out;
    if (!manifest.is_object() || !manifest.contains("files") || !manifest["files"].is_array()) return out;

    for (const auto& file : manifest["files"])
    {
        if (!file.is_object()) continue;
        auto text = [&](const char* key) {
            return file.contains(key) && file[key].is_string() ? file[key].get<std::string>() : std::string();
        };
        FeedFile feed{ text("url"), text("fileName"), Lower(text("sha256")) };
        if (feed.url.empty() || feed.fileName.empty()) continue;
        const auto name = Lower(feed.fileName);
        if (name.size() < 5 || name.compare(name.size() - 5, 5, ".json") != 0) continue;
        out.push_back(std::move(feed));
    }
    return out;
}

// gamedata/index.json: sha256 of every file the site has published, for any build -> the
// latest/ URLs ("<plugin>/<fileName>") that file was published as.
inline std::map<std::string, std::set<std::string>> ParseIndexHashes(const nlohmann::json& index)
{
    std::map<std::string, std::set<std::string>> out;
    if (!index.is_object() || !index.contains("versions") || !index["versions"].is_array()) return out;

    for (const auto& version : index["versions"])
    {
        if (!version.is_object() || !version.contains("files") || !version["files"].is_array()) continue;
        for (const auto& file : version["files"])
        {
            if (!file.is_object() || !file.contains("plugin") || !file.contains("fileName") || !file.contains("content")) continue;
            const auto& content = file["content"];
            if (!file["plugin"].is_string() || !file["fileName"].is_string() || !content.is_object() || !content.contains("sha256") ||
                !content["sha256"].is_string())
                continue;
            out[Lower(content["sha256"].get<std::string>())].insert(file["plugin"].get<std::string>() + "/" +
                                                                    file["fileName"].get<std::string>());
        }
    }
    return out;
}

// One appearance of a file in gamedata/index.json: published as `url` ("<plugin>/<fileName>")
// for game build `build`.
struct Publication
{
    std::string url;
    std::string build;
};

// gamedata/index.json: sha256 -> every (url, build) it was published as. ParseIndexHashes without
// the builds is enough to identify a file; deciding whether it is an *older* copy needs them.
inline std::map<std::string, std::vector<Publication>> ParseIndexPublications(const nlohmann::json& index)
{
    std::map<std::string, std::vector<Publication>> out;
    if (!index.is_object() || !index.contains("versions") || !index["versions"].is_array()) return out;

    for (const auto& version : index["versions"])
    {
        if (!version.is_object() || !version.contains("gameVersion") || !version["gameVersion"].is_string() || !version.contains("files") ||
            !version["files"].is_array())
            continue;
        const auto build = version["gameVersion"].get<std::string>();
        for (const auto& file : version["files"])
        {
            if (!file.is_object() || !file.contains("plugin") || !file.contains("fileName") || !file.contains("content")) continue;
            const auto& content = file["content"];
            if (!file["plugin"].is_string() || !file["fileName"].is_string() || !content.is_object() || !content.contains("sha256") ||
                !content["sha256"].is_string())
                continue;
            out[Lower(content["sha256"].get<std::string>())].push_back(
                { file["plugin"].get<std::string>() + "/" + file["fileName"].get<std::string>(), build });
        }
    }
    return out;
}

// --- Identification ---

enum class Match
{
    None,
    Explicit,
    Hash,
    Remembered,
    Name,
};

inline const char* MatchName(Match match)
{
    switch (match)
    {
        case Match::Explicit:
            return "PluginGamedataFeeds";
        case Match::Hash:
            return "published file hash";
        case Match::Remembered:
            return "previous update";
        case Match::Name:
            return "file name";
        default:
            return "none";
    }
}

struct Candidates
{
    Match match = Match::None;
    // More than one only for Hash/Name: the caller loads them and lets PickByKeyOverlap decide.
    std::vector<std::string> urls;
    std::string note; // why nothing matched, for the log
};

inline bool InFeed(const std::vector<FeedFile>& feed, const std::string& url)
{
    return std::any_of(feed.begin(), feed.end(), [&](const FeedFile& f) {
        return f.url == url;
    });
}

// explicitUrl: the core.json mapping for this file, if any ("" = do not update this file).
// hashes: ParseIndexHashes, may be empty when index.json could not be fetched.
// localSha256s: ContentSha256s of the file (or just its sha256): any of them may match.
inline Candidates FindCandidates(const std::string& fileName,
                                 const std::vector<std::string>& localSha256s,
                                 const std::vector<FeedFile>& feed,
                                 const std::map<std::string, std::set<std::string>>& hashes,
                                 const std::string* explicitUrl,
                                 const std::string& rememberedUrl)
{
    Candidates out;

    if (explicitUrl)
    {
        if (explicitUrl->empty()) out.note = "disabled in PluginGamedataFeeds";
        else if (!InFeed(feed, *explicitUrl))
            out.note = "PluginGamedataFeeds names " + *explicitUrl + ", which the feed does not publish for this build";
        else
        {
            out.match = Match::Explicit;
            out.urls = { *explicitUrl };
        }
        return out;
    }

    // A published byte-for-byte copy names the plugin exactly. If it was published under more
    // than one name (a fork shipping the same file), the one remembered from last time decides,
    // otherwise key overlap does.
    std::set<std::string> hashedUrls;
    for (const auto& sha : localSha256s)
    {
        auto hashed = hashes.find(Lower(sha));
        if (hashed != hashes.end()) hashedUrls.insert(hashed->second.begin(), hashed->second.end());
    }
    if (!hashedUrls.empty())
    {
        std::vector<std::string> live;
        for (const auto& url : hashedUrls)
            if (InFeed(feed, url)) live.push_back(url);
        if (!live.empty())
        {
            out.match = Match::Hash;
            if (std::find(live.begin(), live.end(), rememberedUrl) != live.end()) out.urls = { rememberedUrl };
            else
                out.urls = std::move(live);
            return out;
        }
    }

    if (!rememberedUrl.empty() && InFeed(feed, rememberedUrl))
    {
        out.match = Match::Remembered;
        out.urls = { rememberedUrl };
        return out;
    }

    const auto wanted = Lower(fileName);
    for (const auto& file : feed)
        if (Lower(file.fileName) == wanted) out.urls.push_back(file.url);
    if (!out.urls.empty()) out.match = Match::Name;
    else
        out.note = "no feed file is named " + fileName;
    return out;
}

inline Candidates FindCandidates(const std::string& fileName,
                                 const std::string& localSha256,
                                 const std::vector<FeedFile>& feed,
                                 const std::map<std::string, std::set<std::string>>& hashes,
                                 const std::string* explicitUrl,
                                 const std::string& rememberedUrl)
{
    return FindCandidates(fileName, std::vector<std::string>{ localSha256 }, feed, hashes, explicitUrl, rememberedUrl);
}

inline bool IsGamedataKey(const std::string& key) { return !key.empty() && key[0] != '_'; }

// Index into `options` of the feed file sharing the most gamedata keys with `local`;
// kOverlapTie when two share the same, highest number, kOverlapNone when none shares any.
constexpr int kOverlapTie = -1;
constexpr int kOverlapNone = -2;

inline int PickByKeyOverlap(const ordered_json& local, const std::vector<const ordered_json*>& options)
{
    int best = -1;
    size_t bestCount = 0;
    bool tie = false;
    for (size_t i = 0; i < options.size(); ++i)
    {
        if (!options[i] || !options[i]->is_object()) continue;
        size_t count = 0;
        for (const auto& [key, value] : local.items())
            if (IsGamedataKey(key) && options[i]->contains(key)) ++count;
        if (count == 0) continue;
        if (count > bestCount)
        {
            best = static_cast<int>(i);
            bestCount = count;
            tie = false;
        }
        else if (count == bestCount)
            tie = true;
    }
    if (best < 0) return kOverlapNone;
    return tie ? kOverlapTie : best;
}

// --- Patching ---

// "library" is optional in gamedata and means the server module when absent.
inline std::string LibraryOf(const ordered_json& signatures)
{
    if (signatures.contains("library") && signatures["library"].is_string())
    {
        auto library = signatures["library"].get<std::string>();
        if (!library.empty()) return library;
    }
    return "server";
}

// Signature text of `entry` for `platform`, or "" when it has none.
inline std::string SignatureOf(const ordered_json& entry, const char* platform)
{
    if (!entry.is_object() || !entry.contains("signatures") || !entry["signatures"].is_object()) return {};
    const auto& signatures = entry["signatures"];
    if (!signatures.contains(platform) || !signatures[platform].is_string()) return {};
    return signatures[platform].get<std::string>();
}

using SignatureResolver = std::function<bool(const std::string& library, const std::string& signature)>;

// Keys of `gamedata` whose `platform` signature does not resolve.
inline std::vector<std::string> BrokenSignatures(const ordered_json& gamedata, const char* platform, const SignatureResolver& resolves)
{
    std::vector<std::string> out;
    if (!gamedata.is_object()) return out;
    for (const auto& [key, entry] : gamedata.items())
    {
        if (!IsGamedataKey(key)) continue;
        const auto signature = SignatureOf(entry, platform);
        if (!signature.empty() && !resolves(LibraryOf(entry["signatures"]), signature)) out.push_back(key);
    }
    return out;
}

struct PatchResult
{
    std::vector<std::string> patched; // keys whose signature was replaced
    std::vector<std::string> unresolved; // "Key: why", broken and left as they are
};

// Replaces, in `local`, every `platform` signature that does not resolve with the feed's
// signature for the same key, when that one resolves in the same library. Nothing else in the
// file changes.
inline PatchResult
PatchBrokenSignatures(ordered_json& local, const ordered_json& feed, const char* platform, const SignatureResolver& resolves)
{
    PatchResult result;
    if (!local.is_object()) return result;

    for (const auto& key : BrokenSignatures(local, platform, resolves))
    {
        auto& signatures = local[key]["signatures"];
        const auto library = LibraryOf(signatures);

        if (!feed.is_object() || !feed.contains(key))
        {
            result.unresolved.push_back(key + ": the feed does not have it");
            continue;
        }
        const auto& feedEntry = feed[key];
        const auto feedSignature = SignatureOf(feedEntry, platform);
        if (feedSignature.empty())
        {
            result.unresolved.push_back(key + ": the feed has no " + std::string(platform) + " signature");
            continue;
        }
        if (LibraryOf(feedEntry["signatures"]) != library)
        {
            result.unresolved.push_back(key + ": the feed's signature is for library " + LibraryOf(feedEntry["signatures"]) +
                                        ", the file's for " + library);
            continue;
        }
        if (feedSignature == signatures[platform].get<std::string>())
        {
            result.unresolved.push_back(key + ": the feed has the same signature");
            continue;
        }
        if (!resolves(library, feedSignature))
        {
            result.unresolved.push_back(key + ": the feed's signature does not resolve either");
            continue;
        }

        signatures[platform] = feedSignature;
        result.patched.push_back(key);
    }
    return result;
}

// --- Taking this build's file whole (offsets included) ---
//
// Offsets cannot be checked in this process: a wrong offset reads or writes the wrong field and
// nothing notices until a crash. So a plugin file's offsets only change when the file provably
// is VibeSignatures' own and unmodified, just from an earlier build: byte-identical (BOM aside)
// to a file gamedata/index.json lists as published under the same feed URL for an earlier
// build, while this server's build has a newer version of that same feed file in its signed
// manifest. Then nothing in the local file is the operator's or the plugin author's, and this
// build's file is what VibeSignatures generated from the same source for this build: it is
// taken whole, byte for byte. Whole rather than per key so the result is again a published
// file, which keeps it identifiable by hash and eligible for the same step on the next build
// (a per-key merge would never match a published hash again). Any local difference keeps the
// signature-only rule above.

struct RefreshDecision
{
    bool refresh = false;
    bool current = false; // already this build's file: nothing to do
    std::string fromBuild; // newest earlier build the local file was published for
    std::string why; // why not, for the log
};

// localSha256s: ContentSha256s of the local file. url: the feed file it was identified as.
// feedBuild: the gameVersion of the signed manifest this server's build uses; feedSha256: the
// sha256 that manifest lists for url ("" when it lists none). signedManifest: the manifest's
// signature was verified (GamedataSigningKey set); without one no file is taken whole.
inline RefreshDecision DecideWholeFileRefresh(const std::vector<std::string>& localSha256s,
                                              const std::string& url,
                                              const std::string& feedBuild,
                                              const std::string& feedSha256,
                                              bool signedManifest,
                                              const std::map<std::string, std::vector<Publication>>& publications)
{
    RefreshDecision out;
    if (AnyOf(localSha256s, feedSha256) && !feedSha256.empty())
    {
        out.current = true;
        out.why = "already the file published for build " + feedBuild;
        return out;
    }

    for (const auto& sha : localSha256s)
    {
        auto it = publications.find(Lower(sha));
        if (it == publications.end()) continue;
        for (const auto& publication : it->second)
            if (publication.url == url && CompareBuilds(publication.build, feedBuild) < 0 &&
                (out.fromBuild.empty() || CompareBuilds(publication.build, out.fromBuild) > 0))
                out.fromBuild = publication.build;
    }

    if (out.fromBuild.empty())
    {
        out.why = "not a published copy of " + url + " for an earlier build (edited, or another release)";
        return out;
    }
    if (!signedManifest)
    {
        out.why = "the manifest is not signed (GamedataSigningKey is off)";
        return out;
    }
    if (feedSha256.empty())
    {
        out.why = "build " + feedBuild + "'s manifest lists no sha256 for " + url;
        return out;
    }
    out.refresh = true;
    return out;
}

// Gamedata keys `local` has and `incoming` does not. Taking a file whole must not drop a key
// the plugin may still read; when it would, the file keeps the signature-only rule.
inline std::vector<std::string> DroppedKeys(const ordered_json& local, const ordered_json& incoming)
{
    std::vector<std::string> out;
    if (!local.is_object()) return out;
    for (const auto& [key, value] : local.items())
        if (IsGamedataKey(key) && (!incoming.is_object() || !incoming.contains(key))) out.push_back(key);
    return out;
}

// One key the updater changed, and what in it: "signatures.linux", "offsets.windows",
// "signatures.library", "added", or the name of any other field that changed.
struct KeyChange
{
    std::string name;
    std::vector<std::string> changed;
};

inline std::vector<KeyChange> DiffEntries(const ordered_json& before, const ordered_json& after)
{
    std::vector<KeyChange> out;
    if (!after.is_object()) return out;
    for (const auto& [key, value] : after.items())
    {
        if (!IsGamedataKey(key)) continue;
        if (!before.is_object() || !before.contains(key))
        {
            out.push_back({ key, { "added" } });
            continue;
        }
        const auto& old = before[key];
        if (old == value) continue;

        KeyChange change{ key, {} };
        if (!old.is_object() || !value.is_object())
        {
            change.changed.push_back("value");
            out.push_back(std::move(change));
            continue;
        }
        std::set<std::string> fields;
        for (const auto& [field, unused] : old.items())
            fields.insert(field);
        for (const auto& [field, unused] : value.items())
            fields.insert(field);
        for (const auto& field : fields)
        {
            const auto* a = old.contains(field) ? &old[field] : nullptr;
            const auto* b = value.contains(field) ? &value[field] : nullptr;
            if (a && b && *a == *b) continue;
            // signatures/offsets: per platform (and library), the part the status is read for.
            if ((field == "signatures" || field == "offsets") && a && b && a->is_object() && b->is_object())
            {
                std::set<std::string> subs;
                for (const auto& [sub, unused] : a->items())
                    subs.insert(sub);
                for (const auto& [sub, unused] : b->items())
                    subs.insert(sub);
                for (const auto& sub : subs)
                    if (!a->contains(sub) || !b->contains(sub) || (*a)[sub] != (*b)[sub]) change.changed.push_back(field + "." + sub);
                continue;
            }
            change.changed.push_back(field);
        }
        out.push_back(std::move(change));
    }
    for (const auto& key : DroppedKeys(before, after))
        out.push_back({ key, { "removed" } });
    return out;
}

// --- Writing back in the file's own style ---

struct Indent
{
    int width = 4;
    char character = ' ';
};

// Indentation of the first indented line; 4 spaces when the file has none (one line).
inline Indent DetectIndent(const std::string& text)
{
    size_t pos = 0;
    while (pos < text.size())
    {
        auto end = text.find('\n', pos);
        if (end == std::string::npos) end = text.size();
        size_t i = pos;
        while (i < end && (text[i] == ' ' || text[i] == '\t'))
            ++i;
        if (i > pos && i < end && text[i] != '\r') return { static_cast<int>(i - pos), text[pos] };
        pos = end + 1;
    }
    return {};
}

// `gamedata` serialized the way `original` was: same indentation, same line endings, and a
// trailing newline when the original had one.
inline std::string Serialize(const ordered_json& gamedata, const std::string& original)
{
    const auto indent = DetectIndent(original);
    auto text = gamedata.dump(indent.width, indent.character);
    if (original.find("\r\n") != std::string::npos)
    {
        std::string crlf;
        crlf.reserve(text.size() + text.size() / 16);
        for (char c : text)
        {
            if (c == '\n') crlf += '\r';
            crlf += c;
        }
        text = std::move(crlf);
    }
    if (!original.empty() && original.back() == '\n') text += original.find("\r\n") != std::string::npos ? "\r\n" : "\n";
    return text;
}

} // namespace counterstrikesharp::update::plugin_gamedata
