/**
 * vim: set ts=4 sw=4 tw=99 noet :
 * ======================================================
 * Metamod:Source Sample Plugin
 * Written by AlliedModders LLC.
 * ======================================================
 *
 * This software is provided 'as-is', without any express or implied warranty.
 * In no event will the authors be held liable for any damages arising from
 * the use of this software.
 *
 * This sample plugin is public domain.
 */

#include "mm_plugin.h"

#include <cstdio>
#include <cstdlib>
#include <mutex>
#include <unordered_set>

#include "core/detours.h"
#include "core/dynamic_hook.h"
#include "core/fatal_reporter.h"
#include "core/coreconfig.h"
#include "core/game_system.h"
#include "core/gameconfig.h"
#include "core/gameconfig_updater.h"
#include "core/global_listener.h"
#include "core/khook_original_return.h"
#include "core/log.h"
#include "core/managers/entity_manager.h"
#include "core/managers/chat_manager.h"
#include "core/managers/player_manager.h"
#include "core/tick_scheduler.h"
#include "core/timer_system.h"
#include "core/utils.h"
#include "entity2/entitysystem.h"
#include <public/eiface.h>
#include "igameeventsystem.h"
#include "interfaces/cs2_interfaces.h"
#include "iserver.h"
#include "scripting/callback_manager.h"
#include "scripting/dotnet_host.h"
#include "scripting/script_engine.h"
#include "tier0/vprof.h"
#include "tier0/icommandline.h"
#include "tier1/utlstringtoken.h"
#include <convar.h>

DLL_IMPORT ICommandLine* CommandLine();

#define VERSION_STRING  "v" SEMVER " @ " GITHUB_SHA
#define BUILD_TIMESTAMP __DATE__ " " __TIME__

// Vtable of CGameEventManager, resolved at Load() and kept so the global KHook on
// LoadEventsFromFile can be removed again on Unload().
void* g_pCGameEventManagerVTable = nullptr;

counterstrikesharp::GlobalClass* counterstrikesharp::GlobalClass::head = nullptr;

CGameEntitySystem* GameEntitySystem() { return counterstrikesharp::globals::entitySystem; }

// TODO: Workaround for windows, we __MUST__ have COUNTERSTRIKESHARP_API to handle it.
// like on windows it should be `extern "C" __declspec(dllexport)`, on linux it should be anything else.
DLL_EXPORT void InvokeNative(counterstrikesharp::fxNativeContext& context)
{
    if (context.nativeIdentifier == 0) return;

    if (context.nativeIdentifier != counterstrikesharp::hash_string_const("QUEUE_TASK_FOR_FRAME") &&
        context.nativeIdentifier != counterstrikesharp::hash_string_const("GET_SCHEMA_OFFSET") &&
        counterstrikesharp::globals::gameThreadId != std::this_thread::get_id())
    {
        counterstrikesharp::ScriptContextRaw scriptContext(context);
        scriptContext.ThrowNativeError("Invoked on a non-main thread");

        // Log once per offending native id. The throwing plugin gets the native
        // error on every call (that's the real signal); the core log only needs the
        // first occurrence -- a misbehaving plugin would otherwise flood the log.
        static std::mutex s_warnMutex;
        static std::unordered_set<uint64_t> s_warnedNatives;
        {
            std::lock_guard<std::mutex> lock(s_warnMutex);
            if (s_warnedNatives.insert(context.nativeIdentifier).second)
            {
                CSSHARP_CORE_CRITICAL("Native {:x} was invoked on a non-main thread (further occurrences suppressed)",
                                      context.nativeIdentifier);
            }
        }
        return;
    }

    counterstrikesharp::ScriptEngine::InvokeNative(context);
}

class GameSessionConfiguration_t
{
};

PLUGIN_EXPOSE(CounterStrikeSharpMMPlugin, counterstrikesharp::gPlugin);

