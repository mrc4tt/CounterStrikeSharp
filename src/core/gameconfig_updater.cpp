#include "core/gameconfig_updater.h"
#include "core/utils.h"
#include "core/coreconfig.h"
#include "core/log.h"
#include "core/globals.h"
#include "core/gamedata_custom.h"
#include "core/gamedata_signature_check.h"
#include "core/gamedata_plugin_update.h"
#include "core/gamedata_signature.h"
#include "core/gamedata_update_guard.h"
#include "core/gamedata_update_status.h"
#include "core/gamedata_upload.h"
#include "core/gameconfig.h"
#include "core/memory.h"
#include "core/memory_module.h"
#include "core/vtable_fallback.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstring>
#include <ctime>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iterator>
#include <memory>
#include <mutex>
#include <random>
#include <thread>
#include <utility>
#include <map>
#include <optional>
#include <set>
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
#include <csignal>
#include <dlfcn.h>
#include <fcntl.h>
#include <unistd.h>
#endif

namespace counterstrikesharp::update {

namespace {

// A gamedata.json is ~100 KiB; anything near this is not a gamedata file.
constexpr size_t kMaxBodyBytes = 16 * 1024 * 1024;
constexpr long kConnectTimeoutSeconds = 3;
// One budget for the whole update (manifest + gamedata), not per request: this runs
// synchronously in Load(), and two requests with their own 15s limits could hold up server
// boot for ~30s against a host that accepts connections but answers slowly.
constexpr long kTotalTimeoutSeconds = 15;

std::chrono::steady_clock::time_point g_updateDeadline;

// What the updaters did on this start, for the "patched" block of logs/gamedata-status.json.
// Game thread only: CheckPreviousStart resets it, the updaters and WriteGamedataStatus run after
// it in the same Load().
status::StartRecord g_startRecord;

// Seconds left of the update budget, or 0 when it is spent.
long RemainingBudgetSeconds(const std::chrono::steady_clock::time_point* deadline = nullptr)
{
    auto left =
        std::chrono::duration_cast<std::chrono::seconds>((deadline ? *deadline : g_updateDeadline) - std::chrono::steady_clock::now())
            .count();
    return left > 0 ? static_cast<long>(left) : 0;
}

struct FetchResult
{
    int status = 0;
    std::string etag;
    std::string body;
    std::string error;
};

// A plain GET unless told otherwise. Every request runs against the update budget
// (g_updateDeadline) unless `deadline` names another.
struct FetchOptions
{
    const std::string* body = nullptr; // JSON request body: POST, unless `method` says otherwise
    const char* method = nullptr; // e.g. "PUT"
    std::string header; // one extra request header, "Name: value"
    // Off for anything that must come from one host only (the upload read links): a redirect
    // would hand the request, and the answer, to another.
    bool followRedirects = true;
    const std::chrono::steady_clock::time_point* deadline = nullptr;
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
constexpr int OPT_POSTFIELDS = 10015;
constexpr int OPT_POSTFIELDSIZE = 60;
constexpr int OPT_CUSTOMREQUEST = 10036;
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
    static std::once_flag attempted;
    static std::string loadError;

