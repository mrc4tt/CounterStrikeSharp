#include "core/gameconfig_updater.h"
#include "core/utils.h"
#include "core/coreconfig.h"
#include "core/log.h"
#include "core/globals.h"
#include "core/gamedata_custom.h"
#include "core/gamedata_mirrors.h"
#include "core/gamedata_signature_check.h"
#include "core/gameconfig.h"
#include "core/memory.h"
#include "core/memory_module.h"
#include "core/vtable_fallback.h"

#include <algorithm>
#include <chrono>
#include <cstring>
#include <ctime>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include <nlohmann/json.hpp>

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <winhttp.h>
#else
#include <dlfcn.h>
#endif

namespace counterstrikesharp::update {

namespace {

// A gamedata.json is ~100 KiB; anything near this is not a gamedata file.
constexpr size_t kMaxBodyBytes = 16 * 1024 * 1024;
constexpr long kConnectTimeoutSeconds = 3;
// One budget per mirror (manifest + gamedata), not per request, and a cap over all mirrors: this
// runs synchronously in Load(), and requests with their own limits could hold up server boot for
// a long time against hosts that accept connections but answer slowly.
constexpr long kMirrorTimeoutSeconds = 15;
constexpr long kTotalTimeoutSeconds = 25;

std::chrono::steady_clock::time_point g_updateDeadline;

// Seconds left of the update budget, or 0 when it is spent.
long RemainingBudgetSeconds()
{
    auto left = std::chrono::duration_cast<std::chrono::seconds>(g_updateDeadline - std::chrono::steady_clock::now()).count();
    return left > 0 ? static_cast<long>(left) : 0;
}

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

