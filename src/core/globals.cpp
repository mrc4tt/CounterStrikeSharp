#include "mm_plugin.h"
#include "core/globals.h"

#include <memory>
#include "core/managers/player_manager.h"
#include "core/tick_scheduler.h"
#include "iserver.h"
#include "managers/event_manager.h"
#include "scripting/callback_manager.h"
#include "scripting/dotnet_host.h"
#include "timer_system.h"

#include <ISmmPlugin.h>

#include "log.h"
#include "utils/virtual.h"
#include "core/memory.h"
#include "core/managers/con_command_manager.h"
#include "core/managers/chat_manager.h"
#include "memory_module.h"
#include "interfaces/cs2_interfaces.h"
#include "core/managers/entity_manager.h"
#include "core/managers/server_manager.h"
#include "core/managers/voice_manager.h"
#include "core/managers/usermessage_manager.h"
#include "core/customhudlayout.h"
#include <public/game/server/iplayerinfo.h>
#include <public/entity2/entitysystem.h>

namespace counterstrikesharp {

namespace modules {
std::vector<std::unique_ptr<CModule>> moduleList{};
CModule* engine = nullptr;
CModule* tier0 = nullptr;
CModule* server = nullptr;
CModule* schemasystem = nullptr;
CModule* vscript = nullptr;
} // namespace modules

namespace globals {
// KHook detour on CGameEventManager::Init, installed in Initialize().
//
// Was a raw funchook jmp patch. KHook installs a single SafetyHook inline hook per
// address and fans out to every consumer through its own dispatcher, so other
// Metamod plugins hooking this function are ordered against us instead of racing
// our patch; it also keeps our detour invisible to their KHook::LookupSignature
// scans, which compensate only for KHook-owned patches.
//
// Held by pointer so RemoveDetours() can destroy it (and with it, remove the hook)
// before the plugin .so is unloaded -- KHook's dispatcher lives in Metamod and
// would otherwise call into our freed callbacks.
static std::unique_ptr<KHook::Function<void, IGameEventManager2*>> s_gameEventInitHook;
IVEngineServer2* engineServer2 = nullptr;
IVEngineServer* engine = nullptr;
IGameEventManager2* gameEventManager = nullptr;
IGameEventSystem* gameEventSystem = nullptr;
IPlayerInfoManager* playerinfoManager = nullptr;
IBotManager* botManager = nullptr;
IServerPluginHelpers* helpers = nullptr;
IUniformRandomStream* randomStream = nullptr;
IEngineTrace* engineTrace = nullptr;
IEngineSound* engineSound = nullptr;
IEngineServiceMgr* engineServiceManager = nullptr;
INetworkMessages* networkMessages = nullptr;
INetworkStringTableContainer* netStringTables = nullptr;
CGlobalVars* globalVars = nullptr;
IFileSystem* fileSystem = nullptr;
IServerGameDLL* serverGameDll = nullptr;
IServerGameClients* serverGameClients = nullptr;
INetworkServerService* networkServerService = nullptr;
CSchemaSystem* schemaSystem = nullptr;
IServerTools* serverTools = nullptr;
IPhysics* physics = nullptr;
IPhysicsCollision* physicsCollision = nullptr;
IPhysicsSurfaceProps* physicsSurfaceProps = nullptr;
IMDLCache* modelCache = nullptr;
IVoiceServer* voiceServer = nullptr;
CDotNetManager dotnetManager;
ICvar* cvars = nullptr;
ISource2Server* server = nullptr;
CGlobalEntityList* globalEntityList = nullptr;
CounterStrikeSharpMMPlugin* mmPlugin = nullptr;
ISmmAPI* ismm = nullptr;
CGameEntitySystem* entitySystem = nullptr;
CCoreConfig* coreConfig = nullptr;
CGameConfig* gameConfig = nullptr;
ISource2GameEntities* gameEntities = nullptr;

// Custom Managers
CallbackManager callbackManager;
EventManager eventManager;
PlayerManager playerManager;
TimerSystem timerSystem;
ConCommandManager conCommandManager;
EntityManager entityManager;
ChatManager chatManager;
ServerManager serverManager;
VoiceManager voiceManager;
TickScheduler tickScheduler;
UserMessageManager userMessageManager;
CCSCustomHudLayout customHudLayout;

std::atomic<bool> gameLoopInitialized{ false };
GetLegacyGameEventListener_t* GetLegacyGameEventListener = nullptr;
GameEventManagerInit_t* GameEventManagerInit = nullptr;
std::thread::id gameThreadId;

// Based on 64 fixed tick rate
const float engine_fixed_tick_interval = 0.015625f;

void Initialize()
{
    modules::Initialize();

    modules::engine = modules::GetModuleByName(MODULE_PREFIX "engine2" MODULE_EXT);
    modules::tier0 = modules::GetModuleByName(MODULE_PREFIX "tier0" MODULE_EXT);
    modules::server = modules::GetModuleByName(MODULE_PREFIX "server" MODULE_EXT);
    modules::schemasystem = modules::GetModuleByName(MODULE_PREFIX "schemasystem" MODULE_EXT);
    modules::vscript = modules::GetModuleByName(MODULE_PREFIX "vscript" MODULE_EXT);

    if (!interfaces::pGameResourceServiceServer)
    {
        CSSHARP_CORE_ERROR("Failed to get CGameResourceServiceServer");
        return;
    }

    GetLegacyGameEventListener = reinterpret_cast<GetLegacyGameEventListener_t*>(
        modules::server->FindSignature(globals::gameConfig->GetSignature("LegacyGameEventListener")));

    if (GetLegacyGameEventListener == nullptr)
    {
        CSSHARP_CORE_ERROR("Failed to find signature for \'GetLegacyGameEventListener\'");
        return;
    }

    GameEventManagerInit = reinterpret_cast<GameEventManagerInit_t*>(
        modules::server->FindSignature(globals::gameConfig->GetSignature("CGameEventManager_Init")));

    if (GameEventManagerInit == nullptr)
    {
        CSSHARP_CORE_ERROR("Failed to find signature for \'GameEventManagerInit\'");
        return;
    }

    NetworkStateChanged =
        reinterpret_cast<NetworkStateChanged_t*>(modules::server->FindSignature(globals::gameConfig->GetSignature("NetworkStateChanged")));

    if (NetworkStateChanged == nullptr)
    {
        CSSHARP_CORE_ERROR("Failed to find signature for \'NetworkStateChanged\'");
        return;
    }

    s_gameEventInitHook =
        std::make_unique<KHook::Function<void, IGameEventManager2*>>(&OnGameEventManagerInit, &OnGameEventManagerInitPost);
    s_gameEventInitHook->Configure((void*)GameEventManagerInit);
}

void RemoveDetours() { s_gameEventInitHook.reset(); }

// The funchook version ran as one function: stash the manager, call the original,
// then fire OnAllInitialized_Post. KHook calls the original for us, so that splits
// into a pre (stash) and a post (notify) callback to keep the same ordering.
KHook::Return<void> OnGameEventManagerInit(IGameEventManager2* pGameEventManager)
{
    gameEventManager = pGameEventManager;

    return { KHook::Action::Ignore };
}

KHook::Return<void> OnGameEventManagerInitPost(IGameEventManager2* pGameEventManager)
{
    eventManager.OnAllInitialized_Post();

    return { KHook::Action::Ignore };
}

CGlobalVars* getGlobalVars()
{
    INetworkGameServer* server = networkServerService->GetIGameServer();
    if (!server) return nullptr;
    return networkServerService->GetIGameServer()->GetGlobals();
}
} // namespace globals
} // namespace counterstrikesharp