    // The gamedata report fetches from its own thread (ReportGamedata), possibly while a later
    // `meta load` runs the updater on the game thread.
    std::call_once(attempted, [] {
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
    });

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

FetchResult Fetch(const ParsedUrl&, const std::string& fullUrl, const std::string& knownETag, const FetchOptions& options = {})
{
    FetchResult result;
    const auto* postJson = options.body;
    const auto* deadline = options.deadline;

    const long budget = RemainingBudgetSeconds(deadline);
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
    if (postJson)
    {
        headers = api->slist_append(headers, "Content-Type: application/json");
        // POSTFIELDS does not copy: *postJson outlives easy_perform below.
        api->easy_setopt(easy, curl::OPT_POSTFIELDSIZE, static_cast<long>(postJson->size()));
        api->easy_setopt(easy, curl::OPT_POSTFIELDS, postJson->data());
    }
    if (!options.header.empty()) headers = api->slist_append(headers, options.header.c_str());
    if (options.method) api->easy_setopt(easy, curl::OPT_CUSTOMREQUEST, options.method);

    api->easy_setopt(easy, curl::OPT_URL, fullUrl.c_str());
    api->easy_setopt(easy, curl::OPT_FOLLOWLOCATION, options.followRedirects ? 1L : 0L);
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

FetchResult Fetch(const ParsedUrl& url, const std::string& fullUrl, const std::string& knownETag, const FetchOptions& options = {})
{
    FetchResult result;
    const auto* postJson = options.body;
    const auto* deadline = options.deadline;

    const long budget = RemainingBudgetSeconds(deadline);
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
    const std::wstring verb = options.method ? winhttp::Widen(options.method) : postJson ? L"POST" : L"GET";
    winhttp::Handle request(
        WinHttpOpenRequest(connection.get(), verb.c_str(), path.c_str(), nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, flags));
    if (!request)
    {
        result.error = winhttp::Failed("WinHttpOpenRequest");
        return result;
    }
    if (!options.followRedirects)
    {
        DWORD policy = WINHTTP_OPTION_REDIRECT_POLICY_NEVER;
        WinHttpSetOption(request.get(), WINHTTP_OPTION_REDIRECT_POLICY, &policy, sizeof(policy));
    }

    std::wstring headers;
    if (!knownETag.empty()) headers = L"If-None-Match: " + winhttp::Widen(knownETag);
    if (postJson) headers += std::wstring(headers.empty() ? L"" : L"\r\n") + L"Content-Type: application/json";
    if (!options.header.empty()) headers += std::wstring(headers.empty() ? L"" : L"\r\n") + winhttp::Widen(options.header);

    void* data = postJson ? const_cast<char*>(postJson->data()) : WINHTTP_NO_REQUEST_DATA;
    const DWORD dataSize = postJson ? static_cast<DWORD>(postJson->size()) : 0;
    if (!WinHttpSendRequest(request.get(), headers.empty() ? WINHTTP_NO_ADDITIONAL_HEADERS : headers.c_str(),
                            headers.empty() ? 0 : static_cast<DWORD>(-1L), data, dataSize, dataSize, 0))
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
        if (RemainingBudgetSeconds(deadline) == 0)
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

std::string FetchError(const std::string& url, const FetchResult& res)
{
    return "could not fetch " + url + " (" + (res.error.empty() ? "HTTP " + std::to_string(res.status) : res.error) + ")";
}

bool SigningEnabled() { return !globals::coreConfig->GamedataSigningKey.empty(); }

// <site>/latest/manifest.json, which lists the sha256 of every published file. With signing on
// (GamedataSigningKey, see core/gamedata_signature.h) it must come with a manifest.json.sig that
// verifies, and then every file the updaters apply is checked against its sha256 here: an
// unsigned or tampered manifest stops the update rather than downgrading to "trust the host".
std::optional<nlohmann::json> FetchManifest(const std::string& manifestUrl, std::string& error)
{
    auto parsed = ParseUrl(manifestUrl);
    if (!parsed)
    {
        error = "invalid manifest URL " + manifestUrl;
        return std::nullopt;
    }
    auto res = Fetch(*parsed, manifestUrl, "");
    if (!res.error.empty() || res.status != 200)
    {
        error = FetchError(manifestUrl, res);
        return std::nullopt;
    }

    if (SigningEnabled())
    {
        const auto signatureUrl = manifestUrl + ".sig";
        auto parsedSignature = ParseUrl(signatureUrl);
        auto sig = parsedSignature ? Fetch(*parsedSignature, signatureUrl, "") : FetchResult{};
        if (!sig.error.empty() || sig.status != 200)
        {
            error = "the manifest is not signed (" + FetchError(signatureUrl, sig) +
                    "); set GamedataSigningKey to \"\" in core.json only if this feed is not VibeSignatures'";
            return std::nullopt;
        }
        std::string why;
        if (!signing::VerifySignatureWithAnyKey(res.body, sig.body, globals::coreConfig->GamedataSigningKey, why))
        {
            error = manifestUrl + " failed its signature check: " + why;
            return std::nullopt;
        }
    }

    auto manifest = nlohmann::json::parse(res.body, nullptr, false);
    if (manifest.is_discarded() || !manifest.is_object())
    {
        error = manifestUrl + " is not a JSON object";
        return std::nullopt;
    }
    return manifest;
}

// The sha256 the manifest publishes for <site>/latest/<relative>, empty when it lists no such file.
std::string ManifestSha256(const nlohmann::json& manifest, const std::string& relative)
{
    if (!manifest.contains("files") || !manifest["files"].is_array()) return {};
    for (const auto& file : manifest["files"])
        if (file.is_object() && file.value("url", "") == relative && file.contains("sha256") && file["sha256"].is_string())
            return plugin_gamedata::Lower(file["sha256"].get<std::string>());
    return {};
}

struct ServerManifest
{
    nlohmann::json manifest;
    // "latest" or "builds/<build>": where under <site>/ this server's files are.
    std::string directory;
    std::string published;
};

// The signed manifest for the game build this server runs. <site>/latest/ serves the newest
// processed build only, which can be ahead of a server that has not taken the game update yet;
// VibeSignatures also publishes every build as <site>/builds/<build>/ (same layout, same
// signing), so on a mismatch the server asks for its own build there. Signatures for another
// build either fail to resolve or, worse, resolve to the wrong function, so without a match
// nothing is applied.
std::optional<ServerManifest> ManifestForServer(const std::string& site, const std::string& serverTag, std::string& reason)
{
    auto read = [&](const std::string& directory, std::string& error) -> std::optional<ServerManifest> {
        const auto manifestUrl = site + "/" + directory + "/manifest.json";
        auto fetched = FetchManifest(manifestUrl, error);
        if (!fetched) return std::nullopt;
        if (!fetched->contains("gameVersion") || !(*fetched)["gameVersion"].is_string())
        {
            error = manifestUrl + " has no gameVersion";
            return std::nullopt;
        }
        ServerManifest out{ std::move(*fetched), directory, {} };
        out.published = out.manifest["gameVersion"].get<std::string>();
        return out;
    };

    auto latest = read("latest", reason);
    if (!latest) return std::nullopt;
    if (StripBuildSuffix(latest->published) == serverTag) return latest;

    const auto directory = "builds/" + serverTag;
    std::string error;
    auto own = read(directory, error);
    if (own && StripBuildSuffix(own->published) == serverTag)
    {
        CSSHARP_CORE_INFO("Newest published gamedata is for game build {}; using {}/{}/ for this server's build {}", latest->published,
                          site, directory, serverTag);
        return own;
    }
    reason = "published gamedata is for game build " + latest->published + ", this server runs " + serverTag +
             ", and the feed has no gamedata for " + serverTag + " (" +
             (own ? site + "/" + directory + "/manifest.json names build " + own->published : error) + ")";
    return std::nullopt;
}

// Gates the core update on the game build (see ManifestForServer). feedUrl is the URL to fetch
// gamedata.json from: url itself, or its builds/<build>/ counterpart.
bool GameVersionMatches(
    const std::string& url, std::string& reason, std::string& matchedTag, nlohmann::json& manifestOut, std::string& feedUrl)
{
    feedUrl = url;
    const auto pos = url.rfind("/latest/");
    if (pos == std::string::npos)
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

    auto found = ManifestForServer(url.substr(0, pos), serverTag, reason);
    if (!found) return false;

    CSSHARP_CORE_INFO("Published gamedata is for game build {}, matching this server", found->published);
    feedUrl = url.substr(0, pos) + "/" + found->directory + "/" + url.substr(pos + sizeof("/latest/") - 1);
    matchedTag = serverTag;
    manifestOut = std::move(found->manifest);
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

// "20261106-142233": sorts lexically, safe in a file name.
std::string UtcStamp()
{
    char stamp[32];
    const auto now = std::time(nullptr);
    std::tm utc{};
#ifdef _WIN32
    gmtime_s(&utc, &now);
#else
    gmtime_r(&now, &utc);
#endif
    std::strftime(stamp, sizeof(stamp), "%Y%m%d-%H%M%S", &utc);
    return stamp;
}

// Crash-loop guard state (core/gamedata_update_guard.h). g_pendingUpdate: gamedata/update.pending
// exists and this process is the one to confirm it.
std::atomic<bool> g_pendingUpdate{ false };

// Shared with a detached thread, never destroyed: a joinable std::thread still alive at exit()
// would std::terminate the server, and static mutex/condvar destructors would race the thread.
struct ConfirmTimer
{
    std::mutex mutex;
    std::condition_variable wake;
    bool stop = false;
};
std::shared_ptr<ConfirmTimer> g_confirmTimer;

// Map of this start, for the guard's progress note (game thread writes, timer thread reads).
std::mutex g_progressMutex;
std::string g_currentMap;

bool GuardEnabled() { return globals::coreConfig->GamedataCrashGuard; }

// "in 59m", "in 5h 59m": how long until a rejection ends.
std::string Remaining(std::time_t until)
{
    const auto left = until - std::time(nullptr);
    if (left <= 0) return "now";
    const auto minutes = static_cast<long long>((left + 59) / 60);
    if (minutes < 60) return "in " + std::to_string(minutes) + "m";
    return "in " + std::to_string(minutes / 60) + "h " + std::to_string(minutes % 60) + "m";
}

// Hashes still rejected, empty when the guard is off.
std::set<std::string> RejectedNow()
{
    if (!GuardEnabled()) return {};
    return guard::Rejected(utils::GamedataDirectory());
}

#ifndef _WIN32
// A stop signal is not a crash: mark it, so the next start does not count this one as an early
// death (core/gamedata_update_guard.h). Path built before the handler is installed; the handler
// itself only open/write/close (async-signal-safe), then hands the signal on.
char g_stoppedMarkerPath[4096] = { 0 };
constexpr int kStopSignals[] = { SIGTERM, SIGINT, SIGHUP };
struct sigaction g_prevStop[3];
std::atomic<bool> g_stopHandlerInstalled{ false };

void StopSignalHandler(int sig, siginfo_t* info, void* ucontext)
{
    if (g_pendingUpdate.load(std::memory_order_relaxed) && g_stoppedMarkerPath[0])
    {
        const int fd = open(g_stoppedMarkerPath, O_WRONLY | O_CREAT | O_TRUNC, 0644);
        if (fd >= 0)
        {
            const char* name = sig == SIGTERM ? "SIGTERM\n" : sig == SIGINT ? "SIGINT\n" : "SIGHUP\n";
            ssize_t ignored = write(fd, name, strlen(name));
            (void)ignored;
            close(fd);
        }
    }

    for (size_t i = 0; i < std::size(kStopSignals); ++i)
    {
        if (kStopSignals[i] != sig) continue;
        const auto& prev = g_prevStop[i];
        if (prev.sa_flags & SA_SIGINFO)
        {
            if (prev.sa_sigaction) prev.sa_sigaction(sig, info, ucontext);
        }
        else if (prev.sa_handler == SIG_DFL)
        {
            // Default action (terminate): put it back and deliver the signal again.
            signal(sig, SIG_DFL);
            raise(sig);
        }
        else if (prev.sa_handler != SIG_IGN && prev.sa_handler)
            prev.sa_handler(sig);
        return;
    }
}
#endif

void NotePendingWrite(const std::string& path, const std::string& backup, const std::string& sha256)
{
    if (!GuardEnabled()) return;
    std::string error;
    if (guard::RecordPending(utils::GamedataDirectory(), { path, backup, sha256 }, error)) g_pendingUpdate = true;
    else
        CSSHARP_CORE_WARN("Could not record the gamedata update for the crash-loop guard ({}); it cannot be rolled back automatically",
                          error);
}

// Copies gamedata.json to gamedata/backup/gamedata-<UTC time>.json and keeps the newest kMaxBackups.
bool BackupGamedata(const std::string& gamedataPath, std::string& backupPath, std::string& error)
{
    namespace fs = std::filesystem;
    const fs::path dir = fs::path(utils::GamedataDirectory()) / "backup";
    std::error_code ec;
    fs::create_directories(dir, ec);

    backupPath = (dir / ("gamedata-" + UtcStamp() + ".json")).string();
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

// gamedata/*.json: the one directory CounterStrikeSharp loads gamedata from (GameDataProvider
// merges every file in it) and where plugins read their own file from too. plugins/<Name>/gamedata/
// is deliberately not looked at: it is a build-output copy no one reads (WeaponPaints and
// RetakesAllocator read addons/counterstrikesharp/gamedata/<file>; the plugin folder is at most
// a fallback when that root cannot be found), and a stale copy there only produced false
// "doesn't resolve" reports.
std::vector<std::filesystem::path> GamedataFiles(const std::filesystem::path& root)
{
    namespace fs = std::filesystem;
    std::vector<fs::path> files;
    std::error_code ec;

    for (const auto& entry : fs::directory_iterator(root / "gamedata", ec))
        if (entry.path().extension() == ".json") files.push_back(entry.path());
    return files;
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

/// Checks that the published gamedata targets this server's game build, then fetches
/// AutoUpdateURL with a conditional GET (If-None-Match: <stored ETag>) and, when the
/// server returns a new file, validates it and atomically replaces gamedata/gamedata.json.
/// Runs before gamedata is loaded, so a successful update applies to this start.
bool TryUpdateGameConfig()
{
    const auto& configuredUrl = globals::coreConfig->AutoUpdateURL;
    if (!globals::coreConfig->ReplacedAutoUpdateURL.empty())
        CSSHARP_CORE_WARN("AutoUpdateURL '{}' is upstream's gamedata, which does not match this build; using {} instead. "
                          "Set AutoUpdateURL in configs/core.json to silence this",
                          globals::coreConfig->ReplacedAutoUpdateURL, configuredUrl);
    CSSHARP_CORE_INFO("AutoUpdate enabled, checking for gamedata updates from {}", configuredUrl);

    g_updateDeadline = std::chrono::steady_clock::now() + std::chrono::seconds(kTotalTimeoutSeconds);

    auto& core = g_startRecord.core;
    core = {};
    core.feed = configuredUrl;
    auto outcome = [&](const char* result, std::string detail) {
        core.result = result;
        core.detail = std::move(detail);
    };

    auto url = ParseUrl(configuredUrl);
    if (!url)
    {
        CSSHARP_CORE_ERROR("AutoUpdateURL '{}' is not an http:// or https:// URL", configuredUrl);
        outcome("failed", "AutoUpdateURL is not an http:// or https:// URL");
        return false;
    }

    auto gamedataPath = utils::GamedataDirectory() + "/gamedata.json";
    auto etagPath = utils::GamedataDirectory() + "/gamedata.etag";

    const auto localEdits = CheckLocalEdits(gamedataPath);
    if (localEdits == LocalEdits::Edited)
    {
        CSSHARP_CORE_WARN("gamedata.json differs from the copy the updater last wrote (edited by hand, or replaced by a deploy). "
                          "The next gamedata update replaces it: keys the feed does not have move to gamedata/custom/_rescued.json "
                          "and the old file is backed up to gamedata/backup/, but changed values are kept only in the backup. "
                          "Put your own signatures/offsets in gamedata/custom/ instead.");
    }

    std::string reason;
    std::string matchedTag;
    nlohmann::json manifest;
    std::string feedUrl;
    if (!GameVersionMatches(configuredUrl, reason, matchedTag, manifest, feedUrl))
    {
        CSSHARP_CORE_WARN("Gamedata update skipped, keeping the current gamedata.json: {}", reason);
        outcome("skipped", reason);
        return false;
    }
    core.feed = feedUrl;
    if (manifest.is_object() && manifest.contains("gameVersion") && manifest["gameVersion"].is_string())
        core.build = manifest["gameVersion"].get<std::string>();

    // Without a gamedata.json there is nothing a 304 could refer to; ask for the full file.
    auto localETag = std::filesystem::exists(gamedataPath) ? ReadFirstLine(etagPath) : std::string();

    auto feed = ParseUrl(feedUrl);
    if (!feed)
    {
        CSSHARP_CORE_ERROR("Gamedata feed URL '{}' is not an http:// or https:// URL", feedUrl);
        outcome("failed", "the feed URL is not an http:// or https:// URL");
        return false;
    }

    auto res = Fetch(*feed, feedUrl, localETag);
    if (!res.error.empty())
    {
        CSSHARP_CORE_ERROR("Gamedata update from {} failed: {}", feedUrl, res.error);
        outcome("failed", res.error);
        return false;
    }

    if (res.status == 304)
    {
        CSSHARP_CORE_INFO("Gamedata is up to date, ETag: {}", localETag);
        RecordGamedataBuild(matchedTag, gamedataPath);
        outcome("up-to-date", "ETag " + localETag);
        return true;
    }

    if (res.status != 200)
    {
        CSSHARP_CORE_ERROR("Gamedata update from {} failed: HTTP {}", feedUrl, res.status);
        outcome("failed", "HTTP " + std::to_string(res.status));
        return false;
    }

    if (!res.etag.empty() && res.etag == localETag)
    {
        CSSHARP_CORE_INFO("Gamedata is up to date, ETag: {}", localETag);
        RecordGamedataBuild(matchedTag, gamedataPath);
        outcome("up-to-date", "ETag " + localETag);
        return true;
    }

    const auto bodySha256 = plugin_gamedata::Sha256Hex(res.body);
    if (SigningEnabled())
    {
        // GameVersionMatches only passes a signed manifest (or none: a URL outside <site>/latest/,
        // which cannot be vouched for at all).
        const auto latest = configuredUrl.rfind("/latest/");
        const auto expected = latest == std::string::npos || manifest.is_null()
                                  ? std::string()
                                  : ManifestSha256(manifest, configuredUrl.substr(latest + sizeof("/latest/") - 1));
        if (expected.empty())
        {
            CSSHARP_CORE_ERROR("Gamedata update from {} rejected: no signed manifest lists this file (set GamedataSigningKey to \"\" "
                               "in core.json only for a feed that is not VibeSignatures')",
                               feedUrl);
            outcome("rejected", "no signed manifest lists this file");
            return false;
        }
        if (expected != bodySha256)
        {
            CSSHARP_CORE_ERROR("Gamedata update from {} rejected: its sha256 is not the one the signed manifest lists "
                               "(published mid-download, or tampered with); the next start tries again",
                               feedUrl);
            outcome("rejected", "its sha256 is not the one the signed manifest lists");
            return false;
        }
    }

    if (RejectedNow().count(bodySha256))
    {
        const auto when = Remaining(guard::RejectedUntil(utils::GamedataDirectory(), bodySha256));
        CSSHARP_CORE_WARN("Gamedata update skipped: this gamedata.json (sha256 {}) was rolled back after the server died early "
                          "with it twice. It is tried again {} (or at once when a newer one is published); delete "
                          "gamedata/update.rejected to try it now",
                          bodySha256, when);
        outcome("skipped",
                "this file (sha256 " + bodySha256 + ") was rolled back after the server died early with it twice; retried " + when);
        return false;
    }

    std::string error;
    if (!ValidateGamedata(res.body, gamedataPath, error))
    {
        CSSHARP_CORE_ERROR("Gamedata update from {} rejected: {}", feedUrl, error);
        outcome("rejected", error);
        return false;
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
        CSSHARP_CORE_ERROR("Gamedata update from {} rejected, keeping the current gamedata.json: {}", feedUrl, error);
        outcome("rejected", error);
        return false;
    }

    // Always backed up now: it is what the crash-loop guard (core/gamedata_update_guard.h) restores.
    std::string backupPath;
    if (std::filesystem::exists(gamedataPath))
    {
        if (!BackupGamedata(gamedataPath, backupPath, error))
        {
            CSSHARP_CORE_ERROR("Gamedata update skipped, the local gamedata.json could not be backed up: {}", error);
            outcome("failed", "could not back up the current file: " + error);
            return false;
        }
        if (localEdits == LocalEdits::Edited || !rescued.empty())
            CSSHARP_CORE_INFO("Gamedata: backed up the local gamedata.json to {}", backupPath);
    }

    if (!rescued.empty())
    {
        std::string rescuedPath;
        if (!WriteRescuedKeys(rescued, rescuedPath, error))
        {
            CSSHARP_CORE_ERROR("Gamedata update skipped, keys not in the feed could not be moved to custom/: {}", error);
            outcome("failed", "could not move keys the feed does not have to custom/: " + error);
            return false;
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
        outcome("failed", error);
        return false;
    }
    NotePendingWrite(gamedataPath, backupPath, bodySha256);

    // An empty ETag is still written: it clears a stale one, so the next start does a full GET.
    if (!WriteAtomically(etagPath, res.etag, error))
    {
        CSSHARP_CORE_WARN("Gamedata updated, but the ETag could not be stored ({}); the next start re-downloads it", error);
    }

    RecordGamedataBuild(matchedTag, gamedataPath);
    RecordWrittenHash(gamedataPath);

    CSSHARP_CORE_INFO("Gamedata file written to: {} with ETag {}", gamedataPath, res.etag);
    outcome("updated", res.etag.empty() ? std::string() : "ETag " + res.etag);
    core.sha256 = bodySha256;
    core.backup = backupPath;
    return true;
}

/// Crash-loop guard, first half: runs before any gamedata is updated or read. See
/// core/gamedata_update_guard.h.
void CheckPreviousStart()
{
    const auto directory = utils::GamedataDirectory();
    const auto gamedataPath = directory + "/gamedata.json";
    // A new start (or `meta load`): nothing updated yet.
    g_startRecord = {};
    if (!globals::coreConfig->AutoUpdateEnabled) g_startRecord.core.detail = "AutoUpdateEnabled is off";
    {
        std::lock_guard lock(g_progressMutex);
        g_currentMap.clear();
    }

    if (!GuardEnabled())
    {
        // Off: nothing pending can be confirmed later, and old rejections must not block updates.
        guard::Disable(directory);
        g_pendingUpdate = false;
        return;
    }

#ifdef _WIN32
    constexpr auto platform = "windows";
#else
    constexpr auto platform = "linux";
#endif
    auto check = guard::CheckPreviousStart(directory, platform);
    auto relativeOf = [](const std::string& path) {
        std::error_code ec;
        auto relative = std::filesystem::relative(path, utils::GetRootDirectory(), ec).generic_string();
        return ec || relative.empty() ? path : relative;
    };
    for (const auto& path : check.restored)
        g_startRecord.rolledBack.push_back(relativeOf(path));
    for (const auto& error : check.errors)
        CSSHARP_CORE_WARN("Gamedata crash-loop guard: {}", error);

    // "after 95s on de_inferno", "within 180s (before any map loaded)"
    std::string howFar = check.lastUptime >= 0 ? "after about " + std::to_string(check.lastUptime) + "s"
                                               : "within " + std::to_string(guard::kConfirmAfterSeconds) + "s";
    howFar += check.lastMap.empty() ? std::string(" (before any map loaded)") : " on " + check.lastMap;

    g_pendingUpdate = check.outcome == guard::Outcome::Retry || check.outcome == guard::Outcome::Stopped;
    switch (check.outcome)
    {
        case guard::Outcome::Clean:
            break;
        case guard::Outcome::Stopped:
            CSSHARP_CORE_INFO("The previous start was stopped ({}) {} before its gamedata update was confirmed; not counted as a "
                              "crash, the update stays",
                              check.stopSignal.empty() ? "stop signal" : check.stopSignal, howFar);
            break;
        case guard::Outcome::Retry:
            CSSHARP_CORE_WARN("The previous start stopped {} after applying a gamedata update, without a clean shutdown or stop "
                              "signal. Keeping the update for one more start; if this one dies early too, the files it changed "
                              "are rolled back (gamedata/update.pending)",
                              howFar);
            break;
        case guard::Outcome::Innocent:
        {
            std::string kept;
            for (const auto& path : check.kept)
                kept += (kept.empty() ? "" : ", ") + relativeOf(path);
            CSSHARP_CORE_WARN("The server died early {} starts in a row after a gamedata update (last one {}), but the update "
                              "changed no {} signature or offset ({}). Gamedata is not the cause; the update stays. Look at "
                              "plugins or the map instead",
                              check.earlyDeaths, howFar, platform, kept.empty() ? "nothing pending" : kept);
            break;
        }
        case guard::Outcome::RolledBack:
        {
            std::string restored;
            for (const auto& path : check.restored)
                restored += (restored.empty() ? "" : ", ") + relativeOf(path);
            CSSHARP_CORE_ERROR("The server died early {} starts in a row after a gamedata update (last one {}). Rolled back: {}. "
                               "Those exact files are tried again {}, or at once when a newer one is published "
                               "(gamedata/update.rejected; delete it to retry now)",
                               check.earlyDeaths, howFar, restored.empty() ? "nothing" : restored,
                               check.rejectedUntil ? Remaining(check.rejectedUntil) : "never");
            for (const auto& suspect : check.suspects)
            {
                std::string keys;
                constexpr size_t kMaxListed = 20;
                for (size_t i = 0; i < suspect.keys.size() && i < kMaxListed; ++i)
                    keys += (keys.empty() ? "" : ", ") + suspect.keys[i];
                if (suspect.keys.size() > kMaxListed) keys += ", +" + std::to_string(suspect.keys.size() - kMaxListed) + " more";
                CSSHARP_CORE_ERROR("  {}: {} key(s) changed by that update, one of them the likely cause: {}", relativeOf(suspect.path),
                                   suspect.keys.size(), keys);
            }
            if (!check.kept.empty())
            {
                std::string kept;
                for (const auto& path : check.kept)
                    kept += (kept.empty() ? "" : ", ") + relativeOf(path);
                CSSHARP_CORE_WARN("  Kept (no {} signature or offset changed): {}", platform, kept);
            }

            if (std::find(check.restored.begin(), check.restored.end(), gamedataPath) != check.restored.end())
            {
                // The restored file is not the one the ETag describes, and the updater cannot vouch for its
                // build any more; it is, however, what the updater last put there.
                std::error_code ec;
                std::filesystem::remove(directory + "/gamedata.etag", ec);
                RecordGamedataBuild("", gamedataPath);
                RecordWrittenHash(gamedataPath);
            }
            break;
        }
    }
}

void NoteMapStart(const char* map)
{
    if (!map || !*map) return;
    {
        std::lock_guard lock(g_progressMutex);
        g_currentMap = map;
    }
    if (g_pendingUpdate) guard::NoteProgress(utils::GamedataDirectory(), -1, map);
}

void InstallStopSignalMarker()
{
#ifndef _WIN32
    if (!GuardEnabled() || g_stopHandlerInstalled.exchange(true)) return;
    const auto path = utils::GamedataDirectory() + "/" + guard::kStoppedMarker;
    if (path.size() >= sizeof(g_stoppedMarkerPath))
    {
        g_stopHandlerInstalled = false;
        return;
    }
    std::memcpy(g_stoppedMarkerPath, path.c_str(), path.size() + 1);

    struct sigaction sa;
    std::memset(&sa, 0, sizeof(sa));
    sa.sa_sigaction = StopSignalHandler;
    sa.sa_flags = SA_SIGINFO | SA_RESTART;
    sigemptyset(&sa.sa_mask);
    for (size_t i = 0; i < std::size(kStopSignals); ++i)
        sigaction(kStopSignals[i], &sa, &g_prevStop[i]);
#endif
}

void RemoveStopSignalMarker()
{
#ifndef _WIN32
    if (!g_stopHandlerInstalled.exchange(false)) return;
    for (size_t i = 0; i < std::size(kStopSignals); ++i)
    {
        // Only when ours is still the handler: one installed after us chains to ours and keeps it.
        struct sigaction current;
        if (sigaction(kStopSignals[i], nullptr, &current) == 0 && (current.sa_flags & SA_SIGINFO) &&
            current.sa_sigaction == StopSignalHandler)
            sigaction(kStopSignals[i], &g_prevStop[i], nullptr);
    }
#endif
}

/// Crash-loop guard, second half: once this start has run kConfirmAfterSeconds, the update it
/// applied (or retried) is kept for good. A thread, not a game-frame check, so a hibernating server
/// confirms too; a crash takes it down with the process, which is the point.
void StartUpdateConfirmTimer()
{
    StopUpdateConfirmTimer(false);
    if (!g_pendingUpdate) return;

    auto timer = std::make_shared<ConfirmTimer>();
    g_confirmTimer = timer;
    const auto directory = utils::GamedataDirectory();
    std::thread([timer, directory] {
        // Uptime every few seconds, so a rollback can say how far the dead start got.
        constexpr int kProgressEverySeconds = 10;
        const auto started = std::chrono::steady_clock::now();
        std::unique_lock lock(timer->mutex);
        for (int elapsed = 0; elapsed < guard::kConfirmAfterSeconds; elapsed += kProgressEverySeconds)
        {
            const auto until = started + std::chrono::seconds((std::min)(elapsed + kProgressEverySeconds, guard::kConfirmAfterSeconds));
            if (timer->wake.wait_until(lock, until, [&] {
                return timer->stop;
            }))
                return;
            std::string map;
            {
                std::lock_guard progress(g_progressMutex);
                map = g_currentMap;
            }
            guard::NoteProgress(directory, (std::min)(elapsed + kProgressEverySeconds, guard::kConfirmAfterSeconds), map);
        }
        guard::Confirm(directory);
        g_pendingUpdate = false;
        // No logging from here: the log sinks are not ours to call from a foreign thread.
    }).detach();
}

/// A clean shutdown (or `meta unload`) is not a crash: confirm whatever is pending.
void StopUpdateConfirmTimer(bool cleanShutdown)
{
    if (auto timer = std::exchange(g_confirmTimer, nullptr))
    {
        {
            std::lock_guard lock(timer->mutex);
            timer->stop = true;
        }
        timer->wake.notify_all();
    }

    if (cleanShutdown) RemoveStopSignalMarker();
    if (cleanShutdown && g_pendingUpdate)
    {
        guard::Confirm(utils::GamedataDirectory());
        g_pendingUpdate = false;
    }
}

namespace {

// The plugin update gets its own budget, after the core one: it only goes to the network when a
// plugin file has a broken signature, and then fetches the manifest, the index and one small file
// per broken plugin.
constexpr long kPluginTotalTimeoutSeconds = 10;

// "<site>" of "<site>/latest/CounterStrikeSharp/gamedata.json", empty for any other URL.
std::string FeedSiteFor(const std::string& url)
{
    auto pos = url.rfind("/latest/");
    return pos == std::string::npos ? std::string() : url.substr(0, pos);
}

std::optional<std::string> FetchBody(const std::string& url, std::string& error)
{
    auto parsed = ParseUrl(url);
    if (!parsed)
    {
        error = "invalid URL " + url;
        return std::nullopt;
    }
    auto res = Fetch(*parsed, url, "");
    if (!res.error.empty() || res.status != 200)
    {
        error = "could not fetch " + url + " (" + (res.error.empty() ? "HTTP " + std::to_string(res.status) : res.error) + ")";
        return std::nullopt;
    }
    return std::move(res.body);
}

std::optional<nlohmann::json> FetchJson(const std::string& url, std::string& error)
{
    auto body = FetchBody(url, error);
    if (!body) return std::nullopt;
    auto json = nlohmann::json::parse(*body, nullptr, false);
    if (json.is_discarded() || !json.is_object())
    {
        error = url + " is not a JSON object";
        return std::nullopt;
    }
    return json;
}

// gamedata/backup/plugins/<path with '/' as '_'>.<UTC time>.bak, newest kMaxBackups per file.
// ".bak", not ".json": nothing that loads gamedata may pick a backup up.
bool BackupPluginGamedata(const std::string& relative, const std::string& contents, std::string& backupPath, std::string& error)
{
    namespace fs = std::filesystem;
    const fs::path dir = fs::path(utils::GamedataDirectory()) / "backup" / "plugins";
    std::error_code ec;
    fs::create_directories(dir, ec);

    std::string flat = relative;
    std::replace(flat.begin(), flat.end(), '/', '_');
    backupPath = (dir / (flat + "." + UtcStamp() + ".bak")).string();
    if (!WriteAtomically(backupPath, contents, error)) return false;

    std::vector<fs::path> backups;
    for (const auto& entry : fs::directory_iterator(dir, ec))
    {
        const auto name = entry.path().filename().string();
        if (name.size() == flat.size() + 1 + 15 + 4 && name.rfind(flat + ".", 0) == 0 && entry.path().extension() == ".bak")
            backups.push_back(entry.path());
    }
    std::sort(backups.begin(), backups.end());
    while (backups.size() > kMaxBackups)
    {
        fs::remove(backups.front(), ec);
        backups.erase(backups.begin());
    }
    return true;
}

// PluginGamedataFeeds entry for a file: by its path relative to addons/counterstrikesharp
// first, then by bare file name (both compared case-insensitively, '\' read as '/').
const std::string*
ExplicitFeedFor(const std::map<std::string, std::string>& feeds, const std::string& relative, const std::string& fileName)
{
    namespace pg = plugin_gamedata;
    auto normalize = [](std::string text) {
        std::replace(text.begin(), text.end(), '\\', '/');
        return pg::Lower(text);
    };
    const auto wantedPath = normalize(relative);
    const auto wantedName = pg::Lower(fileName);
    const std::string* byName = nullptr;
    for (const auto& [file, feed] : feeds)
    {
        const auto key = normalize(file);
        if (key == wantedPath) return &feed;
        if (key == wantedName) byName = &feed;
    }
    return byName;
}

// gamedata/plugin-gamedata.uploads (core/gamedata_upload.h). Written by the report's thread and
// by the plugin updater; every change re-reads under this lock so neither drops the other's.
std::string UploadsPath() { return utils::GamedataDirectory() + "/plugin-gamedata.uploads"; }

// Never destroyed: the report's detached thread may still hold it while the process exits.
std::mutex& UploadsMutex()
{
    static auto* mutex = new std::mutex;
    return *mutex;
}

std::map<std::string, upload::Record> ReadUploads()
{
    auto json = nlohmann::json::parse(ReadWholeFile(UploadsPath()), nullptr, false);
    return upload::ParseState(json.is_discarded() ? nlohmann::json() : json);
}

bool ChangeUploads(const std::function<void(std::map<std::string, upload::Record>&)>& change, std::string& error)
{
    std::lock_guard lock(UploadsMutex());
    auto records = ReadUploads();
    change(records);
    return WriteAtomically(UploadsPath(), upload::SerializeState(records).dump(2) + "\n", error);
}

// Plugin files often leave "library" out, which means the server module; the regression check
// reads it as given.
bool PluginSignatureResolves(const std::string& library, const std::string& signature)
{
    return SignatureResolvesInProcess(library.empty() ? "server" : library, signature);
}

std::string JoinKeys(const std::vector<std::string>& keys)
{
    std::string out;
    for (const auto& key : keys)
        out += (out.empty() ? "" : ", ") + key;
    return out;
}

} // namespace

/// Fixes the signatures that do not resolve in gamedata/*.json (other than gamedata.json) from the
/// same VibeSignatures site AutoUpdateURL points at. Only broken signatures are touched, and only
/// with one that resolves here; a file that is an unmodified published copy from an earlier build
/// is replaced by this build's (offsets included). See core/gamedata_plugin_update.h for the
/// rules. Runs before gamedata and plugins load.
void TryUpdatePluginGamedata()
{
#ifdef _WIN32
    constexpr auto platform = "windows";
#else
    constexpr auto platform = "linux";
#endif
    namespace fs = std::filesystem;
    namespace pg = plugin_gamedata;

    const auto& config = *globals::coreConfig;
    const auto site = FeedSiteFor(config.AutoUpdateURL);
    if (site.empty())
    {
        CSSHARP_CORE_INFO("Plugin gamedata update skipped: AutoUpdateURL is not a <site>/latest/... feed URL");
        return;
    }

    // Idempotent; globals::Initialize() calls it again later.
    modules::Initialize();

    const fs::path root = utils::GetRootDirectory();
    const auto coreFile = root / "gamedata" / "gamedata.json";

    struct LocalFile
    {
        fs::path path;
        std::string relative;
        std::string text;
        std::vector<std::string> sha256s; // pg::ContentSha256s(text)
        pg::ordered_json json;
        size_t broken = 0;
    };

    std::vector<LocalFile> files;
    size_t brokenFiles = 0;
    for (const auto& path : GamedataFiles(root))
    {
        if (path == coreFile) continue; // the core updater's, replaced whole

        LocalFile file;
        file.path = path;
        std::error_code ec;
        file.relative = fs::relative(path, root, ec).generic_string();
        if (ec) file.relative = path.filename().generic_string();
        file.text = ReadWholeFile(path.string());
        // Strict JSON only: a file with comments would lose them when written back.
        file.json = pg::ordered_json::parse(file.text, nullptr, false);
        if (file.json.is_discarded() || !file.json.is_object()) continue;

        file.sha256s = pg::ContentSha256s(file.text);
        file.broken = pg::BrokenSignatures(file.json, platform, SignatureResolvesInProcess).size();
        if (file.broken > 0) ++brokenFiles;
        files.push_back(std::move(file));
    }

    // Which feed file each local file was patched from last time: a patched file no longer
    // matches any published hash. Not ".json", so no gamedata reader picks it up. "_build": the
    // game build every file was last looked at for (below).
    const auto statePath = utils::GamedataDirectory() + "/plugin-gamedata.state";
    auto state = nlohmann::json::parse(ReadWholeFile(statePath), nullptr, false);
    if (state.is_discarded() || !state.is_object()) state = nlohmann::json::object();
    bool stateChanged = false;

    std::string error;
    const auto serverTag = files.empty() ? std::string() : ServerGameVersionTag(error);
    if (!files.empty() && serverTag.empty())
    {
        if (brokenFiles > 0)
            CSSHARP_CORE_WARN("Plugin gamedata update skipped: could not determine this server's game version ({})", error);
        return;
    }

    // Offsets go stale on a game update without any signature breaking, and only a published copy
    // can have them refreshed (DecideWholeFileRefresh). So once per game build every file is
    // looked at; otherwise only files with a broken signature go to the network.
    const bool buildCheck = !files.empty() && upload::Text(state, "_build") != serverTag;
    if (brokenFiles == 0 && !buildCheck)
    {
        CSSHARP_CORE_DEBUG("Plugin gamedata: every signature resolves and build {} was checked already, nothing to update", serverTag);
        return;
    }

    g_updateDeadline = std::chrono::steady_clock::now() + std::chrono::seconds(kPluginTotalTimeoutSeconds);

    // Same gate as the core update (ManifestForServer): this server's build, from latest/ or
    // builds/<build>/.
    auto found = ManifestForServer(site, serverTag, error);
    if (!found)
    {
        if (brokenFiles > 0) CSSHARP_CORE_WARN("Plugin gamedata update skipped: {}", error);
        else
            CSSHARP_CORE_INFO("Plugin gamedata check for build {} skipped, retried next start: {}", serverTag, error);
        return;
    }
    const auto* manifest = &found->manifest;
    const auto feedBase = site + "/" + found->directory + "/";
    const auto feed = pg::ParseManifestFiles(*manifest);
    auto feedSha256 = [&](const std::string& url) {
        auto it = std::find_if(feed.begin(), feed.end(), [&](const pg::FeedFile& f) {
            return f.url == url;
        });
        return it == feed.end() ? std::string() : it->sha256;
    };

    auto hints = nlohmann::json::parse(ReadWholeFile(utils::GamedataDirectory() + "/plugin-gamedata.report"), nullptr, false);
    if (hints.is_discarded() || !hints.is_object()) hints = nlohmann::json::object();

    // This server's own uploads (GamedataUploadEnabled), read from the GamedataReportURL host only.
    std::map<std::string, upload::Record> uploads;
    std::optional<std::string> uploadOrigin;
    if (config.GamedataUploadEnabled)
    {
        uploads = ReadUploads();
        std::string why;
        uploadOrigin = upload::ServiceOrigin(config.GamedataReportURL, why);
        if (!uploadOrigin && !uploads.empty()) CSSHARP_CORE_WARN("Plugin gamedata: uploaded copies not used: {}", why);
    }

    // Every build's published files: identification by hash, and whether a file is an older copy.
    std::map<std::string, std::set<std::string>> hashes;
    std::map<std::string, std::vector<pg::Publication>> publications;
    bool indexFetched = false;
    if (auto index = FetchJson(site + "/gamedata/index.json", error))
    {
        hashes = pg::ParseIndexHashes(*index);
        publications = pg::ParseIndexPublications(*index);
        indexFetched = true;
    }
    else
        CSSHARP_CORE_WARN("Plugin gamedata: {}; identifying plugin files by name only, offsets are not updated", error);

    struct FeedBody
    {
        pg::ordered_json json;
        std::string body;
    };
    std::map<std::string, std::optional<FeedBody>> feedCache;
    auto loadFeed = [&](const std::string& url) -> const FeedBody* {
        auto cached = feedCache.find(url);
        if (cached != feedCache.end()) return cached->second ? &*cached->second : nullptr;

        auto& slot = feedCache[url];
        std::string fetchError;
        auto body = FetchBody(feedBase + url, fetchError);
        if (!body)
        {
            CSSHARP_CORE_WARN("Plugin gamedata: {}", fetchError);
            return nullptr;
        }
        const auto expected = feedSha256(url);
        if (SigningEnabled() && expected.empty())
        {
            CSSHARP_CORE_WARN("Plugin gamedata: {} is not in the signed manifest, not used", url);
            return nullptr;
        }
        if (!expected.empty() && pg::Sha256Hex(*body) != expected)
        {
            CSSHARP_CORE_WARN("Plugin gamedata: {} does not match the manifest's sha256 (changed mid-publish?), not used", url);
            return nullptr;
        }
        auto json = pg::ordered_json::parse(*body, nullptr, false);
        if (json.is_discarded() || !json.is_object())
        {
            CSSHARP_CORE_WARN("Plugin gamedata: {} is not a JSON object, not used", url);
            return nullptr;
        }
        slot = FeedBody{ std::move(json), std::move(*body) };
        return &*slot;
    };

    size_t filesPatched = 0, filesRefreshed = 0, keysPatched = 0, keysLeft = 0;
    std::map<std::string, std::string> uploadsWritten; // relative path -> sha256 written
    // Read links the service answered 404 for (deleted, or expired unread after 90 days): dropped,
    // so the next report uploads the file again.
    std::set<std::string> uploadsGone;
    // A file that should have been replaced with this build's and was not (network, disk): the
    // build check runs again next start.
    bool checkAgain = false;

    // Backup, write, crash-loop guard: the same for every way a file gets written.
    auto writeFile = [&](LocalFile& file, const std::string& text, status::FilePatch patch) {
        const auto textSha256 = pg::Sha256Hex(text);
        if (RejectedNow().count(textSha256))
        {
            CSSHARP_CORE_WARN("Plugin gamedata: {} not updated: this exact file was rolled back after the server died early with it "
                              "twice. It is tried again {}; delete gamedata/update.rejected to try it now",
                              file.relative, Remaining(guard::RejectedUntil(utils::GamedataDirectory(), textSha256)));
            return false;
        }

        std::string writeError;
        std::string backupPath;
        if (!BackupPluginGamedata(file.relative, file.text, backupPath, writeError))
        {
            CSSHARP_CORE_ERROR("Plugin gamedata: {} not updated, it could not be backed up: {}", file.relative, writeError);
            return false;
        }
        if (!WriteAtomically(file.path.string(), text, writeError))
        {
            CSSHARP_CORE_ERROR("Plugin gamedata: {} not updated: {}", file.relative, writeError);
            return false;
        }
        NotePendingWrite(file.path.string(), backupPath, textSha256);
        if (uploads.count(file.relative)) uploadsWritten[file.relative] = textSha256;

        patch.file = file.relative;
        patch.platform = platform;
        patch.backup = backupPath;
        patch.sha256 = textSha256;
        g_startRecord.files.push_back(std::move(patch));
        return true;
    };

    // Signature-only patch of `file` from `source`, written back in the file's own style.
    auto patchSignatures = [&](LocalFile& file, const pg::ordered_json& source, status::FilePatch patch) {
        auto result = pg::PatchBrokenSignatures(file.json, source, platform, SignatureResolvesInProcess);
        for (const auto& reason : result.unresolved)
            CSSHARP_CORE_WARN("Plugin gamedata: {} {} ({} {})", file.relative, reason, patch.source, patch.feed);
        keysLeft += result.unresolved.size();
        if (result.patched.empty()) return;

        std::string text;
        try
        {
            text = pg::Serialize(file.json, file.text);
        }
        catch (const std::exception& ex)
        {
            CSSHARP_CORE_ERROR("Plugin gamedata: {} not updated, could not serialize it: {}", file.relative, ex.what());
            keysLeft += result.patched.size();
            return;
        }

        const auto sourceName = patch.source;
        const auto feedName = patch.feed;
        const auto matchedBy = patch.matchedBy;
        patch.mode = "signatures";
        patch.keys = status::SignatureChanges(result.patched, platform);
        if (!writeFile(file, text, std::move(patch)))
        {
            keysLeft += result.patched.size();
            return;
        }
        CSSHARP_CORE_INFO("Plugin gamedata: {} fixed {} {} signature(s) from {} {} (matched by {}, backup {}): {}", file.relative,
                          result.patched.size(), platform, sourceName, feedName, matchedBy, g_startRecord.files.back().backup,
                          JoinKeys(result.patched));
        ++filesPatched;
        keysPatched += result.patched.size();
    };

    for (auto& file : files)
    {
        if (file.broken == 0 && !buildCheck) continue;

        const auto fileName = file.path.filename().string();
        const auto* explicitUrl = ExplicitFeedFor(config.PluginGamedataFeeds, file.relative, fileName);

        // The feed this file was patched from before, else the one the last gamedata report's
        // answer named (ReportGamedata).
        std::string remembered;
        if (state.contains(file.relative) && state[file.relative].is_string()) remembered = state[file.relative].get<std::string>();
        else if (hints.contains(file.relative) && hints[file.relative].is_string())
            remembered = hints[file.relative].get<std::string>();
        auto candidates = pg::FindCandidates(fileName, file.sha256s, feed, hashes, explicitUrl, remembered);

        // This server's upload of the file (GamedataUploadEnabled) beats a guess by memory or name,
        // but not the operator's mapping or a published copy. Unsigned: signatures only.
        const bool strongMatch = candidates.match == pg::Match::Explicit || candidates.match == pg::Match::Hash;
        auto uploaded = uploadOrigin && !strongMatch ? uploads.find(file.relative) : uploads.end();
        if (uploaded != uploads.end())
        {
            if (file.broken == 0) continue;
            const auto readUrl = upload::ReadUrl(*uploadOrigin, uploaded->second);
            FetchOptions oneHost;
            oneHost.followRedirects = false;
            const auto parsed = ParseUrl(readUrl);
            auto res = parsed ? Fetch(*parsed, readUrl, "", oneHost) : FetchResult{ 0, {}, {}, "invalid URL" };
            auto json = res.error.empty() && res.status == 200 ? pg::ordered_json::parse(res.body, nullptr, false) : pg::ordered_json();
            if (json.is_object())
            {
                status::FilePatch patch;
                patch.source = "upload";
                patch.feed = readUrl;
                patch.matchedBy = "upload";
                patchSignatures(file, json, std::move(patch));
                continue;
            }
            CSSHARP_CORE_WARN("Plugin gamedata: could not use the uploaded copy of {} ({}); trying the signed feed", file.relative,
                              !res.error.empty()  ? res.error
                              : res.status != 200 ? "HTTP " + std::to_string(res.status) + " from " + readUrl
                                                  : std::string("not a JSON object"));
            if (res.error.empty() && res.status == 404) uploadsGone.insert(file.relative);
        }

        // A file without broken signatures is only here for the build check, which concerns
        // published copies: nothing to say about the rest.
        const bool quiet = file.broken == 0;
        if (quiet && !strongMatch) continue;

        if (candidates.urls.empty())
        {
            CSSHARP_CORE_WARN("Plugin gamedata: {} has {} signature(s) that don't resolve, not updated: {}", file.relative, file.broken,
                              candidates.note);
            keysLeft += file.broken;
            continue;
        }

        // A file name alone is weak evidence (every other plugin calls its file gamedata.json), so a
        // name match must also share keys with the local file, and the most of them among several.
        std::string chosen = candidates.urls.front();
        if (candidates.urls.size() > 1 || candidates.match == pg::Match::Name)
        {
            std::vector<const pg::ordered_json*> options;
            for (const auto& url : candidates.urls)
            {
                const auto* loaded = loadFeed(url);
                options.push_back(loaded ? &loaded->json : nullptr);
            }
            const int picked = pg::PickByKeyOverlap(file.json, options);
            if (picked < 0)
            {
                std::string names = JoinKeys(candidates.urls);
                if (quiet) CSSHARP_CORE_DEBUG("Plugin gamedata: {} could be any of {}, not refreshed", file.relative, names);
                else if (picked == pg::kOverlapNone)
                    CSSHARP_CORE_WARN("Plugin gamedata: {} shares its name but no keys with {}, not updated; name its feed file in "
                                      "PluginGamedataFeeds in core.json if it has one",
                                      file.relative, names);
                else
                    CSSHARP_CORE_WARN("Plugin gamedata: {} could be any of {} ({}); name one in PluginGamedataFeeds in core.json",
                                      file.relative, names, pg::MatchName(candidates.match));
                keysLeft += file.broken;
                continue;
            }
            chosen = candidates.urls[picked];
        }

        // Remember the identification even when nothing could be fixed this time: the next build may.
        if (remembered != chosen && candidates.match != pg::Match::Explicit)
        {
            state[file.relative] = chosen;
            stateChanged = true;
        }

        // Our own unmodified file from an earlier build: take this build's whole, offsets included.
        const auto decision =
            pg::DecideWholeFileRefresh(file.sha256s, chosen, found->published, feedSha256(chosen), SigningEnabled(), publications);
        if (decision.refresh)
        {
            const auto* loaded = loadFeed(chosen);
            std::string refused;
            if (loaded)
            {
                const auto dropped = pg::DroppedKeys(file.json, loaded->json);
                const auto check = CheckSignatureRegressions(file.json, loaded->json, platform, PluginSignatureResolves);
                if (!dropped.empty()) refused = "build " + found->published + "'s file drops " + JoinKeys(dropped);
                else if (!check.regressions.empty())
                    refused = JoinKeys(check.regressions);
            }
            else
            {
                refused = "this build's file could not be fetched";
                checkAgain = true;
            }

            if (refused.empty())
            {
                status::FilePatch patch;
                patch.mode = "file";
                patch.feed = chosen;
                patch.build = found->published;
                patch.fromBuild = decision.fromBuild;
                patch.matchedBy = pg::MatchName(candidates.match);
                patch.keys = pg::DiffEntries(file.json, loaded->json);
                const auto changed = patch.keys.size();
                if (writeFile(file, loaded->body, std::move(patch)))
                {
                    CSSHARP_CORE_INFO("Plugin gamedata: {} was the published {} for build {}, unmodified; replaced with build {}'s "
                                      "(signatures and offsets, {} key(s) changed, backup {})",
                                      file.relative, chosen, decision.fromBuild, found->published, changed,
                                      g_startRecord.files.back().backup);
                    ++filesRefreshed;
                    continue;
                }
                checkAgain = true;
            }
            else
                CSSHARP_CORE_WARN("Plugin gamedata: {} not replaced with build {}'s {} ({}); signatures only", file.relative,
                                  found->published, chosen, refused);
        }
        else if (quiet)
        {
            CSSHARP_CORE_DEBUG("Plugin gamedata: {} ({}) not refreshed: {}", file.relative, chosen, decision.why);
            continue;
        }

        if (file.broken == 0) continue;

        const auto* loaded = loadFeed(chosen);
        if (!loaded)
        {
            keysLeft += file.broken;
            continue;
        }
        status::FilePatch patch;
        patch.feed = chosen;
        patch.build = found->published;
        patch.matchedBy = pg::MatchName(candidates.match);
        patchSignatures(file, loaded->json, std::move(patch));
    }

    // Every file has been looked at for this build; offsets need not be looked at again until the
    // next one. Not when the index could not be fetched (then nothing could be judged), nor when a
    // replacement failed.
    if (buildCheck && indexFetched && !checkAgain)
    {
        state["_build"] = serverTag;
        stateChanged = true;
    }

    if (stateChanged && !WriteAtomically(statePath, state.dump(2) + "\n", error))
        CSSHARP_CORE_WARN("Plugin gamedata: could not record which feed files were used ({})", error);

    // The updater's own writes are not local changes: the next report must not re-upload them.
    if ((!uploadsWritten.empty() || !uploadsGone.empty()) && !ChangeUploads([&](std::map<std::string, upload::Record>& records) {
        for (const auto& [file, sha] : uploadsWritten)
            if (auto it = records.find(file); it != records.end()) it->second.written = sha;
        for (const auto& file : uploadsGone)
            records.erase(file);
    }, error))
        CSSHARP_CORE_WARN("Plugin gamedata: could not record the updated files in plugin-gamedata.uploads ({})", error);

    if (keysPatched > 0 || keysLeft > 0 || filesRefreshed > 0)
        CSSHARP_CORE_INFO("Plugin gamedata: fixed {} signature(s) in {} file(s), replaced {} unmodified file(s) with this build's, "
                          "{} signature(s) still don't resolve",
                          keysPatched, filesPatched, filesRefreshed, keysLeft);
}

namespace {

// The report runs on its own thread and holds up nothing, so it can afford a retry: one start
// on a host whose DNS or route is not ready yet used to lose its report to a single timeout.
constexpr long kReportTimeoutSeconds = 20;
constexpr int kReportAttempts = 3;
constexpr auto kReportRetryDelay = std::chrono::seconds(2);

// Worth another attempt: no answer at all, or the proxy in front of the service gave up on it.
// A 4xx (a bad report, the rate limit) answers the same the next time.
bool ReportRetryable(const FetchResult& res)
{
    if (!res.error.empty()) return res.error != "update time budget spent";
    return res.status == 502 || res.status == 503 || res.status == 504 || (res.status >= 520 && res.status <= 524);
}
constexpr size_t kReportMaxFiles = 200;

// A random id, kept in gamedata/report.id, so VibeSignatures can count servers rather than
// reports. It names nothing: the service stores only a truncated hash of it.
std::string ReportInstallId()
{
    const auto path = utils::GamedataDirectory() + "/report.id";
    auto id = ReadFirstLine(path);
    if (id.size() == 32 && std::all_of(id.begin(), id.end(), [](char c) {
        return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
    }))
        return id;

    static constexpr char hex[] = "0123456789abcdef";
    std::random_device random;
    id.clear();
    for (int i = 0; i < 32; ++i)
        id += hex[random() & 0xf];

    std::string error;
    if (!WriteAtomically(path, id + "\n", error)) CSSHARP_CORE_WARN("Gamedata report: could not store the install id ({})", error);
    return id;
}

// GamedataUploadEnabled (core/gamedata_upload.h). The service stores 2 files a minute per address
// (burst 3), and it and nginx cap a body at 256 KiB, JSON escaping included.
constexpr size_t kMaxUploadsPerStart = 3;
constexpr size_t kMaxUploadBytes = 200 * 1024;
constexpr long kUploadTimeoutSeconds = 20;

struct UploadCandidate
{
    std::string relative;
    std::string fileName;
    std::string text;
    std::string sha256;
};

// One POST /api/files or PUT /api/files/manage; the checked answer, or nullopt with status/error.
std::optional<upload::Record> SendUpload(const std::string& url,
                                         const char* method,
                                         const std::string& header,
                                         const nlohmann::json& body,
                                         const upload::Record* existing,
                                         const std::chrono::steady_clock::time_point& deadline,
                                         int& status,
                                         std::string& error)
{
    std::string payload;
    try
    {
        payload = body.dump();
    }
    catch (const std::exception& ex)
    {
        error = std::string("the file cannot be sent as JSON (") + ex.what() + ")";
        return std::nullopt;
    }

    FetchOptions options;
    options.body = &payload;
    options.method = method;
    options.header = header;
    options.followRedirects = false;
    options.deadline = &deadline;
    const auto parsed = ParseUrl(url);
    auto res = parsed ? Fetch(*parsed, url, "", options) : FetchResult{ 0, {}, {}, "invalid URL " + url };
    status = res.status;
    if (!res.error.empty() || (res.status != 200 && res.status != 201))
    {
        error = res.error.empty() ? "HTTP " + std::to_string(res.status) : res.error;
        auto answer = nlohmann::json::parse(res.body, nullptr, false);
        if (const auto why = upload::Text(answer, "error"); !why.empty()) error += ": " + why;
        return std::nullopt;
    }
    return upload::ParseAnswer(nlohmann::json::parse(res.body, nullptr, false), existing, error);
}

// Sends the text of the files the report answer called unknown to the GamedataReportURL host,
// unless this server uploaded that exact file already, and records where it can be read back.
// Runs on the report's thread; touches only its own copies and plugin-gamedata.uploads.
void UploadUnknownFiles(const std::string& reportUrl, const std::string& build, const std::vector<UploadCandidate>& files)
{
    std::string error;
    const auto origin = upload::ServiceOrigin(reportUrl, error);
    if (!origin)
    {
        CSSHARP_CORE_WARN("Gamedata upload skipped: {}", error);
        return;
    }

    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(kUploadTimeoutSeconds);
    std::map<std::string, upload::Record> records;
    {
        std::lock_guard lock(UploadsMutex());
        records = ReadUploads();
    }

    size_t sent = 0;
    for (size_t i = 0; i < files.size(); ++i)
    {
        const auto& file = files[i];
        const auto it = records.find(file.relative);
        const auto* existing = it == records.end() ? nullptr : &it->second;
        auto action = upload::Decide(existing, file.sha256);
        if (action == upload::Action::None)
        {
            CSSHARP_CORE_DEBUG("Gamedata upload: {} was uploaded already and has not changed", file.relative);
            continue;
        }
        if (file.text.size() > kMaxUploadBytes)
        {
            CSSHARP_CORE_WARN("Gamedata upload: {} is larger than {} KiB, not uploaded", file.relative, kMaxUploadBytes / 1024);
            continue;
        }
        if (sent == kMaxUploadsPerStart)
        {
            CSSHARP_CORE_INFO("Gamedata upload: at most {} file(s) per start; the rest go on the next one", kMaxUploadsPerStart);
            break;
        }
        ++sent;

        int status = 0;
        std::optional<upload::Record> stored;
        if (action == upload::Action::Replace)
        {
            // Same links, new text: the plugin was updated since the upload.
            stored = SendUpload(upload::ManageUrl(*origin), "PUT", "X-Manage-Token: " + existing->manage,
                                upload::UploadBody(file.text, file.fileName, build), existing, deadline, status, error);
            // 404: the service no longer has it (deleted on its manage page, or unread for 90 days).
            if (!stored && status == 404) action = upload::Action::Create;
        }
        if (action == upload::Action::Create)
        {
            stored = SendUpload(upload::CreateUrl(*origin), nullptr, "", upload::UploadBody(file.text, file.fileName, build), nullptr,
                                deadline, status, error);
            // The service only takes builds it has binaries for; a server ahead of it sends none, and
            // the service starts from its newest.
            if (!stored && status == 400 && error.find("build") != std::string::npos)
                stored = SendUpload(upload::CreateUrl(*origin), nullptr, "", upload::UploadBody(file.text, file.fileName, ""), nullptr,
                                    deadline, status, error);
        }
        if (!stored)
        {
            CSSHARP_CORE_WARN("Gamedata upload of {} to {} failed: {}", file.relative, *origin, error);
            continue;
        }

        stored->sha256 = file.sha256;
        stored->written.clear();
        stored->build = build;
        stored->at = static_cast<long long>(std::time(nullptr));
        const auto record = *stored;
        if (!ChangeUploads([&](std::map<std::string, upload::Record>& all) {
            all[file.relative] = record;
        }, error))
        {
            CSSHARP_CORE_WARN("Gamedata upload: {} was uploaded but could not be recorded ({}); it is sent again next start", file.relative,
                              error);
            continue;
        }
        records[file.relative] = record;
        CSSHARP_CORE_INFO("Gamedata upload: {} the text of {} on {} (GamedataUploadEnabled). From the next start its broken "
                          "signatures are fixed from {}, signatures only; the manage token is in gamedata/plugin-gamedata.uploads",
                          action == upload::Action::Replace ? "replaced" : "stored", file.relative, *origin,
                          upload::ReadUrl(*origin, record));
    }
}

} // namespace

/// Tells VibeSignatures which gamedata files this server runs (name, sha256, key names, which
/// keys don't resolve; never the file text), on a thread of its own so boot does not wait. The
/// answer says, per file, whether VibeSignatures generates it, and under which feed file; that is
/// kept in gamedata/plugin-gamedata.report for the next start's plugin update to identify the file
/// by. Files it does not know are recorded there as demand for a generator.
void ReportGamedata()
{
#ifdef _WIN32
    constexpr auto platform = "windows";
#else
    constexpr auto platform = "linux";
#endif
    namespace fs = std::filesystem;
    namespace pg = plugin_gamedata;

    const auto url = globals::coreConfig->GamedataReportURL;
    if (!ParseUrl(url))
    {
        CSSHARP_CORE_WARN("Gamedata report skipped: GamedataReportURL '{}' is not an http:// or https:// URL", url);
        return;
    }

    std::string error;
    const auto build = ServerGameVersionTag(error);
    if (build.empty())
    {
        CSSHARP_CORE_WARN("Gamedata report skipped: {}", error);
        return;
    }

    modules::Initialize();

    const fs::path root = utils::GetRootDirectory();
    const auto coreFile = root / "gamedata" / "gamedata.json";
    auto files = nlohmann::json::array();
    std::vector<std::string> paths;
    // GamedataUploadEnabled: the text of each reported file, in case the answer calls it unknown.
    const bool uploadEnabled = globals::coreConfig->GamedataUploadEnabled;
    std::vector<UploadCandidate> texts;
    for (const auto& path : GamedataFiles(root))
    {
        // Ours: VibeSignatures generates it.
        if (path == coreFile) continue;
        if (paths.size() >= kReportMaxFiles) break;

        const auto text = ReadWholeFile(path.string());
        const auto json = pg::ordered_json::parse(text, nullptr, false);
        if (json.is_discarded() || !json.is_object()) continue;

        auto keys = nlohmann::json::array();
        for (const auto& [key, value] : json.items())
            if (pg::IsGamedataKey(key)) keys.push_back(key);

        std::error_code ec;
        auto relative = fs::relative(path, root, ec).generic_string();
        if (ec) relative = path.filename().generic_string();

        files.push_back({ { "name", path.filename().string() },
                          { "sha256", pg::Sha256Hex(text) },
                          { "keys", std::move(keys) },
                          { "broken", pg::BrokenSignatures(json, platform, SignatureResolvesInProcess) } });
        if (uploadEnabled) texts.push_back({ relative, path.filename().string(), text, pg::Sha256Hex(text) });
        paths.push_back(std::move(relative));
    }

    if (paths.empty())
    {
        CSSHARP_CORE_DEBUG("Gamedata report: no plugin gamedata files to report");
        return;
    }

    nlohmann::json body = { { "build", build }, { "platform", platform }, { "install", ReportInstallId() }, { "files", std::move(files) } };
    std::string payload;
    try
    {
        payload = body.dump();
    }
    catch (const std::exception& ex)
    {
        CSSHARP_CORE_WARN("Gamedata report skipped: {}", ex.what());
        return;
    }

    const auto hintsPath = utils::GamedataDirectory() + "/plugin-gamedata.report";
    std::thread([url, build, uploadEnabled, payload = std::move(payload), paths = std::move(paths), texts = std::move(texts), hintsPath] {
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(kReportTimeoutSeconds);
        const auto parsed = ParseUrl(url);
        FetchOptions post;
        post.body = &payload;
        post.deadline = &deadline;
        auto res = Fetch(*parsed, url, "", post);
        for (int attempt = 1; attempt < kReportAttempts && ReportRetryable(res); ++attempt)
        {
            // Too little left for a connect plus an answer: report the failure we have.
            if (deadline - std::chrono::steady_clock::now() < kReportRetryDelay + std::chrono::seconds(kConnectTimeoutSeconds)) break;
            CSSHARP_CORE_DEBUG("Gamedata report to {}: attempt {} failed ({}), retrying", url, attempt,
                               res.error.empty() ? "HTTP " + std::to_string(res.status) : res.error);
            std::this_thread::sleep_for(kReportRetryDelay);
            res = Fetch(*parsed, url, "", post);
        }
        if (!res.error.empty() || res.status != 200)
        {
            CSSHARP_CORE_WARN("Gamedata report to {} failed: {}", url,
                              res.error.empty() ? "HTTP " + std::to_string(res.status) : res.error);
            return;
        }

        auto answer = nlohmann::json::parse(res.body, nullptr, false);
        if (answer.is_discarded() || !answer.is_object() || !answer.contains("files") || !answer["files"].is_array() ||
            answer["files"].size() != paths.size())
        {
            CSSHARP_CORE_WARN("Gamedata report to {}: the answer is not one entry per reported file", url);
            return;
        }

        auto hints = nlohmann::json::object();
        size_t known = 0, unknown = 0;
        std::vector<UploadCandidate> uploads;
        for (size_t i = 0; i < paths.size(); ++i)
        {
            const auto& entry = answer["files"][i];
            if (!entry.is_object()) continue;
            const auto status = entry.value("status", std::string());
            if ((status == "known" || status == "plugin") && entry.contains("feed") && entry["feed"].is_string())
            {
                hints[paths[i]] = entry["feed"];
                ++known;
            }
            else if (status == "unknown")
            {
                CSSHARP_CORE_INFO("Gamedata report: {} is not one VibeSignatures generates yet; reported", paths[i]);
                ++unknown;
                if (uploadEnabled) uploads.push_back(texts[i]);
            }
        }

        std::string writeError;
        if (!WriteAtomically(hintsPath, hints.dump(2) + "\n", writeError))
            CSSHARP_CORE_WARN("Gamedata report: could not store the answer ({})", writeError);
        CSSHARP_CORE_INFO("Gamedata report sent: {} file(s), {} known to VibeSignatures, {} not yet", paths.size(), known, unknown);

        if (!uploads.empty()) UploadUnknownFiles(url, build, uploads);
    }).detach();
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
    const auto files = GamedataFiles(root);
    std::error_code ec;

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
        // What the updaters wrote on this start, and the core update's outcome (status::StartRecord).
        { "patched", status::ToJson(g_startRecord) },
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
