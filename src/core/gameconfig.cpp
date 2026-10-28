#include "core/gameconfig.h"
#include "core/signature_pattern.h"

#include <algorithm>
#include <filesystem>
#include <fstream>
#include <stdexcept>

#include "log.h"

namespace counterstrikesharp {

namespace {
#if _WIN32
constexpr auto kPlatform = "windows";
#else
constexpr auto kPlatform = "linux";
#endif
} // namespace

CGameConfig::CGameConfig(const std::string& path) { m_sPath = path; }

CGameConfig::~CGameConfig() = default;

bool CGameConfig::Init(char* conf_error, int conf_error_size)
{
    std::ifstream ifs(m_sPath);
    if (!ifs)
    {
        V_snprintf(conf_error, conf_error_size, "Gamedata file not found.");
        return false;
    }

    try
    {
        // Inside the try: a truncated / half-written gamedata.json (the auto-updater
        // rewrites it in place) used to throw out of Init() and std::terminate the server.
        m_json = json::parse(ifs);
        if (!m_json.is_object()) throw std::runtime_error("top level is not a JSON object");
        RebuildLookups();
    }
    catch (const std::exception& ex)
    {
        V_snprintf(conf_error, conf_error_size, "Failed to parse gamedata file: %s", ex.what());
        return false;
    }
    return true;
}

void CGameConfig::RebuildLookups()
{
    m_umOffsets.clear();
    m_umSignatures.clear();
    m_umLibraries.clear();
    m_umPatches.clear();
    m_coreDump.clear();

    for (auto& [k, v] : m_json.items())
    {
        if (!v.is_object()) continue;
        if (v.contains("signatures") && v["signatures"].is_object())
        {
            const auto& signatures = v["signatures"];
            if (signatures.contains("library") && signatures["library"].is_string())
            {
                m_umLibraries[k] = signatures["library"].get<std::string>();
            }
            if (signatures.contains(kPlatform) && signatures[kPlatform].is_string())
            {
                m_umSignatures[k] = signatures[kPlatform].get<std::string>();
            }
        }
        if (v.contains("offsets") && v["offsets"].is_object())
        {
            if (const auto& offsets = v["offsets"]; offsets.contains(kPlatform) && offsets[kPlatform].is_number_integer())
            {
                m_umOffsets[k] = offsets[kPlatform].get<std::int64_t>();
            }
        }
        if (v.contains("patches") && v["patches"].is_object())
        {
            if (const auto& patches = v["patches"]; patches.contains(kPlatform) && patches[kPlatform].is_string())
            {
                m_umPatches[k] = patches[kPlatform].get<std::string>();
            }
        }
    }
}

void CGameConfig::ApplyCustomOverrides(const std::string& directory,
                                       const std::string& serverTag,
                                       const update::SignatureResolver& resolves)
{
    namespace fs = std::filesystem;

    m_overrides.clear();

    std::vector<fs::path> paths;
    std::error_code ec;
    for (const auto& entry : fs::directory_iterator(directory, ec))
        if (entry.is_regular_file() && entry.path().extension() == ".json") paths.push_back(entry.path());
    if (paths.empty()) return;

    // Later files win, so the order has to be the same on every host.
    std::sort(paths.begin(), paths.end(), [](const fs::path& a, const fs::path& b) {
        return a.filename() < b.filename();
    });

    std::vector<gamedata::CustomFile> files;
    for (const auto& path : paths)
    {
        const auto name = "custom/" + path.filename().string();
        std::ifstream in(path);
        auto parsed = json::parse(in, nullptr, false);
        if (parsed.is_discarded())
        {
            CSSHARP_CORE_ERROR("Gamedata: skipping {}, it is not valid JSON", name);
            continue;
        }
        files.push_back({ name, std::move(parsed) });
    }

    auto result = gamedata::ApplyCustomFiles(m_json, files, serverTag, kPlatform, resolves);

    for (const auto& skipped : result.skippedFiles)
        CSSHARP_CORE_WARN("Gamedata: skipping {}; core gamedata.json applies instead", skipped);
    for (const auto& warning : result.warnings)
        CSSHARP_CORE_WARN("Gamedata: {}", warning);

    for (const auto& applied : result.applied)
    {
        std::string fields;
        for (const auto& field : applied.fields)
            fields += (fields.empty() ? "" : ", ") + field;
        CSSHARP_CORE_INFO("Gamedata override: {} [{}{}] <- {}", applied.key, applied.added ? "added; " : "", fields, applied.file);
        m_overrides[applied.key] = applied.file;
    }

    RebuildLookups();
}

const char* CGameConfig::GetCoreJson()
{
    if (m_coreDump.empty()) m_coreDump = m_json.dump();
    return m_coreDump.c_str();
}

const std::string* CGameConfig::GetOverrideSource(const std::string& name) const
{
    auto it = m_overrides.find(name);
    return it == m_overrides.end() ? nullptr : &it->second;
}

const std::string CGameConfig::GetPath() { return m_sPath; }

const char* CGameConfig::GetLibrary(const std::string& name)
{
    // My recommendation is switch to C++20.
    auto it = m_umLibraries.find(name);
    if (it == m_umLibraries.end())
    {
        return nullptr;
    }
    return it->second.c_str();
}

const char* CGameConfig::GetSignature(const std::string& name)
{
    auto it = m_umSignatures.find(name);
    if (it == m_umSignatures.end())
    {
        return nullptr;
    }
    return it->second.c_str();
}

const char* CGameConfig::GetSymbol(const char* name)
{
    const char* symbol = this->GetSignature(name);

    if (!symbol || strlen(symbol) <= 1)
    {
        CSSHARP_CORE_ERROR("Missing symbol: {}\n", name);
        return nullptr;
    }
    return symbol + 1;
}

const char* CGameConfig::GetPatch(const std::string& name)
{
    auto it = m_umPatches.find(name);
    if (it == m_umPatches.end())
    {
        return nullptr;
    }
    return it->second.c_str();
}

int CGameConfig::GetOffset(const std::string& name)
{
    auto it = m_umOffsets.find(name);
    if (it == m_umOffsets.end())
    {
        return -1;
    }
    return it->second;
}

void* CGameConfig::GetAddress(const std::string& name, void* engine, void* server, char* error, int maxlen)
{
    CSSHARP_CORE_ERROR("Not implemented.");
    return nullptr;
}

modules::CModule** CGameConfig::GetModule(const char* name)
{
    const char* library = this->GetLibrary(name);
    if (!library) return nullptr;

    if (strcmp(library, "engine") == 0) return &modules::engine;
    else if (strcmp(library, "server") == 0)
        return &modules::server;
    else if (strcmp(library, "vscript") == 0)
        return &modules::vscript;
    else if (strcmp(library, "tier0") == 0)
        return &modules::tier0;

    return nullptr;
}

bool CGameConfig::IsSymbol(const char* name)
{
    const char* sigOrSymbol = this->GetSignature(name);
    if (!sigOrSymbol || strlen(sigOrSymbol) <= 0)
    {
        CSSHARP_CORE_ERROR("Missing signature or symbol: {}\n", name);
        return false;
    }
    return sigOrSymbol[0] == '@';
}

void* CGameConfig::ResolveSignature(const char* name)
{
    modules::CModule** module = this->GetModule(name);
    if (!module || !(*module))
    {
        CSSHARP_CORE_ERROR("Invalid Module {}\n", name);
        return nullptr;
    }

    void* address = nullptr;
    if (this->IsSymbol(name))
    {
        const char* symbol = this->GetSymbol(name);
        if (!symbol)
        {
            CSSHARP_CORE_ERROR("Invalid symbol for {}\n", name);
            return nullptr;
        }
        address = (*module)->FindSymbol(symbol);
    }
    else
    {
        const char* signature = this->GetSignature(name);
        if (!signature)
        {
            CSSHARP_CORE_ERROR("Failed to find signature for {}\n", name);
            return nullptr;
        }

        address = (*module)->FindSignature(signature);
    }

    if (!address)
    {
        CSSHARP_CORE_ERROR("Failed to find address for {}\n", name);
        return nullptr;
    }
    return address;
}

std::string CGameConfig::GetDirectoryName(const std::string& directoryPathInput)
{
    std::string directoryPath = std::string(directoryPathInput);

    size_t found = std::string(directoryPath).find_last_of("/\\");
    if (found != std::string::npos)
    {
        return std::string(directoryPath, found + 1);
    }
    return "";
}

std::vector<int16_t> CGameConfig::HexToByte(std::string_view src)
{
    if (src.empty())
    {
        return {};
    }

    auto hex_char_to_byte = [](char c) -> int16_t {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;

        // a valid hex char can never go up to 0xFF
        return -1;
    };

    std::vector<int16_t> result{};

    const bool is_code_style = src[0] == '\\';

    const std::string_view pattern = is_code_style ? R"(\x)" : " ";
    const std::string_view wildcard = is_code_style ? "2A" : "?";

    std::string::size_type pos = 0;

    while (pos < src.size())
    {
        std::string::size_type found = src.find(pattern, pos);
        if (found == std::string::npos)
        {
            found = src.size();
        }
        std::string_view str = src.substr(pos, found - pos);
        pos = found + pattern.size();

        if (str.empty()) continue;

        std::string byte(str.data(), str.size());

        if (byte == wildcard || (!is_code_style && byte == "??"))
        {
            result.emplace_back(-1);
            continue;
        }

        // Exactly two hex digits per byte. Taking the first two of a longer token ("488B")
        // shortens the pattern without saying so, which is how a signature ends up matching
        // somewhere it was never meant to.
        if (byte.size() != 2)
        {
            CSSHARP_CORE_WARN("Signature contains a malformed byte `{0}`, discarding it", byte);

            return {};
        }

        const auto high = hex_char_to_byte(byte[0]);
        const auto low = hex_char_to_byte(byte[1]);

        // hex_char_to_byte returns -1 (int16) on an invalid char. The old check compared
        // against 0xFF, which never matched, so the -1 flowed through and became a
        // wildcard byte: a typo in gamedata silently matched the wrong address.
        if (high < 0 || low < 0)
        {
            CSSHARP_CORE_WARN("Signature contains an invalid byte `{0}`, discarding it", byte);

            return {};
        }

        result.emplace_back((high << 4) | low);
    }

    return result;
}

std::string CGameConfig::ByteToHex(const std::vector<int16_t>& bytes) { return SignatureToSpacedHex(bytes); }

std::string CGameConfig::FormatSignature(std::string_view src)
{
    // Already IDA-style: echo it verbatim rather than normalising "??" to "?".
    if (src.empty() || src[0] != '\\') return std::string(src);

    const auto bytes = HexToByte(src);
    return bytes.empty() ? std::string(src) : ByteToHex(bytes);
}
} // namespace counterstrikesharp