    const long budget = RemainingBudgetSeconds();
    if (budget == 0)
    {
        result.error = "update time budget spent";
        return result;
    }

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
    api->easy_setopt(easy, curl::OPT_CONNECTTIMEOUT, std::min(kConnectTimeoutSeconds, budget));
    api->easy_setopt(easy, curl::OPT_TIMEOUT, budget);
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

// WinHTTP ships with every Windows install and uses the system certificate store, so https
// works without bundling a TLS library -- the Windows counterpart of loading the Steam
// Runtime's libcurl on Linux. Redirects are followed by WinHTTP's default policy, which
// refuses an https -> http downgrade.
namespace winhttp {
struct HandleCloser
{
    void operator()(void* handle) const { WinHttpCloseHandle(handle); }
};
using Handle = std::unique_ptr<void, HandleCloser>;

std::wstring Widen(const std::string& text)
{
    if (text.empty()) return {};
    int length = MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0);
    std::wstring wide(length, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), wide.data(), length);
    return wide;
}

std::string Narrow(const std::wstring& text)
{
    if (text.empty()) return {};
    int length = WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    std::string narrow(length, '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), narrow.data(), length, nullptr, nullptr);
    return narrow;
}

std::string Failed(const char* call) { return std::string(call) + " failed (WinHTTP error " + std::to_string(GetLastError()) + ")"; }
} // namespace winhttp

FetchResult Fetch(const ParsedUrl& url, const std::string& fullUrl, const std::string& knownETag)
{
    FetchResult result;

    const long budget = RemainingBudgetSeconds();
    if (budget == 0)
    {
        result.error = "update time budget spent";
        return result;
    }

    auto wideUrl = winhttp::Widen(fullUrl);
    URL_COMPONENTS parts{};
    parts.dwStructSize = sizeof(parts);
    parts.dwHostNameLength = static_cast<DWORD>(-1);
    parts.dwUrlPathLength = static_cast<DWORD>(-1);
    parts.dwExtraInfoLength = static_cast<DWORD>(-1);
    if (!WinHttpCrackUrl(wideUrl.c_str(), static_cast<DWORD>(wideUrl.size()), 0, &parts))
    {
        result.error = winhttp::Failed("WinHttpCrackUrl");
        return result;
    }
    std::wstring host(parts.lpszHostName, parts.dwHostNameLength);
    std::wstring path(parts.lpszUrlPath, parts.dwUrlPathLength);
    if (parts.lpszExtraInfo) path.append(parts.lpszExtraInfo, parts.dwExtraInfoLength);
    if (path.empty()) path = L"/";

    winhttp::Handle session(WinHttpOpen(L"CounterStrikeSharp-gamedata-updater", WINHTTP_ACCESS_TYPE_DEFAULT_PROXY, WINHTTP_NO_PROXY_NAME,
                                        WINHTTP_NO_PROXY_BYPASS, 0));
    if (!session)
    {
        result.error = winhttp::Failed("WinHttpOpen");
        return result;
    }

    // WinHTTP has no whole-transfer timeout: each phase gets the remaining budget, and the
    // read loop below checks the deadline between chunks.
    const int budgetMs = static_cast<int>(budget * 1000);
    WinHttpSetTimeouts(session.get(), budgetMs, static_cast<int>((std::min)(kConnectTimeoutSeconds, budget) * 1000), budgetMs, budgetMs);

    DWORD maxRedirects = 5;
    WinHttpSetOption(session.get(), WINHTTP_OPTION_MAX_HTTP_AUTOMATIC_REDIRECTS, &maxRedirects, sizeof(maxRedirects));

    winhttp::Handle connection(WinHttpConnect(session.get(), host.c_str(), parts.nPort, 0));
    if (!connection)
    {
        result.error = winhttp::Failed("WinHttpConnect");
        return result;
    }

    const DWORD flags = url.scheme == "https" ? WINHTTP_FLAG_SECURE : 0;
    winhttp::Handle request(
        WinHttpOpenRequest(connection.get(), L"GET", path.c_str(), nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, flags));
    if (!request)
    {
        result.error = winhttp::Failed("WinHttpOpenRequest");
        return result;
    }

    std::wstring headers;
    if (!knownETag.empty()) headers = L"If-None-Match: " + winhttp::Widen(knownETag);

    if (!WinHttpSendRequest(request.get(), headers.empty() ? WINHTTP_NO_ADDITIONAL_HEADERS : headers.c_str(),
                            headers.empty() ? 0 : static_cast<DWORD>(-1L), WINHTTP_NO_REQUEST_DATA, 0, 0, 0))
    {
        result.error = winhttp::Failed("WinHttpSendRequest");
        return result;
    }
    if (!WinHttpReceiveResponse(request.get(), nullptr))
    {
        result.error = winhttp::Failed("WinHttpReceiveResponse");
        return result;
    }

    // Both headers describe the final response of a redirect chain.
    DWORD status = 0;
    DWORD statusSize = sizeof(status);
    if (!WinHttpQueryHeaders(request.get(), WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_HEADER_NAME_BY_INDEX, &status,
                             &statusSize, WINHTTP_NO_HEADER_INDEX))
    {
        result.error = winhttp::Failed("WinHttpQueryHeaders");
        return result;
    }
    result.status = static_cast<int>(status);

    DWORD etagBytes = 0;
    WinHttpQueryHeaders(request.get(), WINHTTP_QUERY_ETAG, WINHTTP_HEADER_NAME_BY_INDEX, WINHTTP_NO_OUTPUT_BUFFER, &etagBytes,
                        WINHTTP_NO_HEADER_INDEX);
    if (GetLastError() == ERROR_INSUFFICIENT_BUFFER && etagBytes > 0)
    {
        std::wstring etag(etagBytes / sizeof(wchar_t), L'\0');
        if (WinHttpQueryHeaders(request.get(), WINHTTP_QUERY_ETAG, WINHTTP_HEADER_NAME_BY_INDEX, etag.data(), &etagBytes,
                                WINHTTP_NO_HEADER_INDEX))
        {
            etag.resize(etagBytes / sizeof(wchar_t));
            result.etag = winhttp::Narrow(etag);
        }
    }

    char buffer[16 * 1024];
    for (;;)
    {
        if (RemainingBudgetSeconds() == 0)
        {
            result.error = "update time budget spent";
            return result;
        }

        DWORD read = 0;
        if (!WinHttpReadData(request.get(), buffer, sizeof(buffer), &read))
        {
            result.error = winhttp::Failed("WinHttpReadData");
            return result;
        }
        if (read == 0) break;
        if (result.body.size() + read > kMaxBodyBytes)
        {
            result.error = "response too large";
            return result;
        }
        result.body.append(buffer, read);
    }
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
modules::CModule* ModuleForLibrary(const std::string& library)
{
    // "engine" is engine2 on disk; the other library names are the file stem.
    const std::string stem = library == "engine" ? "engine2" : library;
    if (stem.empty()) return nullptr;

    return modules::GetModuleByName(MODULE_PREFIX + stem + MODULE_EXT);
}

// The pattern alone (ScanSignature, not FindSignature): the vtable fallback is judged on its own,
// so a fallback is never reported as the pattern working.
void* ScanInProcess(const std::string& library, const std::string& signature)
{
    auto* module = ModuleForLibrary(library);
    if (!module || signature.empty()) return nullptr;

    if (signature[0] == '@') return signature.size() > 1 ? module->FindSymbol(signature.substr(1)) : nullptr;
    return module->ScanSignature(signature.c_str());
}

bool SignatureResolvesInProcess(const std::string& library, const std::string& signature)
{
    return ScanInProcess(library, signature) != nullptr;
}

void* SlotInProcess(const std::string& library, const gamedata::VtableSlot& slot)
{
    return gamedata::ResolveVtableSlot(ModuleForLibrary(library), slot);
}

// Blocks an update that would break a signature the current gamedata resolves. The game
// build already matches (checked first), so a signature that stops resolving is a bad
// publish, not a game update. Offsets cannot be verified this way and are not checked.
bool SignaturesHoldUp(const std::string& body, const nlohmann::json& current, std::string& error)
{
    if (!current.is_object()) return true; // nothing working to protect, or the current file is broken anyway

    auto incoming = nlohmann::json::parse(body, nullptr, false);

#ifdef _WIN32
    constexpr auto platform = "windows";
#else
    constexpr auto platform = "linux";
#endif

    // Idempotent; globals::Initialize() calls it again later.
    modules::Initialize();

    auto result = CheckSignatureRegressions(current, incoming, platform, SignatureResolvesInProcess,
                                            [](const std::string& library, const gamedata::VtableSlot& slot) {
        return SlotInProcess(library, slot) != nullptr;
    });

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
// `publishedSha` gets the sha256 the manifest lists for the file, empty when it lists none.
bool GameVersionMatches(const std::string& url, std::string& reason, std::string& matchedTag, std::string& publishedSha)
{
    auto manifestUrl = ManifestUrlFor(url);
    if (!manifestUrl)
    {
        CSSHARP_CORE_WARN("{} has no /latest/manifest.json beside it; skipping the game-version check", url);
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
    matchedTag = serverTag;
    publishedSha = gamedata::ManifestSha256For(manifest, url);
    return true;
}

std::string ReadWholeFile(const std::string& path)
{
    std::ifstream in(path, std::ios::binary);
    return { std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>() };
}

std::string GameverPath() { return utils::GamedataDirectory() + "/gamedata.gamever"; }

// Records that gamedata.json, as it is on disk now, was generated for `gameTag` (see
// gamedata::GameverMarker). Without a confirmed tag the marker is removed instead: a file the
// updater cannot vouch for must not keep an older vouch.
void RecordGamedataBuild(const std::string& gameTag, const std::string& gamedataPath)
{
    std::error_code ec;
    if (gameTag.empty())
    {
        std::filesystem::remove(GameverPath(), ec);
        return;
    }

    std::string error;
    if (!WriteAtomically(GameverPath(), gamedata::GameverMarker(gameTag, ReadWholeFile(gamedataPath)), error))
        CSSHARP_CORE_WARN("Could not record the gamedata game build ({}); vtable fallbacks stay off", error);
}

// gamedata/gamedata.written: ContentHash of the gamedata.json this updater last wrote. A
// gamedata.json that no longer matches it was edited by hand, and the next update would replace
// those edits; they belong in gamedata/custom/ (core/gamedata_custom.h).
std::string WrittenHashPath() { return utils::GamedataDirectory() + "/gamedata.written"; }

enum class LocalEdits
{
    None,
    Edited,
    Unknown, // no record yet (first update since this was added): treat like Edited, but say so
};

LocalEdits CheckLocalEdits(const std::string& gamedataPath)
{
    if (!std::filesystem::exists(gamedataPath)) return LocalEdits::None;
    const auto recorded = ReadFirstLine(WrittenHashPath());
    if (recorded.empty()) return LocalEdits::Unknown;
    return recorded == gamedata::ContentHash(ReadWholeFile(gamedataPath)) ? LocalEdits::None : LocalEdits::Edited;
}

void RecordWrittenHash(const std::string& gamedataPath)
{
    std::string error;
    if (!WriteAtomically(WrittenHashPath(), gamedata::ContentHash(ReadWholeFile(gamedataPath)) + "\n", error))
        CSSHARP_CORE_WARN("Could not record the written gamedata.json ({}); local edits will not be detected", error);
}

constexpr size_t kMaxBackups = 5;

// Copies gamedata.json to gamedata/backup/gamedata-<UTC time>.json and keeps the newest kMaxBackups.
bool BackupGamedata(const std::string& gamedataPath, std::string& backupPath, std::string& error)
{
    namespace fs = std::filesystem;
    const fs::path dir = fs::path(utils::GamedataDirectory()) / "backup";
    std::error_code ec;
    fs::create_directories(dir, ec);

    char stamp[32];
    const auto now = std::time(nullptr);
    std::tm utc{};
#ifdef _WIN32
    gmtime_s(&utc, &now);
#else
    gmtime_r(&now, &utc);
#endif
    std::strftime(stamp, sizeof(stamp), "%Y%m%d-%H%M%S", &utc);

    backupPath = (dir / ("gamedata-" + std::string(stamp) + ".json")).string();
    if (!WriteAtomically(backupPath, ReadWholeFile(gamedataPath), error)) return false;

    std::vector<fs::path> backups;
    for (const auto& entry : fs::directory_iterator(dir, ec))
    {
        const auto name = entry.path().filename().string();
        if (name.rfind("gamedata-", 0) == 0 && entry.path().extension() == ".json") backups.push_back(entry.path());
    }
    // The timestamp sorts lexically.
    std::sort(backups.begin(), backups.end());
    while (backups.size() > kMaxBackups)
    {
        fs::remove(backups.front(), ec);
        backups.erase(backups.begin());
    }
    return true;
}

// Adds `rescued` to gamedata/custom/_rescued.json. A key already there is kept as it is: it was
// rescued earlier, and the operator may have fixed it since.
bool WriteRescuedKeys(const nlohmann::json& rescued, std::string& path, std::string& error)
{
    namespace fs = std::filesystem;
    const fs::path dir = fs::path(utils::GamedataDirectory()) / "custom";
    std::error_code ec;
    fs::create_directories(dir, ec);
    path = (dir / "_rescued.json").string();

    nlohmann::json existing = nlohmann::json::object();
    if (fs::exists(path))
    {
        std::ifstream in(path);
        existing = nlohmann::json::parse(in, nullptr, false);
        if (existing.is_discarded() || !existing.is_object())
        {
            error = path + " exists but is not a JSON object; not touching it";
            return false;
        }
    }

    for (const auto& [key, value] : rescued.items())
        if (!existing.contains(key)) existing[key] = value;

    return WriteAtomically(path, existing.dump(2) + "\n", error);
}

} // namespace

std::string ServerGameVersion()
{
    std::string error;
    auto tag = ServerGameVersionTag(error);
    if (tag.empty()) CSSHARP_CORE_WARN("Gamedata: {}", error);
    return tag;
}

bool SignatureResolves(const std::string& library, const std::string& signature) { return SignatureResolvesInProcess(library, signature); }

namespace {

enum class MirrorResult
{
    Updated,
    UpToDate,
    Failed,
};

/// Checks that `mirror`'s published gamedata targets this server's game build, then fetches it
/// with a conditional GET (If-None-Match: the ETag this mirror handed out) and, when it returns a
/// new file, validates it and atomically replaces gamedata/gamedata.json. A failure is logged as a
/// warning and leaves gamedata.json alone, so the next mirror can be tried.
MirrorResult TryMirror(const std::string& mirror, const std::string& firstMirror, LocalEdits localEdits)
{
    CSSHARP_CORE_INFO("Checking for gamedata updates from {}", mirror);

    auto url = ParseUrl(mirror);
    if (!url)
    {
        CSSHARP_CORE_WARN("Gamedata mirror '{}' is not an http:// or https:// URL", mirror);
        return MirrorResult::Failed;
    }

    auto gamedataPath = utils::GamedataDirectory() + "/gamedata.json";
    auto etagPath = utils::GamedataDirectory() + "/gamedata.etag";

    std::string reason;
    std::string matchedTag;
    std::string publishedSha;
    if (!GameVersionMatches(mirror, reason, matchedTag, publishedSha))
    {
        CSSHARP_CORE_WARN("Gamedata mirror {} skipped: {}", mirror, reason);
        return MirrorResult::Failed;
    }

    // Without a gamedata.json there is nothing a 304 could refer to; ask for the full file.
    const auto etagRecord = gamedata::ParseEtagRecord(ReadWholeFile(etagPath));
    auto localETag = std::filesystem::exists(gamedataPath) ? gamedata::EtagFor(etagRecord, mirror, firstMirror) : std::string();

    auto res = Fetch(*url, mirror, localETag);
    if (!res.error.empty())
    {
        CSSHARP_CORE_WARN("Gamedata update from {} failed: {}", mirror, res.error);
        return MirrorResult::Failed;
    }

    if (res.status == 304)
    {
        CSSHARP_CORE_INFO("Gamedata is up to date, ETag: {}", localETag);
        RecordGamedataBuild(matchedTag, gamedataPath);
        return MirrorResult::UpToDate;
    }

    if (res.status != 200)
    {
        CSSHARP_CORE_WARN("Gamedata update from {} failed: HTTP {}", mirror, res.status);
        return MirrorResult::Failed;
    }

    if (!res.etag.empty() && res.etag == localETag)
    {
        CSSHARP_CORE_INFO("Gamedata is up to date, ETag: {}", localETag);
        RecordGamedataBuild(matchedTag, gamedataPath);
        return MirrorResult::UpToDate;
    }

    std::string error;
    if (!publishedSha.empty() && gamedata::Sha256Hex(res.body) != publishedSha)
    {
        CSSHARP_CORE_WARN("Gamedata update from {} rejected: the file does not match the sha256 its manifest publishes "
                          "(mirror mid-sync, or a stale cache)",
                          mirror);
        return MirrorResult::Failed;
    }

    // Another mirror's copy of the file already on disk: nothing to replace, only the ETag to learn.
    if (localEdits != LocalEdits::Edited && res.body == ReadWholeFile(gamedataPath))
    {
        if (!WriteAtomically(etagPath, gamedata::FormatEtagRecord({ res.etag, mirror }), error))
            CSSHARP_CORE_WARN("Could not store the gamedata ETag ({}); the next start re-downloads it", error);
        RecordGamedataBuild(matchedTag, gamedataPath);
        RecordWrittenHash(gamedataPath);
        CSSHARP_CORE_INFO("Gamedata is up to date (same file as {})", mirror);
        return MirrorResult::UpToDate;
    }

    if (!ValidateGamedata(res.body, gamedataPath, error))
    {
        CSSHARP_CORE_WARN("Gamedata update from {} rejected: {}", mirror, error);
        return MirrorResult::Failed;
    }

    nlohmann::json current;
    {
        std::ifstream currentFile(gamedataPath);
        if (currentFile.is_open()) current = nlohmann::json::parse(currentFile, nullptr, false);
    }

    // Keys only the local file has were added by hand (or, when edits are Unknown, may have been):
    // they move to custom/, so they survive the update and do not count as removed below. A key the
    // file shares with the feed is the feed's; a hand-changed value of one is only kept in the backup.
    nlohmann::json rescued = nlohmann::json::object();
    if (localEdits != LocalEdits::None && current.is_object())
    {
        auto incoming = nlohmann::json::parse(res.body, nullptr, false);
        for (const auto& [key, value] : current.items())
            if (!gamedata::IsMetadataKey(key) && !incoming.contains(key)) rescued[key] = value;
        for (const auto& [key, value] : rescued.items())
            current.erase(key);
    }

    if (!SignaturesHoldUp(res.body, current, error))
    {
        CSSHARP_CORE_WARN("Gamedata update from {} rejected, keeping the current gamedata.json: {}", mirror, error);
        return MirrorResult::Failed;
    }

    if (localEdits == LocalEdits::Edited || !rescued.empty())
    {
        std::string backupPath;
        if (!BackupGamedata(gamedataPath, backupPath, error))
        {
            CSSHARP_CORE_ERROR("Gamedata update skipped, the local gamedata.json could not be backed up: {}", error);
            return MirrorResult::Failed;
        }
        CSSHARP_CORE_INFO("Gamedata: backed up the local gamedata.json to {}", backupPath);
    }

    if (!rescued.empty())
    {
        std::string rescuedPath;
        if (!WriteRescuedKeys(rescued, rescuedPath, error))
        {
            CSSHARP_CORE_ERROR("Gamedata update skipped, keys not in the feed could not be moved to custom/: {}", error);
            return MirrorResult::Failed;
        }

        std::string keys;
        for (const auto& [key, value] : rescued.items())
            keys += (keys.empty() ? "" : ", ") + key;
        CSSHARP_CORE_WARN("Gamedata: moved {} key(s) the feed does not have to {}: {}{}", rescued.size(), rescuedPath, keys,
                          localEdits == LocalEdits::Unknown ? ". If you never added these, the feed dropped them; delete the file." : "");
    }

    if (!WriteAtomically(gamedataPath, res.body, error))
    {
        CSSHARP_CORE_ERROR("Gamedata update failed: {}", error);
        return MirrorResult::Failed;
    }

    // An empty ETag is still written: it clears a stale one, so the next start does a full GET.
    if (!WriteAtomically(etagPath, gamedata::FormatEtagRecord({ res.etag, mirror }), error))
    {
        CSSHARP_CORE_WARN("Gamedata updated, but the ETag could not be stored ({}); the next start re-downloads it", error);
    }

    RecordGamedataBuild(matchedTag, gamedataPath);
    RecordWrittenHash(gamedataPath);

    CSSHARP_CORE_INFO("Gamedata file written to: {} from {} with ETag {}", gamedataPath, mirror, res.etag);
    return MirrorResult::Updated;
}

} // namespace

/// Tries the AutoUpdateURLs mirrors in order until one updates gamedata/gamedata.json or confirms
/// it is current. Runs before gamedata is loaded, so a successful update applies to this start;
/// when every mirror fails, the gamedata.json already on disk is used as it is.
bool TryUpdateGameConfig()
{
    const auto& mirrors = globals::coreConfig->AutoUpdateURLs;
    if (mirrors.empty())
    {
        CSSHARP_CORE_WARN("AutoUpdate enabled, but no AutoUpdateURLs are configured");
        return false;
    }
    CSSHARP_CORE_INFO("AutoUpdate enabled, {} gamedata mirror(s)", mirrors.size());

    const auto localEdits = CheckLocalEdits(utils::GamedataDirectory() + "/gamedata.json");
    if (localEdits == LocalEdits::Edited)
    {
        CSSHARP_CORE_WARN("gamedata.json differs from the copy the updater last wrote (edited by hand, or replaced by a deploy). "
                          "The next gamedata update replaces it: keys the feed does not have move to gamedata/custom/_rescued.json "
                          "and the old file is backed up to gamedata/backup/, but changed values are kept only in the backup. "
                          "Put your own signatures/offsets in gamedata/custom/ instead.");
    }

    const auto overallDeadline = std::chrono::steady_clock::now() + std::chrono::seconds(kTotalTimeoutSeconds);
    for (const auto& mirror : mirrors)
    {
        g_updateDeadline = (std::min)(overallDeadline, std::chrono::steady_clock::now() + std::chrono::seconds(kMirrorTimeoutSeconds));
        if (RemainingBudgetSeconds() == 0)
        {
            CSSHARP_CORE_WARN("Gamedata update: time budget spent before {}", mirror);
            break;
        }
        if (TryMirror(mirror, mirrors.front(), localEdits) != MirrorResult::Failed) return true;
    }

    CSSHARP_CORE_ERROR("Gamedata update failed on every mirror, keeping the current gamedata.json");
    return false;
}

bool GamedataMatchesServerBuild()
{
    std::string error;
    const auto serverTag = ServerGameVersionTag(error);
    const auto gamedataPath = utils::GamedataDirectory() + "/gamedata.json";
    return gamedata::GameverMarkerMatches(ReadFirstLine(GameverPath()), serverTag, ReadWholeFile(gamedataPath));
}
void WriteGamedataStatus()
{
#ifdef _WIN32
    constexpr auto platform = "windows";
#else
    constexpr auto platform = "linux";
#endif

    namespace fs = std::filesystem;

    // Idempotent; globals::Initialize() has normally run already.
    modules::Initialize();

    const fs::path root = utils::GetRootDirectory();
    std::vector<fs::path> files;
    std::error_code ec;

    for (const auto& entry : fs::directory_iterator(root / "gamedata", ec))
        if (entry.path().extension() == ".json") files.push_back(entry.path());

    // Plugins that ship their own gamedata (plugins/<Name>/gamedata/<file>.json), switched
    // off ones in plugins/disabled/ too: a host that turns a plugin off because its
    // gamedata is missing must still see when it works again.
    for (const auto& folder : { root / "plugins", root / "plugins" / "disabled" })
    {
        std::error_code outer;
        for (const auto& plugin : fs::directory_iterator(folder, outer))
        {
            if (!plugin.is_directory() || plugin.path() == root / "plugins" / "disabled") continue;
            std::error_code inner;
            for (const auto& entry : fs::directory_iterator(plugin.path() / "gamedata", inner))
                if (entry.path().extension() == ".json") files.push_back(entry.path());
        }
    }

    const bool gamedataTrusted = GamedataMatchesServerBuild();

    auto missing = nlohmann::json::array();
    // Pattern broken, vtable slot still resolves: works today, but the signature needs a fix.
    auto viaVtable = nlohmann::json::array();
    // Pattern and slot both resolve, to different functions: one of them is wrong (or one is a thunk).
    auto vtableMismatch = nlohmann::json::array();
    int checked = 0;

    const auto coreFile = root / "gamedata" / "gamedata.json";

    for (const auto& file : files)
    {
        // Core gamedata is reported as it is in effect, custom/ overrides included.
        const bool isCore = file == coreFile && globals::gameConfig != nullptr;
        nlohmann::json json;
        if (isCore) json = globals::gameConfig->GetJson();
        else
        {
            std::ifstream in(file);
            json = nlohmann::json::parse(in, nullptr, false);
        }
        if (json.is_discarded() || !json.is_object()) continue;

        const auto relative = fs::relative(file, root, ec).generic_string();

        for (const auto& [name, entry] : json.items())
        {
            if (!entry.is_object() || !entry.contains("signatures") || !entry["signatures"].is_object()) continue;

            const auto& signatures = entry["signatures"];
            if (!signatures.contains(platform) || !signatures[platform].is_string()) continue;

            const auto library = signatures.contains("library") && signatures["library"].is_string()
                                     ? signatures["library"].get<std::string>()
                                     : std::string("server");
            const auto signature = signatures[platform].get<std::string>();
            if (signature.empty()) continue;

            checked++;
            void* scanned = ScanInProcess(library, signature);
            const auto slot = gamedata::ParseVtableSlot(entry, platform);
            void* slotted = slot ? SlotInProcess(library, *slot) : nullptr;

            // Same rule as the runtime (vtable_fallback.cpp): only a slot from a gamedata.json
            // proven to be for this game build stands in for a failed signature, and not for a key
            // a custom/ file touched.
            const auto* overrideSource = isCore ? globals::gameConfig->GetOverrideSource(name) : nullptr;
            const bool trusted = file == coreFile && gamedataTrusted && overrideSource == nullptr;

            nlohmann::json row = { { "name", name }, { "file", overrideSource ? "gamedata/" + *overrideSource : relative } };
            if (!scanned && !(slotted && trusted)) missing.push_back(row);
            else if (!scanned)
                viaVtable.push_back(row);
            else if (slotted && slotted != scanned)
                vtableMismatch.push_back(row);
        }
    }

    nlohmann::json status = {
        { "version", 1 },
        { "at", static_cast<long long>(std::time(nullptr)) },
        { "checked", checked },
        { "missing", missing },
        { "viaVtable", viaVtable },
        { "vtableMismatch", vtableMismatch },
        // gamedata.gamever vouches for this gamedata.json on this game build: only then can a
        // vtable slot stand in for a failed signature (vtable_fallback.cpp).
        { "gamedataBuildVerified", gamedataTrusted },
    };

    std::error_code mkdirError;
    fs::create_directories(root / "logs", mkdirError);

    std::string error;
    if (!WriteAtomically((root / "logs" / "gamedata-status.json").string(), status.dump(2) + "\n", error))
    {
        CSSHARP_CORE_WARN("Could not write the gamedata status: {}", error);
        return;
    }

    // "signatures", not "entries": offset-only entries are not scanned here, so this count is
    // the signature half of the managed "Gamedata: N entries ...: S signatures, O offsets" line.
    if (missing.empty()) CSSHARP_CORE_INFO("Gamedata: all {} signatures resolve ({} file(s))", checked, files.size());
    else
        CSSHARP_CORE_WARN("Gamedata status: {} of {} signatures don't resolve (logs/gamedata-status.json)", missing.size(), checked);

    if (!viaVtable.empty())
        CSSHARP_CORE_WARN("Gamedata status: {} signature(s) only resolve through their vtable slot (logs/gamedata-status.json)",
                          viaVtable.size());
    if (!vtableMismatch.empty())
        CSSHARP_CORE_WARN("Gamedata status: {} signature(s) disagree with their vtable slot (logs/gamedata-status.json)",
                          vtableMismatch.size());
}

} // namespace counterstrikesharp::update
