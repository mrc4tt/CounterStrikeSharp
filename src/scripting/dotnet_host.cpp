/*
 *  This file is part of CounterStrikeSharp.
 *  CounterStrikeSharp is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  CounterStrikeSharp is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with CounterStrikeSharp.  If not, see <https://www.gnu.org/licenses/>. *
 */

#include "scripting/dotnet_host.h"

#include <dotnet/coreclr_delegates.h>
#include <dotnet/hostfxr.h>

#include <codecvt>
#include <locale>
#include <filesystem>
#include <sstream>
#include <vector>
#include <algorithm>

#ifdef WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <Windows.h>
#include <direct.h>

#define STR(s)        L##s
#define CH(c)         L##c
#define DIR_SEPARATOR L'\\'

#else
#define STR(s)        s
#define CH(c)         c
#define DIR_SEPARATOR '/'

#include <dlfcn.h>
#endif

#include <cassert>
#include <iostream>

#include "core/log.h"
#include "core/utils.h"

#include "utils/string.h"

std::wstring_convert<std::codecvt_utf8_utf16<wchar_t>> converter;

namespace {
hostfxr_initialize_for_runtime_config_fn init_fptr;
hostfxr_get_runtime_delegate_fn get_delegate_fptr;
hostfxr_close_fn close_fptr;
hostfxr_handle cxt;

bool load_hostfxr();
load_assembly_and_get_function_pointer_fn get_dotnet_load_assembly(const char_t* assembly);
} // namespace

namespace {
// Forward declarations
void* load_library(const char_t*);
void* get_export(void*, const char*);

#ifdef _WINDOWS
void* load_library(const char_t* path)
{
    HMODULE h = ::LoadLibraryW(path);
    assert(h != nullptr);
    return (void*)h;
}

void* get_export(void* h, const char* name)
{
    void* f = ::GetProcAddress((HMODULE)h, name);
    if (f == nullptr)
    {
        DWORD error = GetLastError();
        CSSHARP_CORE_WARN("GetProcAddress failed for '{0}': Error code {1}", name, error);
    }
    return f;
}
#else
void* load_library(const char_t* path)
{
    void* h = dlopen(path, RTLD_LAZY | RTLD_LOCAL);
    assert(h != nullptr);
    return h;
}
void* get_export(void* h, const char* name)
{
    void* f = dlsym(h, name);
    assert(f != nullptr);
    return f;
}
#endif

// <SnippetLoadHostFxr>
// Using the nethost library, discover the location of hostfxr and get exports
bool load_hostfxr()
{
    std::string base_dir = counterstrikesharp::utils::GetRootDirectory();
    namespace fs = std::filesystem;
    namespace css = counterstrikesharp;

    const fs::path fxr_root = fs::path(base_dir) / "dotnet" / "host" / "fxr";
    if (!fs::exists(fxr_root) || !fs::is_directory(fxr_root))
    {
        CSSHARP_CORE_CRITICAL("hostfxr root not found at {0}", fxr_root.string().c_str());
        return false;
    }

    auto parse_version = [](const std::string& v) {
        std::vector<int> parts;
        std::stringstream ss(v);
        std::string part;
        while (std::getline(ss, part, '.'))
        {
            try
            {
                parts.push_back(std::stoi(part));
            }
            catch (...)
            {
                return std::vector<int>{};
            }
        }
        return parts;
    };

    auto is_newer = [](const std::vector<int>& a, const std::vector<int>& b) {
        const auto max_sz = std::max(a.size(), b.size());
        for (size_t i = 0; i < max_sz; ++i)
        {
            const int av = i < a.size() ? a[i] : 0;
            const int bv = i < b.size() ? b[i] : 0;
            if (av != bv) return av > bv;
        }
        return false;
    };

    std::string best_version;
    std::vector<int> best_parts;

    for (const auto& entry : fs::directory_iterator(fxr_root))
    {
        if (!entry.is_directory()) continue;

        const auto name = entry.path().filename().string();
        if (name.rfind("8.", 0) != 0) continue;

        const auto parts = parse_version(name);
        if (parts.empty()) continue;

        if (best_version.empty() || is_newer(parts, best_parts))
        {
            best_version = name;
            best_parts = parts;
        }
    }

    if (best_version.empty())
    {
        CSSHARP_CORE_CRITICAL("No 8.x hostfxr version found under {0}", fxr_root.string().c_str());
        return false;
    }

#if _WIN32
    const fs::path fxr_path = fxr_root / best_version / "hostfxr.dll";
    std::wstring buffer = css::widen(fxr_path.string());
    CSSHARP_CORE_INFO("Loading hostfxr from {0}", css::narrow(buffer).c_str());
#else
    const fs::path fxr_path = fxr_root / best_version / "libhostfxr.so";
    std::string buffer = fxr_path.string();
    CSSHARP_CORE_INFO("Loading hostfxr from {0}", buffer.c_str());
#endif

    // Load hostfxr and get desired exports
    void* lib = load_library(buffer.c_str());
    if (lib == nullptr)
    {
#ifdef _WINDOWS
        CSSHARP_CORE_CRITICAL("Failed to load hostfxr library from: {0}", css::narrow(buffer).c_str());
#else
        CSSHARP_CORE_CRITICAL("Failed to load hostfxr library from: {0}", buffer.c_str());
#endif
        return false;
    }
    
    CSSHARP_CORE_INFO("Successfully loaded hostfxr library, getting function exports...");
    
    init_fptr = (hostfxr_initialize_for_runtime_config_fn)get_export(lib, "hostfxr_initialize_for_runtime_config");
    if (init_fptr == nullptr)
    {
        CSSHARP_CORE_CRITICAL("unable to get export function: \"hostfxr_initialize_for_runtime_config\"");
        CSSHARP_CORE_CRITICAL("Possible causes:");
        CSSHARP_CORE_CRITICAL("  1. Wrong .NET runtime version (expected 8.0.x)");
        CSSHARP_CORE_CRITICAL("  2. Corrupted or missing hostfxr.dll");
        CSSHARP_CORE_CRITICAL("  3. Architecture mismatch (must be x64)");
        CSSHARP_CORE_CRITICAL("  4. Incompatible .NET runtime");
        return false;
    }
    get_delegate_fptr = (hostfxr_get_runtime_delegate_fn)get_export(lib, "hostfxr_get_runtime_delegate");
    if (!get_delegate_fptr)
    {
        CSSHARP_CORE_CRITICAL("unable to get export function: \"hostfxr_get_runtime_delegate\"");
        return false;
    }
    close_fptr = (hostfxr_close_fn)get_export(lib, "hostfxr_close");
    if (!close_fptr)
    {
        CSSHARP_CORE_CRITICAL("unable to get export function: \"hostfxr_close\"");
        return false;
    }

    return (init_fptr && get_delegate_fptr && close_fptr);
}
// </SnippetLoadHostFxr>

// <SnippetInitialize>
// Load and initialize .NET Core and get desired function pointer for scenario
load_assembly_and_get_function_pointer_fn get_dotnet_load_assembly(const char_t* config_path)
{
    // Load .NET Core
    void* load_assembly_and_get_function_pointer = nullptr;
    int rc = init_fptr(config_path, nullptr, &cxt);
    if (rc != 0 || cxt == nullptr)
    {
        CSSHARP_CORE_CRITICAL("Init failed: {0:x}", rc);
        close_fptr(cxt);
        return nullptr;
    }

    // Get the load assembly function pointer
    rc = get_delegate_fptr(cxt, hdt_load_assembly_and_get_function_pointer, &load_assembly_and_get_function_pointer);
    if (rc != 0 || load_assembly_and_get_function_pointer == nullptr)
    {
        CSSHARP_CORE_ERROR("Get delegate failed: {0:x}", rc);
    }

    // close_fptr(cxt);
    return (load_assembly_and_get_function_pointer_fn)load_assembly_and_get_function_pointer;
}

} // namespace

