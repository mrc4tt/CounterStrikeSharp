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

#include <chrono>
#include <filesystem>
#ifndef _WIN32
#include <unistd.h>
#endif
#include <cstdio>
#include <cstdlib>
#include <mutex>
#include <unordered_set>

#include "core/detours.h"
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

// Keeps the newest `keep` dumps and deletes the rest. A crash loop writes one
// multi-GB dump per restart; without this, the disk fills and the next thing the
// host debugs is not the crash.
static void PruneOldCrashDumps(const std::string& directory, int keep)
{
    if (keep <= 0) return;

    std::error_code ec;
    std::vector<std::pair<std::filesystem::file_time_type, std::filesystem::path>> dumps;

    for (const auto& entry : std::filesystem::directory_iterator(directory, ec))
    {
        if (ec) return;
        if (!entry.is_regular_file(ec)) continue;
        if (entry.path().extension() != ".dmp") continue;

        auto when = entry.last_write_time(ec);
        if (ec) continue;
        dumps.emplace_back(when, entry.path());
    }

    if ((int)dumps.size() <= keep) return;

    std::sort(dumps.begin(), dumps.end(), [](const auto& a, const auto& b) {
        return a.first > b.first;
    });

    for (size_t i = (size_t)keep; i < dumps.size(); ++i)
    {
        std::filesystem::remove(dumps[i].second, ec);
        if (!ec) CSSHARP_CORE_DEBUG("Pruned old crash dump {}", dumps[i].second.string());
    }
}

// Everything the crash evidence needs, resolved once at startup: the directory,
// this server's identity, retention, and the runtime's dump settings.
static void SetupCrashReporting()
{
    const std::string crashDir = std::string(globals::ismm->GetBaseDir()) + "/dumps";

    std::error_code ec;
    std::filesystem::create_directories(crashDir, ec);
    if (ec)
    {
        CSSHARP_CORE_WARN("Could not create crash report directory '{}': {}", crashDir, ec.message());
        return;
    }

    // Identity, in order of preference: an explicit CSSHARP_SERVER_ID, otherwise
    // host:port, which is unique across a fleet and readable in a report without
    // anyone having had to configure it.
    std::string serverId;
    if (const char* envId = std::getenv("CSSHARP_SERVER_ID"); envId != nullptr && envId[0] != '\0')
    {
        serverId = envId;
    }
    else
    {
        char host[128] = { 0 };
#ifndef _WIN32
        if (gethostname(host, sizeof(host) - 1) != 0) host[0] = '\0';
#endif
        const char* port = CommandLine()->ParmValue("-port", (const char*)nullptr);
        if (port == nullptr) port = CommandLine()->ParmValue("+port", (const char*)nullptr);

        serverId = (host[0] != '\0' ? std::string(host) : std::string("unknown"));
        if (port != nullptr) serverId += std::string(":") + port;
    }

    fatal::SetBuildVersion(VERSION_STRING);
    fatal::ConfigureReporting(crashDir.c_str(), serverId.c_str());
    fatal::WriteStateFile();

    if (!globals::coreConfig->CrashDumpsEnabled)
    {
        CSSHARP_CORE_INFO("Crash reports active (id '{}', directory '{}'); .NET dumps disabled by config", serverId, crashDir);
        return;
    }

    PruneOldCrashDumps(crashDir, globals::coreConfig->CrashDumpRetention);

#ifndef _WIN32
    const std::string dumpName = crashDir + "/cssharp-%p-%t.dmp";
    const std::string dumpType = std::to_string(globals::coreConfig->CrashDumpType);

    setenv("DOTNET_DbgEnableMiniDump", "1", 0);
    setenv("DOTNET_DbgMiniDumpType", dumpType.c_str(), 0);
    setenv("DOTNET_DbgMiniDumpName", dumpName.c_str(), 0);
#else
    const std::string dumpName = crashDir + "/cssharp-%p-%t.dmp";
    _putenv_s("DOTNET_DbgEnableMiniDump", "1");
    _putenv_s("DOTNET_DbgMiniDumpType", std::to_string(globals::coreConfig->CrashDumpType).c_str());
    _putenv_s("DOTNET_DbgMiniDumpName", dumpName.c_str());
#endif

    CSSHARP_CORE_INFO("Crash reporting active (id '{}', directory '{}', dump type {})", serverId, crashDir,
                      globals::coreConfig->CrashDumpType);
}

