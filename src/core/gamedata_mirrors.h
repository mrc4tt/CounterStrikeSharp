#pragma once

#include <cstdint>
#include <string>
#include <vector>

#include <nlohmann/json.hpp>

namespace counterstrikesharp::gamedata {

// Gamedata auto-update mirrors (gameconfig_updater.cpp). core.json's "AutoUpdateURLs" lists
// gamedata.json URLs to try in order; the first one whose manifest matches this server's game
// build and whose file passes every check wins. A mirror that is down, behind or serves a broken
// file only costs its own time budget, and the local gamedata.json stays the last known good copy
// when none of them works.
//
// Engine-free so it can be tested without a game server.

// sig.miksen.me is GitHub Pages (CNAME mrc4tt.github.io), so a second mirror has to live on other
// infrastructure; the github.io URL only redirects back to it.
inline const std::vector<std::string>& DefaultMirrors()
{
    static const std::vector<std::string> mirrors = {
        "https://sig.miksen.me/latest/CounterStrikeSharp/gamedata.json",
    };
    return mirrors;
}

// The single-URL default every core.json written before the mirror list carries
// (core.example.json spelled it out). Seeing it means "the default", not "only this one".
inline constexpr const char* kLegacyDefaultUrl = "https://sig.miksen.me/latest/CounterStrikeSharp/gamedata.json";

// The mirrors a core.json asks for:
// - "AutoUpdateURLs": a non-empty array of strings is used as given (blank entries dropped).
// - else "AutoUpdateURL": a custom URL is a list of one; the old default means DefaultMirrors().
// - else DefaultMirrors().
inline std::vector<std::string> ResolveMirrors(const nlohmann::json& coreConfig)
{
    if (coreConfig.is_object())
    {
        auto list = coreConfig.find("AutoUpdateURLs");
        if (list != coreConfig.end() && list->is_array())
        {
            std::vector<std::string> mirrors;
            for (const auto& url : *list)
                if (url.is_string() && !url.get<std::string>().empty()) mirrors.push_back(url.get<std::string>());
            if (!mirrors.empty()) return mirrors;
        }

        auto single = coreConfig.find("AutoUpdateURL");
        if (single != coreConfig.end() && single->is_string() && !single->get<std::string>().empty() &&
            single->get<std::string>() != kLegacyDefaultUrl)
            return { single->get<std::string>() };
    }
    return DefaultMirrors();
}

// The VibeSignatures layout is <site>/latest/<plugin>/<file> next to <site>/latest/manifest.json,
// whose files[].url is "<plugin>/<file>". Empty when the URL has no /latest/ part.
inline std::string ManifestFileUrl(const std::string& gamedataUrl)
{
    auto pos = gamedataUrl.rfind("/latest/");
    if (pos == std::string::npos) return {};
    return gamedataUrl.substr(pos + 8);
}

// The sha256 the manifest publishes for `gamedataUrl`'s file, lowercase hex, or empty when the
// manifest does not list it. A mirror synced file by file can briefly serve a new manifest with an
// old gamedata.json (or the reverse); the hash catches that pair.
inline std::string ManifestSha256For(const nlohmann::json& manifest, const std::string& gamedataUrl)
{
    const auto fileUrl = ManifestFileUrl(gamedataUrl);
    if (fileUrl.empty() || !manifest.is_object()) return {};
    auto files = manifest.find("files");
    if (files == manifest.end() || !files->is_array()) return {};
    for (const auto& file : *files)
    {
        if (!file.is_object() || file.value("url", std::string()) != fileUrl) continue;
        auto sha = file.value("sha256", std::string());
        for (auto& c : sha)
            c = static_cast<char>(tolower(static_cast<unsigned char>(c)));
        return sha;
    }
    return {};
}

// gamedata/gamedata.etag: the ETag on line 1, the mirror it came from on line 2. ETags are per
// server, so one mirror's ETag means nothing to another. Files from before the mirror list have
// no second line; they came from the first (then only) URL.
struct EtagRecord
{
    std::string etag;
    std::string source;
};

inline EtagRecord ParseEtagRecord(const std::string& contents)
{
    EtagRecord record;
    auto newline = contents.find('\n');
    record.etag = contents.substr(0, newline);
    if (newline != std::string::npos)
    {
        auto rest = contents.substr(newline + 1);
        record.source = rest.substr(0, rest.find('\n'));
    }
    for (auto* field : { &record.etag, &record.source })
        while (!field->empty() && (field->back() == '\r' || field->back() == ' '))
            field->pop_back();
    return record;
}

inline std::string FormatEtagRecord(const EtagRecord& record) { return record.etag + "\n" + record.source + "\n"; }

// The ETag to send to `mirror`: only one that mirror handed out.
inline std::string EtagFor(const EtagRecord& record, const std::string& mirror, const std::string& firstMirror)
{
    const auto& source = record.source.empty() ? firstMirror : record.source;
    return source == mirror ? record.etag : std::string();
}

// SHA-256 (FIPS 180-4), lowercase hex. Small and dependency-free: the build links no crypto
// library, and this hashes one ~100 KiB file per start.
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

    std::string message = data;
    const std::uint64_t bitLength = static_cast<std::uint64_t>(data.size()) * 8;
    message.push_back(static_cast<char>(0x80));
    while (message.size() % 64 != 56)
        message.push_back('\0');
    for (int i = 7; i >= 0; --i)
        message.push_back(static_cast<char>((bitLength >> (i * 8)) & 0xFF));

    for (size_t chunk = 0; chunk < message.size(); chunk += 64)
    {
        std::uint32_t w[64];
        for (int i = 0; i < 16; ++i)
        {
            const auto* p = reinterpret_cast<const unsigned char*>(message.data() + chunk + i * 4);
            w[i] = (std::uint32_t(p[0]) << 24) | (std::uint32_t(p[1]) << 16) | (std::uint32_t(p[2]) << 8) | std::uint32_t(p[3]);
        }
        for (int i = 16; i < 64; ++i)
        {
            const auto s0 = rotr(w[i - 15], 7) ^ rotr(w[i - 15], 18) ^ (w[i - 15] >> 3);
            const auto s1 = rotr(w[i - 2], 17) ^ rotr(w[i - 2], 19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16] + s0 + w[i - 7] + s1;
        }

        auto a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
        for (int i = 0; i < 64; ++i)
        {
            const auto t1 = hh + (rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25)) + ((e & f) ^ (~e & g)) + k[i] + w[i];
            const auto t2 = (rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22)) + ((a & b) ^ (a & c) ^ (b & c));
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

    static constexpr char digits[] = "0123456789abcdef";
    std::string out;
    out.reserve(64);
    for (auto word : h)
        for (int shift = 28; shift >= 0; shift -= 4)
            out.push_back(digits[(word >> shift) & 0xF]);
    return out;
}

} // namespace counterstrikesharp::gamedata
