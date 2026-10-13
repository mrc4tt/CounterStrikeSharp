#include "core/vtable_fallback.h"

#include "core/log.h"
#include "core/memory_module.h"
#include "core/utils.h"

#include <filesystem>
#include <fstream>
#include <string>
#include <unordered_map>
#include <unordered_set>

namespace counterstrikesharp::gamedata {

namespace {
VtableFallbacks g_fallbacks;
// module path + '\n' + class -> vtable, nullptr cached too: FindVirtualTable scans and warns.
std::unordered_map<std::string, void*> g_vtables;
// One log line per key and kind, not one per scan: CreateVirtualFunctionBySignature scans on
// every MemoryFunction construction.
std::unordered_set<std::string> g_reported;

bool ReportOnce(const char* kind, const std::string& key) { return g_reported.insert(std::string(kind) + key).second; }
} // namespace

void LoadVtableFallbacks(bool gamedataJsonTrusted)
{
#ifdef _WIN32
    constexpr auto platform = "windows";
#else
    constexpr auto platform = "linux";
#endif

    namespace fs = std::filesystem;

    g_fallbacks = {};
    g_vtables.clear();
    g_reported.clear();

    std::error_code ec;
    for (const auto& file : fs::directory_iterator(utils::GamedataDirectory(), ec))
    {
        if (file.path().extension() != ".json") continue;

        std::ifstream in(file.path());
        auto json = nlohmann::json::parse(in, nullptr, false);
        if (json.is_discarded()) continue; // GameDataProvider reports unreadable files

        g_fallbacks.Add(json, platform, gamedataJsonTrusted && file.path().filename() == "gamedata.json");
    }

    for (const auto& signature : g_fallbacks.Conflicting())
        CSSHARP_CORE_WARN("Gamedata signature {} is listed with two different vtable slots; ignoring both", signature);

    CSSHARP_CORE_DEBUG("{} gamedata signature(s) carry a vtable slot; gamedata.json is {}for this game build", g_fallbacks.Size(),
                       gamedataJsonTrusted ? "" : "not verified ");
}

void* ResolveVtableSlot(modules::CModule* module, const VtableSlot& slot)
{
    if (module == nullptr || slot.index < 0) return nullptr;

    const auto cacheKey = module->m_pszPath + '\n' + slot.className;
    auto it = g_vtables.find(cacheKey);
    if (it == g_vtables.end()) it = g_vtables.emplace(cacheKey, module->FindVirtualTable(slot.className)).first;

    auto** vtable = static_cast<void**>(it->second);
    if (vtable == nullptr) return nullptr;

    // A stale index can run past the end of the table. Reading there is still inside the
    // module, but what it finds is the next table or data, so it must not be taken: the slot
    // has to share the vtable's section and point into code.
    void** entry = vtable + slot.index;
    const auto* tableSection = module->FindSectionContaining(vtable);
    if (tableSection == nullptr || module->FindSectionContaining(entry) != tableSection) return nullptr;

    void* function = *entry;
    const auto* code = module->FindSectionContaining(function);
    if (code == nullptr || code->m_szName != ".text") return nullptr;

    return function;
}

void* ApplyVtableFallback(modules::CModule* module, const char* signature, void* scanned)
{
    if (signature == nullptr) return scanned;

    const auto* fallback = g_fallbacks.Find(signature);
    if (fallback == nullptr) return scanned;

    void* slotted = ResolveVtableSlot(module, fallback->slot);

    if (scanned != nullptr)
    {
        if (slotted != nullptr && slotted != scanned && ReportOnce("mismatch:", fallback->key))
        {
            CSSHARP_CORE_WARN("Gamedata {}: the signature resolves to {}, but vtable slot {}[{}] holds {}. Keeping the signature's "
                              "address; if this binding misbehaves, the signature may sit on the wrong function.",
                              fallback->key, scanned, fallback->slot.className, fallback->slot.index, slotted);
        }
        return scanned;
    }

    if (slotted == nullptr) return nullptr;

    if (!fallback->trusted)
    {
        if (ReportOnce("untrusted:", fallback->key))
        {
            CSSHARP_CORE_WARN("Gamedata {}: the signature no longer resolves. Not using vtable slot {}[{}]: the gamedata is not "
                              "verified for this game build, and an index from another build may name a different function.",
                              fallback->key, fallback->slot.className, fallback->slot.index);
        }
        return nullptr;
    }

    if (ReportOnce("fallback:", fallback->key))
    {
        CSSHARP_CORE_WARN("Gamedata {}: the signature no longer resolves, using vtable slot {}[{}] ({}) instead", fallback->key,
                          fallback->slot.className, fallback->slot.index, slotted);
    }
    return slotted;
}

} // namespace counterstrikesharp::gamedata