CDotNetManager::CDotNetManager() {}

CDotNetManager::~CDotNetManager() {}

bool CDotNetManager::Initialize()
{
    const std::string base_dir = counterstrikesharp::utils::GetRootDirectory();

    CSSHARP_CORE_INFO("Loading .NET runtime...");

    if (!load_hostfxr())
    {
        CSSHARP_CORE_ERROR("Failed to initialize .NET runtime.");
        return false;
    }
    CSSHARP_CORE_INFO(".NET Runtime Initialised.");
    namespace css = counterstrikesharp;
#if _WIN32
    const auto wide_str = std::wstring(css::widen(base_dir) + L"\\api\\CounterStrikeSharp.API.runtimeconfig.json");
    CSSHARP_CORE_INFO("Loading CSS API, Runtime config: {}", counterstrikesharp::narrow(wide_str).c_str());
#else
    std::string wide_str = std::string((base_dir + "/api/CounterStrikeSharp.API.runtimeconfig.json").c_str());
    CSSHARP_CORE_INFO("Loading CSS API, Runtime Config: {}", wide_str);
#endif

    const auto load_assembly_and_get_function_pointer = get_dotnet_load_assembly(wide_str.c_str());
    if (load_assembly_and_get_function_pointer == nullptr)
    {
        CSSHARP_CORE_ERROR("Failed to load CSS API.");
        return false;
    }

#if _WIN32
    const auto dotnetlib_path = std::wstring(css::widen(base_dir) + L"\\api\\CounterStrikeSharp.API.dll");
    CSSHARP_CORE_INFO("CSS API DLL: {}", counterstrikesharp::narrow(dotnetlib_path));
#else
    const std::string dotnetlib_path = std::string((base_dir + "/api/CounterStrikeSharp.API.dll").c_str());
#endif
    const auto dotnet_type = STR("CounterStrikeSharp.API.Bootstrap, CounterStrikeSharp.API");
    // Namespace, assembly name

    typedef int(CORECLR_DELEGATE_CALLTYPE * custom_entry_point_fn)();
    custom_entry_point_fn entry_point = nullptr;
    const int rc = load_assembly_and_get_function_pointer(dotnetlib_path.c_str(), dotnet_type, STR("Run"), UNMANAGEDCALLERSONLY_METHOD,
                                                          nullptr, reinterpret_cast<void**>(&entry_point));
    if (entry_point == nullptr)
    {
        CSSHARP_CORE_ERROR("Trying to get entry point \"Bootstrap::Run\" but failed.");
        return false;
    }

    assert(rc == 0 && entry_point != nullptr && "Failure: load_assembly_and_get_function_pointer()");

    if (const int invoke_result_code = entry_point(); invoke_result_code == 0)
    {
        CSSHARP_CORE_ERROR("Bootstrap::Run return failure.");
        return false;
    }

    CSSHARP_CORE_INFO("CounterStrikeSharp.API Loaded Successfully.");
    return true;
}

void CDotNetManager::UnloadPlugin(PluginContext* context) {}

void CDotNetManager::Shutdown()
{
    // CoreCLR does not currently supporting unloading... :(
    // I think this is intentionally, you should handle Init/Shutdown manually.
    // Better rework in the future, but not now.
}

PluginContext* CDotNetManager::FindContext(std::string path) { return nullptr; }
