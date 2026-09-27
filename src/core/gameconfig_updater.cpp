#include "core/gameconfig_updater.h"
#include "core/utils.h"
#include "core/coreconfig.h"
#include "core/log.h"
#include "core/globals.h"
#include "core/gamedata_signature_check.h"
#include "core/memory.h"
#include "core/memory_module.h"

#include <cstring>
#include <filesystem>
#include <fstream>
#include <optional>
#include <string>

#include <nlohmann/json.hpp>

#ifdef _WIN32
#include "httplib/httplib.h"
#else
#include <dlfcn.h>
#endif

namespace counterstrikesharp::update {

namespace {

// A gamedata.json is ~100 KiB; anything near this is not a gamedata file.
constexpr size_t kMaxBodyBytes = 16 * 1024 * 1024;
constexpr long kConnectTimeoutSeconds = 5;
constexpr long kTotalTimeoutSeconds = 15;

struct FetchResult
{
    int status = 0;
    std::string etag;
    std::string body;
    std::string error;
};

struct ParsedUrl
{
    std::string scheme;
    std::string origin; // scheme://host[:port]
    std::string path; // always starts with '/'
};

// AutoUpdateURL used to be a bare origin ("http://gamedata.cssharp.dev") and the file was
// always fetched from "/". It may now carry a path, e.g. the VibeSignatures Pages URL
// ".../latest/CounterStrikeSharp/gamedata.json"; a bare origin still means "/".
std::optional<ParsedUrl> ParseUrl(const std::string& url)
{
    auto schemeEnd = url.find("://");
    if (schemeEnd == std::string::npos || schemeEnd == 0) return std::nullopt;

    ParsedUrl parsed;
    parsed.scheme = url.substr(0, schemeEnd);
    for (auto& c : parsed.scheme)
        c = static_cast<char>(tolower(static_cast<unsigned char>(c)));
    if (parsed.scheme != "http" && parsed.scheme != "https") return std::nullopt;

    auto hostStart = schemeEnd + 3;
    auto pathStart = url.find('/', hostStart);
    if (pathStart == hostStart) return std::nullopt;

    parsed.origin = url.substr(0, pathStart);
    parsed.path = pathStart == std::string::npos ? "/" : url.substr(pathStart);
    return parsed;
}

#ifndef _WIN32

// libcurl is loaded at runtime instead of linked: the Steam Runtime (sniper) the server runs
// in ships libcurl.so.4 with TLS, so https works without adding an OpenSSL build dependency
// or a DT_NEEDED entry to counterstrikesharp.so. The option/info values below are libcurl's
// stable ABI numbers (curl.h: CURLOPTTYPE_* base + index), spelled out because the build
// has no curl headers.
namespace curl {
constexpr int OPT_WRITEDATA = 10001;
constexpr int OPT_URL = 10002;
constexpr int OPT_TIMEOUT = 13;
constexpr int OPT_USERAGENT = 10018;
constexpr int OPT_HTTPHEADER = 10023;
constexpr int OPT_HEADERDATA = 10029;
constexpr int OPT_FOLLOWLOCATION = 52;
constexpr int OPT_MAXREDIRS = 68;
constexpr int OPT_CONNECTTIMEOUT = 78;
constexpr int OPT_NOSIGNAL = 99;
constexpr int OPT_WRITEFUNCTION = 20011;
constexpr int OPT_HEADERFUNCTION = 20079;
constexpr int INFO_RESPONSE_CODE = 0x200002;

using EasyInit = void* (*)();
using EasySetopt = int (*)(void*, int, ...);
using EasyPerform = int (*)(void*);
using EasyGetinfo = int (*)(void*, int, ...);
using EasyCleanup = void (*)(void*);
using EasyStrerror = const char* (*)(int);
using SlistAppend = void* (*)(void*, const char*);
using SlistFreeAll = void (*)(void*);

struct Api
{
    EasyInit easy_init = nullptr;
    EasySetopt easy_setopt = nullptr;
    EasyPerform easy_perform = nullptr;
    EasyGetinfo easy_getinfo = nullptr;
    EasyCleanup easy_cleanup = nullptr;
    EasyStrerror easy_strerror = nullptr;
    SlistAppend slist_append = nullptr;
    SlistFreeAll slist_free_all = nullptr;
};

// The handle is deliberately never dlclose()d: libcurl/OpenSSL register atexit handlers.
const Api* Load(std::string& error)
{
    static Api api;
    static bool attempted = false;
    static std::string loadError;

    if (!attempted)
    {
        attempted = true;
        void* handle = nullptr;
        for (const char* name : { "libcurl.so.4", "libcurl.so", "libcurl-gnutls.so.4" })
        {
            handle = dlopen(name, RTLD_NOW | RTLD_LOCAL);
            if (handle) break;
        }

        if (!handle)
        {
            loadError = "libcurl not found (tried libcurl.so.4, libcurl.so, libcurl-gnutls.so.4)";
        }
        else
        {
            api.easy_init = reinterpret_cast<EasyInit>(dlsym(handle, "curl_easy_init"));
            api.easy_setopt = reinterpret_cast<EasySetopt>(dlsym(handle, "curl_easy_setopt"));
            api.easy_perform = reinterpret_cast<EasyPerform>(dlsym(handle, "curl_easy_perform"));
            api.easy_getinfo = reinterpret_cast<EasyGetinfo>(dlsym(handle, "curl_easy_getinfo"));
            api.easy_cleanup = reinterpret_cast<EasyCleanup>(dlsym(handle, "curl_easy_cleanup"));
            api.easy_strerror = reinterpret_cast<EasyStrerror>(dlsym(handle, "curl_easy_strerror"));
            api.slist_append = reinterpret_cast<SlistAppend>(dlsym(handle, "curl_slist_append"));
            api.slist_free_all = reinterpret_cast<SlistFreeAll>(dlsym(handle, "curl_slist_free_all"));

            if (!api.easy_init || !api.easy_setopt || !api.easy_perform || !api.easy_getinfo || !api.easy_cleanup || !api.easy_strerror ||
                !api.slist_append || !api.slist_free_all)
            {
                loadError = "libcurl is missing an expected symbol";
            }
        }
    }

    if (!loadError.empty())
    {
        error = loadError;
        return nullptr;
    }
    return &api;
}

size_t OnBody(char* data, size_t size, size_t count, void* userdata)
{
    auto* body = static_cast<std::string*>(userdata);
    size_t bytes = size * count;
    if (body->size() + bytes > kMaxBodyBytes) return 0; // aborts the transfer (CURLE_WRITE_ERROR)
    body->append(data, bytes);
    return bytes;
}

size_t OnHeader(char* data, size_t size, size_t count, void* userdata)
{
    auto* etag = static_cast<std::string*>(userdata);
    size_t bytes = size * count;
    std::string line(data, bytes);

    // Every response in a redirect chain delivers its own headers; only the last one counts.
    if (line.rfind("HTTP/", 0) == 0)
    {
        etag->clear();
        return bytes;
    }

    constexpr const char prefix[] = "etag:";
    if (line.size() > sizeof(prefix) - 1 && strncasecmp(line.c_str(), prefix, sizeof(prefix) - 1) == 0)
    {
        auto value = line.substr(sizeof(prefix) - 1);
        auto first = value.find_first_not_of(" \t");
        auto last = value.find_last_not_of(" \t\r\n");
        *etag = first == std::string::npos ? "" : value.substr(first, last - first + 1);
    }
    return bytes;
}
} // namespace curl

FetchResult Fetch(const ParsedUrl&, const std::string& fullUrl, const std::string& knownETag)
{
    FetchResult result;

    const auto* api = curl::Load(result.error);
    if (!api) return result;

    void* easy = api->easy_init();
    if (!easy)
    {
        result.error = "curl_easy_init failed";
        return result;
    }

    void* headers = nullptr;
    if (!knownETag.empty())
    {
        headers = api->slist_append(headers, ("If-None-Match: " + knownETag).c_str());
    }

    api->easy_setopt(easy, curl::OPT_URL, fullUrl.c_str());
    api->easy_setopt(easy, curl::OPT_FOLLOWLOCATION, 1L);
    api->easy_setopt(easy, curl::OPT_MAXREDIRS, 5L);
    api->easy_setopt(easy, curl::OPT_CONNECTTIMEOUT, kConnectTimeoutSeconds);
    api->easy_setopt(easy, curl::OPT_TIMEOUT, kTotalTimeoutSeconds);
    api->easy_setopt(easy, curl::OPT_NOSIGNAL, 1L);
    api->easy_setopt(easy, curl::OPT_USERAGENT, "CounterStrikeSharp-gamedata-updater");
    api->easy_setopt(easy, curl::OPT_WRITEFUNCTION, &curl::OnBody);
    api->easy_setopt(easy, curl::OPT_WRITEDATA, &result.body);
    api->easy_setopt(easy, curl::OPT_HEADERFUNCTION, &curl::OnHeader);
    api->easy_setopt(easy, curl::OPT_HEADERDATA, &result.etag);
    if (headers) api->easy_setopt(easy, curl::OPT_HTTPHEADER, headers);

    int code = api->easy_perform(easy);
    if (code == 0)
    {
        long status = 0;
        api->easy_getinfo(easy, curl::INFO_RESPONSE_CODE, &status);
        result.status = static_cast<int>(status);
    }
    else
    {
        result.error = api->easy_strerror(code);
    }

    if (headers) api->slist_free_all(headers);
    api->easy_cleanup(easy);
    return result;
}

#else

// The Windows build has no TLS backend for httplib, so only http:// URLs work there.
FetchResult Fetch(const ParsedUrl& url, const std::string&, const std::string& knownETag)
{
    FetchResult result;

    if (url.scheme != "http")
    {
        result.error = "https is not supported by the Windows build; use an http:// AutoUpdateURL";
        return result;
    }

    httplib::Client client(url.origin);
    client.set_follow_location(true);
    client.set_connection_timeout(kConnectTimeoutSeconds, 0);
    client.set_read_timeout(kTotalTimeoutSeconds, 0);

    httplib::Headers headers;
    if (!knownETag.empty()) headers.emplace("If-None-Match", knownETag);

    auto res = client.Get(url.path, headers);
    if (!res)
    {
        result.error = httplib::to_string(res.error());
        return result;
    }

    result.status = res->status;
    result.etag = res->get_header_value("ETag");
    if (res->body.size() <= kMaxBodyBytes) result.body = std::move(res->body);
    else
        result.error = "response too large";
    return result;
}

#endif

std::string ReadFirstLine(const std::string& path)
{
    std::ifstream file(path);
    std::string line;
    if (file.is_open()) std::getline(file, line);
    return line;
}

// Write to a sibling temp file and rename over the target, so a crash or full disk mid-write
// never leaves a truncated gamedata.json for the next start to choke on.
bool WriteAtomically(const std::string& path, const std::string& contents, std::string& error)
{
    auto tmpPath = path + ".tmp";
    {
        std::ofstream out(tmpPath, std::ios::binary | std::ios::trunc);
        if (!out.is_open())
        {
            error = "cannot open " + tmpPath;
            return false;
        }
        out << contents;
        out.close();
        if (!out)
        {
            error = "write failed for " + tmpPath;
            std::error_code ignored;
            std::filesystem::remove(tmpPath, ignored);
            return false;
        }
    }

    std::error_code ec;
    std::filesystem::rename(tmpPath, path, ec);
    if (ec)
    {
        error = "rename to " + path + " failed: " + ec.message();
        std::filesystem::remove(tmpPath, ec);
        return false;
    }
    return true;
}

// Reject anything that is not plausibly a gamedata file before it replaces a working one:
// a captive portal page, a 200 error body, or a truncated/foreign JSON.
bool ValidateGamedata(const std::string& body, const std::string& currentPath, std::string& error)
{
    auto incoming = nlohmann::json::parse(body, nullptr, false);
    if (incoming.is_discarded() || !incoming.is_object() || incoming.empty())
    {
        error = "downloaded file is not a JSON object";
        return false;
    }

    std::ifstream currentFile(currentPath);
    if (currentFile.is_open())
    {
        auto current = nlohmann::json::parse(currentFile, nullptr, false);
        if (!current.is_discarded() && current.is_object() && incoming.size() * 2 < current.size())
        {
            error = "downloaded file has " + std::to_string(incoming.size()) + " keys, the current one " + std::to_string(current.size()) +
                    "; refusing to replace it with less than half";
            return false;
        }
    }
    return true;
}

// Resolves a gamedata signature against the binaries loaded in this process, with the same
// scanner and library mapping CGameConfig::ResolveSignature uses once gamedata is live.
bool SignatureResolvesInProcess(const std::string& library, const std::string& signature)
{
    // "engine" is engine2 on disk; the other library names are the file stem.
    const std::string stem = library == "engine" ? "engine2" : library;
    if (stem.empty()) return false;

    auto* module = modules::GetModuleByName(MODULE_PREFIX + stem + MODULE_EXT);
    if (!module) return false;

    if (signature[0] == '@') return signature.size() > 1 && module->FindSymbol(signature.substr(1)) != nullptr;
    return module->FindSignature(signature.c_str()) != nullptr;
}

// Blocks an update that would break a signature the current gamedata resolves. The game
// build already matches (checked first), so a signature that stops resolving is a bad
// publish, not a game update. Offsets cannot be verified this way and are not checked.
bool SignaturesHoldUp(const std::string& body, const std::string& currentPath, std::string& error)
{
    std::ifstream currentFile(currentPath);
    if (!currentFile.is_open()) return true; // nothing working to protect

    auto current = nlohmann::json::parse(currentFile, nullptr, false);
    if (current.is_discarded()) return true; // the current file is broken anyway

    auto incoming = nlohmann::json::parse(body, nullptr, false);

#ifdef _WIN32
    constexpr auto platform = "windows";
#else
    constexpr auto platform = "linux";
#endif

    // Idempotent; globals::Initialize() calls it again later.
    modules::Initialize();

    auto result = CheckSignatureRegressions(current, incoming, platform, SignatureResolvesInProcess);

    for (const auto& key : result.stillBroken)
        CSSHARP_CORE_WARN("Gamedata signature {} does not resolve in either the current or the downloaded file", key);

    if (!result.regressions.empty())
    {
        for (const auto& regression : result.regressions)
            CSSHARP_CORE_ERROR("Gamedata signature regression: {}", regression);
        error = std::to_string(result.regressions.size()) + " signature(s) that resolve today would stop resolving";
        return false;
    }

    CSSHARP_CORE_INFO("Gamedata signature check passed: {} changed, {} scanned, none regress", result.changed, result.scans);
    return true;
}

// "1.41.8.5" (steam.inf PatchVersion) -> "14185", the tag CS2_VibeSignatures names builds by.
std::string ServerGameVersionTag(std::string& error)
{
    auto steamInfPath = utils::GameDirectory() + "/steam.inf";
    std::ifstream file(steamInfPath);
    if (!file.is_open())
    {
        error = "cannot read " + steamInfPath;
        return {};
    }

    std::string line;
    while (std::getline(file, line))
    {
        if (line.rfind("PatchVersion=", 0) != 0) continue;

        std::string tag;
        for (char c : line.substr(sizeof("PatchVersion=") - 1))
        {
            if (isdigit(static_cast<unsigned char>(c))) tag += c;
            else if (c != '.' && c != '\r' && c != ' ')
                break;
        }
        if (tag.empty()) error = "unparsable PatchVersion in " + steamInfPath + ": " + line;
        return tag;
    }

    error = "no PatchVersion in " + steamInfPath;
    return {};
}

// VibeSignatures suffixes a rebuilt gamever with a letter ("14178b"); the patch it targets is
// the numeric part.
std::string StripBuildSuffix(std::string version)
{
    while (!version.empty() && isalpha(static_cast<unsigned char>(version.back())))
        version.pop_back();
    return version;
}

// The VibeSignatures Pages layout is <site>/latest/<plugin>/<file> next to
// <site>/latest/manifest.json, which names the game build the files were generated for.
// Any other URL has no manifest to check against.
std::optional<std::string> ManifestUrlFor(const std::string& url)
{
    auto pos = url.rfind("/latest/");
    if (pos == std::string::npos) return std::nullopt;
    return url.substr(0, pos) + "/latest/manifest.json";
}

// Only lets an update through when the published gamedata was generated for the game build
// this server runs. Pages always serves the newest processed build, which can be ahead of a
// server that has not taken the game update yet (or behind one that just did); signatures for
// another build either fail to resolve or, worse, resolve to the wrong function.
bool GameVersionMatches(const std::string& url, std::string& reason)
{
    auto manifestUrl = ManifestUrlFor(url);
    if (!manifestUrl)
    {
        CSSHARP_CORE_WARN("AutoUpdateURL has no /latest/manifest.json beside it; skipping the game-version check");
        return true;
    }

    std::string error;
    auto serverTag = ServerGameVersionTag(error);
    if (serverTag.empty())
    {
        reason = "could not determine this server's game version (" + error + ")";
        return false;
    }

    auto parsedManifestUrl = ParseUrl(*manifestUrl);
    if (!parsedManifestUrl)
    {
        reason = "invalid manifest URL " + *manifestUrl;
        return false;
    }

    auto res = Fetch(*parsedManifestUrl, *manifestUrl, "");
    if (!res.error.empty() || res.status != 200)
    {
        reason = "could not fetch " + *manifestUrl + " (" + (res.error.empty() ? "HTTP " + std::to_string(res.status) : res.error) + ")";
        return false;
    }

    auto manifest = nlohmann::json::parse(res.body, nullptr, false);
    if (manifest.is_discarded() || !manifest.is_object() || !manifest.contains("gameVersion") || !manifest["gameVersion"].is_string())
    {
        reason = *manifestUrl + " has no gameVersion";
        return false;
    }

    auto published = manifest["gameVersion"].get<std::string>();
    if (StripBuildSuffix(published) != serverTag)
    {
        reason = "published gamedata is for game build " + published + ", this server runs " + serverTag;
        return false;
    }

    CSSHARP_CORE_INFO("Published gamedata is for game build {}, matching this server", published);
    return true;
}

} // namespace

/// Checks that the published gamedata targets this server's game build, then fetches
/// AutoUpdateURL with a conditional GET (If-None-Match: <stored ETag>) and, when the
/// server returns a new file, validates it and atomically replaces gamedata/gamedata.json.
/// Runs before gamedata is loaded, so a successful update applies to this start.
bool TryUpdateGameConfig()
{
    const auto& configuredUrl = globals::coreConfig->AutoUpdateURL;
    CSSHARP_CORE_INFO("AutoUpdate enabled, checking for gamedata updates from {}", configuredUrl);

    auto url = ParseUrl(configuredUrl);
    if (!url)
    {
        CSSHARP_CORE_ERROR("AutoUpdateURL '{}' is not an http:// or https:// URL", configuredUrl);
        return false;
    }

    std::string reason;
    if (!GameVersionMatches(configuredUrl, reason))
    {
        CSSHARP_CORE_WARN("Gamedata update skipped, keeping the current gamedata.json: {}", reason);
        return false;
    }

    auto gamedataPath = utils::GamedataDirectory() + "/gamedata.json";
    auto etagPath = utils::GamedataDirectory() + "/gamedata.etag";

    // Without a gamedata.json there is nothing a 304 could refer to; ask for the full file.
    auto localETag = std::filesystem::exists(gamedataPath) ? ReadFirstLine(etagPath) : std::string();

    auto res = Fetch(*url, configuredUrl, localETag);
    if (!res.error.empty())
    {
        CSSHARP_CORE_ERROR("Gamedata update from {} failed: {}", configuredUrl, res.error);
        return false;
    }

    if (res.status == 304)
    {
        CSSHARP_CORE_INFO("Gamedata is up to date, ETag: {}", localETag);
        return true;
    }

    if (res.status != 200)
    {
        CSSHARP_CORE_ERROR("Gamedata update from {} failed: HTTP {}", configuredUrl, res.status);
        return false;
    }

    if (!res.etag.empty() && res.etag == localETag)
    {
        CSSHARP_CORE_INFO("Gamedata is up to date, ETag: {}", localETag);
        return true;
    }

    std::string error;
    if (!ValidateGamedata(res.body, gamedataPath, error))
    {
        CSSHARP_CORE_ERROR("Gamedata update from {} rejected: {}", configuredUrl, error);
        return false;
    }

    if (!SignaturesHoldUp(res.body, gamedataPath, error))
    {
        CSSHARP_CORE_ERROR("Gamedata update from {} rejected, keeping the current gamedata.json: {}", configuredUrl, error);
        return false;
    }

    if (!WriteAtomically(gamedataPath, res.body, error))
    {
        CSSHARP_CORE_ERROR("Gamedata update failed: {}", error);
        return false;
    }

    // An empty ETag is still written: it clears a stale one, so the next start does a full GET.
    if (!WriteAtomically(etagPath, res.etag, error))
    {
        CSSHARP_CORE_WARN("Gamedata updated, but the ETag could not be stored ({}); the next start re-downloads it", error);
    }

    CSSHARP_CORE_INFO("Gamedata file written to: {} with ETag {}", gamedataPath, res.etag);
    return true;
}
} // namespace counterstrikesharp::update