bool CounterStrikeSharpMMPlugin::Load(PluginId id, ISmmAPI* ismm, char* error, size_t maxlen, bool late)
{
    PLUGIN_SAVEVARS();
    ismm->AddListener(this, this);

    globals::ismm = ismm;
    globals::gameThreadId = std::this_thread::get_id();

    Log::Init();

    CSSHARP_CORE_DEBUG("Initializing with command line: {}", CommandLine()->GetCmdLine());
    const char* basePath = CommandLine()->ParmValue(MakeStringToken("+css_basepath"), "/addons/counterstrikesharp");

    GET_V_IFACE_CURRENT(GetEngineFactory, globals::engineServer2, IVEngineServer2, SOURCE2ENGINETOSERVER_INTERFACE_VERSION);
    GET_V_IFACE_CURRENT(GetEngineFactory, globals::engine, IVEngineServer, INTERFACEVERSION_VENGINESERVER);
    GET_V_IFACE_CURRENT(GetEngineFactory, globals::cvars, ICvar, CVAR_INTERFACE_VERSION);
    GET_V_IFACE_CURRENT(GetEngineFactory, g_pGameResourceServiceServer, IGameResourceService, GAMERESOURCESERVICESERVER_INTERFACE_VERSION);
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

    // Crash reporting, configured BEFORE the .NET runtime boots.
    //
    // The runtime reads DOTNET_DbgEnableMiniDump and friends once, while it starts,
    // so setting them here is what makes managed crash dumps work with no launch
    // wrapper, no env file and no per-server setup -- the whole point when the fleet
    // is large. setenv() with overwrite=0 throughout: an operator who already set a
    // value on the process keeps it.
    SetupCrashReporting();

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

    return true;
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
    // Fire OnShutdown on every registered manager — the mirror of the
    // CALL_GLOBAL_LISTENER(OnAllInitialized()) done in Load(). Without this the
    // managers' teardown (KHook Remove() calls + callback releases in
    // each manager's OnShutdown) never ran, leaking hooks and script callbacks on
    // every Metamod unload/reload. Run before removing our own hooks/detours below
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

    // Uninstall funchook detours before our .so is unloaded. They redirect engine
    // functions (FireOutputInternal, Host_Say, CGameEventManager::Init) into trampolines
    // that live in THIS module; leaving them installed means the next call after unload
    // jumps into freed code and crashes the server on Metamod reload.
    globals::entityManager.RemoveDetours();
    globals::chatManager.RemoveDetours();
    globals::RemoveDetours();

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

namespace {
// Frame-time watchdog. Measures how long the per-tick work below takes and logs
// a WARN when a single frame blows past its budget, so "the server feels laggy"
// becomes "tick N took X ms". Off the hot path when frames are healthy: one
// steady_clock read at each end plus a compare.
//
// Threshold (ms) comes from env CSSHARP_FRAME_WARN_MS:
//   unset -> default to 2x the engine tick interval (silent on normal frames,
//            warns only on real spikes)
//   0     -> disabled
//   >0    -> explicit budget in milliseconds
// Warnings are throttled to at most once per 250ms so a spike storm can't itself
// flood logging and make things worse.
double g_frame_warn_ms = -1.0; // <0 = not yet initialized
double g_frame_warn_last_log_s = 0.0; // steady seconds of last emitted warning

double ResolveFrameWarnBudgetMs()
{
    const char* env = std::getenv("CSSHARP_FRAME_WARN_MS");
    if (env != nullptr && env[0] != '\0')
    {
        char* end = nullptr;
        double v = std::strtod(env, &end);
        if (end != env && v >= 0.0) return v; // 0 => disabled
    }

    double tick = counterstrikesharp::globals::engine_fixed_tick_interval; // seconds
    if (tick <= 0.0) tick = 1.0 / 64.0; // pre-init fallback
    // 3x tick (~46.9ms @ 64t): only flag frames well past budget so transient
    // single-frame bursts (round start, map event, plugin timer coalescing)
    // don't spam. Override with CSSHARP_FRAME_WARN_MS.
    return tick * 1000.0 * 3.0;
}
} // namespace

KHook::Return<void> CounterStrikeSharpMMPlugin::Hook_GameFrame(IServerGameDLL*, bool simulating, bool bFirstTick, bool bLastTick)
{
    const auto _wd_frame_start = std::chrono::steady_clock::now();
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

    // Flush the crash state file a few times a minute. WriteStateFile returns
    // immediately when nothing changed, so the steady-state cost is one relaxed
    // atomic load per 256 frames.
    static int s_stateFlushCounter = 0;
    if ((++s_stateFlushCounter & 0xFF) == 0)
    {
        if (auto* gv = globals::getGlobalVars()) fatal::SetTick(gv->tickcount);
        fatal::WriteStateFile();
    }

    if (g_frame_warn_ms < 0.0) g_frame_warn_ms = ResolveFrameWarnBudgetMs();
    // Warmup grace: the first seconds after load are dominated by one-off
    // cold-start cost -- JIT-compiling managed/plugin code paths on first call
    // and startup GC. These produce unavoidable, non-actionable frame spikes
    // (e.g. 100+ ms on tick ~70). Suppress the watchdog until the engine has
    // ticked past the warmup window so the warning only flags steady-state
    // regressions. R2R reduces but does not eliminate cold-start JIT.
    const int kFrameWarnWarmupTicks = 640; // ~10s at 64 tick
    if (g_frame_warn_ms > 0.0 && globals::getGlobalVars()->tickcount > kFrameWarnWarmupTicks)
    {
        const auto _wd_now = std::chrono::steady_clock::now();
        const double frame_ms = std::chrono::duration<double, std::milli>(_wd_now - _wd_frame_start).count();
        if (frame_ms > g_frame_warn_ms)
        {
            const double now_s = std::chrono::duration<double>(_wd_now.time_since_epoch()).count();
            if (now_s - g_frame_warn_last_log_s >= 0.25)
            {
                g_frame_warn_last_log_s = now_s;
            }
        }
    }

    return { KHook::Action::Ignore };
}

// Potentially might not work
void CounterStrikeSharpMMPlugin::OnLevelInit(
    char const* pMapName, char const* pMapEntities, char const* pOldLevel, char const* pLandmarkName, bool loadGame, bool background)
{
    CSSHARP_CORE_TRACE("name={0},mapname={1}", "LevelInit", pMapName);

    fatal::SetMap(pMapName);
    fatal::WriteStateFile();

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