namespace counterstrikesharp {

CounterStrikeSharpMMPlugin gPlugin;

CounterStrikeSharpMMPlugin::CounterStrikeSharpMMPlugin()
    : m_GameFrame(&IServerGameDLL::GameFrame, this, nullptr, &CounterStrikeSharpMMPlugin::Hook_GameFrame),
      m_StartupServer(&INetworkServerService::StartupServer, this, nullptr, &CounterStrikeSharpMMPlugin::Hook_StartupServer),
      m_RegisterLoopMode(&IEngineServiceMgr::RegisterLoopMode, this, &CounterStrikeSharpMMPlugin::Hook_RegisterLoopMode, nullptr),
      m_FindService(&IEngineServiceMgr::FindService, this, nullptr, &CounterStrikeSharpMMPlugin::Hook_FindService),
      m_LoadEventsFromFile(&IGameEventManager2::LoadEventsFromFile, this, &CounterStrikeSharpMMPlugin::Hook_LoadEventsFromFile, nullptr)
{
}

#if 0
// Currently unavailable, requires hl2sdk work!
ConVar sample_cvar("sample_cvar", "42", 0);
#endif

bool CounterStrikeSharpMMPlugin::Load(PluginId id, ISmmAPI* ismm, char* error, size_t maxlen, bool late)
{
    PLUGIN_SAVEVARS();
    if (!KHook::__exported__khook)
    {
        snprintf(error, maxlen, "CounterStrikeSharp requires Metamod with KHook (plugin API 18)");
        return false;
    }
    // Any early return / throw below must not leave half-installed KHook patches
    // pointing into a plugin Metamod is about to unload.
    struct LoadGuard
    {
        bool succeeded = false;
        ~LoadGuard()
        {
            if (!succeeded)
            {
                HookSet::ClearAll();
                DynamicHook::ShutdownAll();
            }
        }
    } guard;
    try
    {
        ismm->AddListener(this, this);

        globals::ismm = ismm;
        globals::gameThreadId = std::this_thread::get_id();

        Log::Init();

        CSSHARP_CORE_DEBUG("Initializing with command line: {}", CommandLine()->GetCmdLine());
        const char* basePath = CommandLine()->ParmValue(CUtlStringToken("+css_basepath"), "/addons/counterstrikesharp");

        GET_V_IFACE_CURRENT(GetEngineFactory, globals::engineServer2, IVEngineServer2, SOURCE2ENGINETOSERVER_INTERFACE_VERSION);
        GET_V_IFACE_CURRENT(GetEngineFactory, globals::engine, IVEngineServer, INTERFACEVERSION_VENGINESERVER);
        GET_V_IFACE_CURRENT(GetEngineFactory, globals::cvars, ICvar, CVAR_INTERFACE_VERSION);
        GET_V_IFACE_CURRENT(GetEngineFactory, g_pGameResourceServiceServer, IGameResourceService,
                            GAMERESOURCESERVICESERVER_INTERFACE_VERSION);
        GET_V_IFACE_ANY(GetServerFactory, globals::server, IServerGameDLL, INTERFACEVERSION_SERVERGAMEDLL);
        GET_V_IFACE_ANY(GetServerFactory, globals::serverGameClients, IServerGameClients, INTERFACEVERSION_SERVERGAMECLIENTS);
        GET_V_IFACE_ANY(GetEngineFactory, globals::networkServerService, INetworkServerService, NETWORKSERVERSERVICE_INTERFACE_VERSION);
        GET_V_IFACE_ANY(GetEngineFactory, globals::schemaSystem, CSchemaSystem, SCHEMASYSTEM_INTERFACE_VERSION);
        GET_V_IFACE_ANY(GetEngineFactory, globals::gameEventSystem, IGameEventSystem, GAMEEVENTSYSTEM_INTERFACE_VERSION);
        GET_V_IFACE_ANY(GetEngineFactory, globals::engineServiceManager, IEngineServiceMgr, ENGINESERVICEMGR_INTERFACE_VERSION);
        GET_V_IFACE_ANY(GetEngineFactory, globals::networkMessages, INetworkMessages, NETWORKMESSAGES_INTERFACE_VERSION);
        GET_V_IFACE_ANY(GetServerFactory, globals::gameEntities, ISource2GameEntities, SOURCE2GAMEENTITIES_INTERFACE_VERSION);
        g_pCVar = globals::cvars;
        g_pSource2GameEntities = globals::gameEntities;
        interfaces::pGameResourceServiceServer = (CGameResourceService*)g_pGameResourceServiceServer;
        CSSHARP_CORE_DEBUG("pGameResourceServiceServer resolved: {}", (void*)interfaces::pGameResourceServiceServer);

        if (utils::RelativeDirectory(std::string(basePath)) == "NotFound")
        {
            CSSHARP_CORE_ERROR("Invalid base path: {}", basePath);
            return false;
        }
        CSSHARP_CORE_DEBUG("Current root directory: {}", utils::GetRootDirectory());

        // Now that the addons root is known, attach the file sink under a path the
        // server user owns (<root>/logs) instead of the engine's working directory.
        Log::AttachFileSink(utils::GetRootDirectory() + "/logs");

        auto coreconfig_path = std::string(utils::ConfigsDirectory() + "/core");
        globals::coreConfig = new CCoreConfig(coreconfig_path);
        char coreconfig_error[255] = "";

        if (!globals::coreConfig->Init(coreconfig_error, sizeof(coreconfig_error)))
        {
            CSSHARP_CORE_ERROR("Could not read \'{}\'. Error: {}", coreconfig_path, coreconfig_error);
            return false;
        }

        // Apply configured verbosity now that core.json is parsed. The earliest lines
        // (cmdline, root dir) already printed at the default info level; everything from
        // here on honors LogVerbosity. SPDLOG_LEVEL env still overrides if set.
        Log::SetLevelFromString(globals::coreConfig->LogVerbosity);

        CSSHARP_CORE_DEBUG("CoreConfig loaded.");

        if (globals::coreConfig->AutoUpdateEnabled)
        {
#ifdef _WIN32
            if (!update::TryUpdateGameConfig())
            {
                CSSHARP_CORE_ERROR("Failed to update game config.");
            }
#else
            CSSHARP_CORE_WARN("Auto-update is not currently supported on this platform.");
#endif
        }

        auto gamedata_path = std::string(utils::GamedataDirectory() + "/gamedata.json");
        globals::gameConfig = new CGameConfig(gamedata_path);
        char conf_error[255] = "";

        if (!globals::gameConfig->Init(conf_error, sizeof(conf_error)))
        {
            CSSHARP_CORE_ERROR("Could not read \'{}\'. Error: {}", gamedata_path, conf_error);
            return false;
        }

        globals::Initialize();

        CSSHARP_CORE_DEBUG("Globals loaded.");
        globals::mmPlugin = &gPlugin;

        CALL_GLOBAL_LISTENER(OnAllInitialized());

        on_activate_callback = globals::callbackManager.CreateCallback("OnMapStart");
        on_map_end_callback = globals::callbackManager.CreateCallback("OnMapEnd");
        on_metamod_all_plugins_loaded_callback = globals::callbackManager.CreateCallback("OnMetamodAllPluginsLoaded");

        m_GameFrame.Add(globals::server, "IServerGameDLL::GameFrame");
        m_StartupServer.Add(globals::networkServerService, "INetworkServerService::StartupServer");
        m_RegisterLoopMode.Add(globals::engineServiceManager, "IEngineServiceMgr::RegisterLoopMode");
        m_FindService.Add(globals::engineServiceManager, "IEngineServiceMgr::FindService");

        // CGameEventManager is instantiated by the engine after we load, so hook every
        // instance sharing the class vtable rather than a specific object (the KHook
        // equivalent of SourceHook's DVP hook). AddGlobal() dereferences its argument to
        // get the vtable, hence the address-of on the vtable pointer.
        g_pCGameEventManagerVTable = modules::server->FindVirtualTable("CGameEventManager");
        if (g_pCGameEventManagerVTable != nullptr)
        {
            m_LoadEventsFromFile.AddGlobal((IGameEventManager2*)&g_pCGameEventManagerVTable, "IGameEventManager2::LoadEventsFromFile");
        }
        else
        {
            CSSHARP_CORE_ERROR("Failed to find the CGameEventManager vtable, game events will not be available.");
        }

        if (!InitGameSystems())
        {
            CSSHARP_CORE_ERROR("Failed to initialize GameSystem!");
            return false;
        }

        CSSHARP_CORE_DEBUG("Initialized GameSystem.");

        if (!globals::dotnetManager.Initialize())
        {
            CSSHARP_CORE_ERROR("Failed to initialize .NET runtime");
        }

        // Install AFTER the .NET runtime so our SIGABRT handler runs first (prints the
        // culprit) then chains to the CLR's handler (keeps its crash dump). Lets a
        // garbage-collected-delegate FailFast name the suspect plugin as the LAST
        // console line instead of an anonymous "Process terminated".
        fatal::InstallHandler();

        CSSHARP_CORE_DEBUG("Hooks added.");

        // Used by Metamod Console Commands
        g_pCVar = globals::cvars;
        ConVar_Register(FCVAR_RELEASE | FCVAR_CLIENT_CAN_EXECUTE | FCVAR_GAMEDLL);

        guard.succeeded = true;
        return true;
    }
    catch (const std::exception& exception)
    {
        snprintf(error, maxlen, "Failed to initialize CounterStrikeSharp hooks: %s", exception.what());
        return false;
    }
}

static bool s_bLevelShutdownOccurred = false;

KHook::Return<void> CounterStrikeSharpMMPlugin::Hook_StartupServer(INetworkServerService*,
                                                                   const GameSessionConfiguration_t& config,
                                                                   ISource2WorldSession*,
                                                                   const char*)
{
    CSSHARP_CORE_DEBUG("Hook_StartupServer fired (pGameResourceServiceServer={})", (void*)interfaces::pGameResourceServiceServer);
    globals::entitySystem = interfaces::pGameResourceServiceServer->GetGameEntitySystem();
    // Remove before adding to prevent double-registration when workshop addon changes
    // trigger a second StartupServer within the same map session (ss_dead cycle).
    globals::entitySystem->RemoveListenerEntity(&globals::entityManager.entityListener);
    globals::entitySystem->AddListenerEntity(&globals::entityManager.entityListener);

    // Workshop ss_dead reload cycles fire Hook_StartupServer without a
    // preceding OnLevelShutdown. We pass that distinction down so that:
    //   levelShutdown=true  -> fires OnLevelEnd (PlayerManager etc.) and
    //                          resets timer tick state. Genuine changelevel.
    //   levelShutdown=false -> ONLY resets timer tick state. No OnLevelEnd,
    //                          which is what avoids the PlayerManager
    //                          disconnect -> stale .NET callbacks -> SEGV
    //                          chain on ss_dead reloads.
    // Tick-state reset must be unconditional so universal_time math in
    // OnGameFrame doesn't desync across the cycle (otherwise pending one-off
    // timers stall arbitrarily long).
    globals::timerSystem.OnStartupServer(s_bLevelShutdownOccurred);
    s_bLevelShutdownOccurred = false;

    on_activate_callback->ScriptContext().Reset();
    on_activate_callback->ScriptContext().Push(globals::getGlobalVars()->mapname.ToCStr());
    on_activate_callback->Execute();

    return { KHook::Action::Ignore };
}
bool CounterStrikeSharpMMPlugin::Unload(char* error, size_t maxlen)
{
    // Tear the KHook function detours down FIRST (every manager's HookSet: Host_Say,
    // FireOutputInternal; the CGameEventManager::Init detour; managed DynamicHooks),
    // so no engine call path can re-enter this .so while the managers below release
    // their script callbacks. Leaving any of them installed means the next engine
    // call after unload jumps into freed code and crashes the server on Metamod reload.
    HookSet::ClearAll();
    globals::ShutdownHooks();
    DynamicHook::ShutdownAll();

    // Fire OnShutdown on every registered manager — the mirror of the
    // CALL_GLOBAL_LISTENER(OnAllInitialized()) done in Load(). Without this the
    // managers' teardown (KHook Remove() calls + callback releases in
    // each manager's OnShutdown) never ran, leaking hooks and script callbacks on
    // every Metamod unload/reload. Run before removing our own virtual hooks below
    // so teardown happens in reverse order of init.
    CALL_GLOBAL_LISTENER(OnShutdown());

    m_GameFrame.Remove(globals::server);
    m_StartupServer.Remove(globals::networkServerService);
    m_RegisterLoopMode.Remove(globals::engineServiceManager);
    m_FindService.Remove(globals::engineServiceManager);
    if (g_pCGameEventManagerVTable != nullptr)
    {
        m_LoadEventsFromFile.RemoveGlobal((IGameEventManager2*)&g_pCGameEventManagerVTable);
        // The vtable was resolved out of server.so on Load. Clear it so a later
        // Load re-resolves rather than reusing a stale address, and so a second
        // Unload cannot RemoveGlobal a vtable that is no longer hooked.
        g_pCGameEventManagerVTable = nullptr;
    }

    globals::callbackManager.ReleaseCallback(on_activate_callback);
    globals::callbackManager.ReleaseCallback(on_map_end_callback);
    globals::callbackManager.ReleaseCallback(on_metamod_all_plugins_loaded_callback);

    return true;
}

void CounterStrikeSharpMMPlugin::AllPluginsLoaded()
{
    /* This is where we'd do stuff that relies on the mod or other plugins
     * being initialized (for example, cvars added and events registered).
     */
    on_metamod_all_plugins_loaded_callback->ScriptContext().Reset();
    on_metamod_all_plugins_loaded_callback->Execute();

    if (globals::entityManager.Func_OnTakeDamage)
    {
        globals::entityManager.Func_OnTakeDamage->AddHook(&OnTakeDamageProxy);
    }
}

KHook::Return<void> CounterStrikeSharpMMPlugin::Hook_GameFrame(IServerGameDLL*, bool simulating, bool bFirstTick, bool bLastTick)
{
    /**
     * simulating:
     * ***********
     * true  | game is ticking
     * false | game is not ticking
     */
    // VPROF_BUDGET("CS#::Hook_GameFrame", "CS# On Frame");

    // Fallback init for environments where Hook_StartupServer silently never
    // fires -- e.g. CS2 under FEX-Emu on aarch64, where the detour x86_64
    // trampoline on INetworkServerService::StartupServer can fail to install
    // or invoke. See GH roflmuffin/CounterStrikeSharp#1320. Without this
    // fallback, globals::entitySystem stays nullptr and every entity-touching
    // native throws "Entity system yet is not initialized".
    if (!globals::entitySystem && interfaces::pGameResourceServiceServer)
    {
        auto* pEntitySystem = interfaces::pGameResourceServiceServer->GetGameEntitySystem();
        if (pEntitySystem)
        {
            globals::entitySystem = pEntitySystem;
            // Remove+Add for parity with Hook_StartupServer's idempotent registration.
            globals::entitySystem->RemoveListenerEntity(&globals::entityManager.entityListener);
            globals::entitySystem->AddListenerEntity(&globals::entityManager.entityListener);
            CSSHARP_CORE_WARN("entitySystem lazy-initialized from Hook_GameFrame "
                              "(Hook_StartupServer never fired -- FEX-Emu / hook failure?)");
        }
    }

    // Free DynamicHooks that were removed from inside a hook callback last frame.
    DynamicHook::CollectRetired();
    globals::timerSystem.OnGameFrame(simulating);

    // Reused across frames so the scheduler drain does not allocate a vector per
    // frame. Function-local static: Hook_GameFrame only ever runs on the game thread.
    static std::vector<std::function<void()>> s_frameCallbacks;

    const int tickcount = globals::getGlobalVars()->tickcount;
    globals::tickScheduler.getCallbacks(tickcount, s_frameCallbacks);
    if (!s_frameCallbacks.empty())
    {
        CSSHARP_CORE_TRACE("Executing frame specific tasks of size: {0} on tick number {1}", s_frameCallbacks.size(), tickcount);

        for (auto& callback : s_frameCallbacks)
        {
            callback();
        }

        // Release the callables (and anything they captured) now rather than holding
        // them rooted until the next frame overwrites the slot. Capacity is kept.
        s_frameCallbacks.clear();
    }

    return { KHook::Action::Ignore };
}

// Potentially might not work
void CounterStrikeSharpMMPlugin::OnLevelInit(
    char const* pMapName, char const* pMapEntities, char const* pOldLevel, char const* pLandmarkName, bool loadGame, bool background)
{
    CSSHARP_CORE_TRACE("name={0},mapname={1}", "LevelInit", pMapName);

    m_has_level_initialized = true;
}

KHook::Return<void> CounterStrikeSharpMMPlugin::Hook_RegisterLoopMode(IEngineServiceMgr*,
                                                                      const char* pszLoopModeName,
                                                                      ILoopModeFactory* pLoopModeFactory,
                                                                      void** ppGlobalPointer)
{
    if (strcmp(pszLoopModeName, "game") == 0)
    {
        bool expected = false;
        if (globals::gameLoopInitialized.compare_exchange_strong(expected, true))
        {
            CALL_GLOBAL_LISTENER(OnGameLoopInitialized());
        }
    }

    return { KHook::Action::Ignore };
}

KHook::Return<IEngineService*> CounterStrikeSharpMMPlugin::Hook_FindService(IEngineServiceMgr*, const char* serviceName)
{
    IEngineService* pService = hooks::OriginalReturnOr<IEngineService*>(nullptr);

    return { KHook::Action::Ignore, pService };
}

KHook::Return<int>
CounterStrikeSharpMMPlugin::Hook_LoadEventsFromFile(IGameEventManager2* pGameEventManager, const char* filename, bool bSearchAll)
{
    ExecuteOnce(globals::gameEventManager = pGameEventManager);

    return { KHook::Action::Ignore, 0 };
}

void CounterStrikeSharpMMPlugin::OnLevelShutdown()
{
    CSSHARP_CORE_TRACE("name={0}", "LevelShutdown");

    if (!m_has_level_initialized)
    {
        return;
    }

    m_has_level_initialized = false;

    if (on_map_end_callback && on_map_end_callback->GetFunctionCount())
    {
        on_map_end_callback->ScriptContext().Reset();
        on_map_end_callback->Execute();
    }
}

bool CounterStrikeSharpMMPlugin::Pause(char* error, size_t maxlen) { return true; }

bool CounterStrikeSharpMMPlugin::Unpause(char* error, size_t maxlen) { return true; }

const char* CounterStrikeSharpMMPlugin::GetLicense() { return "GNU GPLv3"; }

const char* CounterStrikeSharpMMPlugin::GetVersion() { return VERSION_STRING; }

const char* CounterStrikeSharpMMPlugin::GetDate() { return BUILD_TIMESTAMP; }

const char* CounterStrikeSharpMMPlugin::GetLogTag() { return "CSSHARP"; }

const char* CounterStrikeSharpMMPlugin::GetAuthor() { return "Miksen"; }

const char* CounterStrikeSharpMMPlugin::GetDescription() { return "Counter Strike .NET Scripting Runtime forked"; }

const char* CounterStrikeSharpMMPlugin::GetName() { return "CounterStrikeSharp"; }

const char* CounterStrikeSharpMMPlugin::GetURL() { return "https://github.com/mrc4tt/CounterStrikeSharp"; }
} // namespace counterstrikesharp
