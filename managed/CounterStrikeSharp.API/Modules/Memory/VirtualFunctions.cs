using System;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;

namespace CounterStrikeSharp.API.Modules.Memory;

// Each engine function is resolved LAZILY, on first access, via a thread-safe Lazy<T>.
//
// Previously these were eager static field initializers (`= new(GameData.GetSignature("Foo"))`).
// Because they all live in one type, a SINGLE missing gamedata key threw inside the static
// constructor and surfaced as a TypeInitializationException for the WHOLE class — taking down
// every plugin that touched ANY VirtualFunctions.* member, not just the consumer of the missing
// key. This tripped on every schema sync that didn't carry forward all keys.
//
// With Lazy<T>, resolution is deferred to first use and isolated per-member: a missing
// "Foo" key now throws only when something actually reads VirtualFunctions.Foo, with the clear
// ArgumentException from GameData.GetSignature, and leaves every other binding usable. Lazy
// caches the constructed MemoryFunction, so the same instance is returned across calls — hook
// then later unhook still operate on one object.
public static class VirtualFunctions
{
    private static readonly Lazy<MemoryFunctionVoid<IntPtr, HudDestination, string, IntPtr, IntPtr, IntPtr, IntPtr>> _clientPrintFunc =
        new(() => new(GameData.GetSignature("ClientPrint")));
    public static MemoryFunctionVoid<IntPtr, HudDestination, string, IntPtr, IntPtr, IntPtr, IntPtr> ClientPrintFunc => _clientPrintFunc.Value;
    public static Action<IntPtr, HudDestination, string, IntPtr, IntPtr, IntPtr, IntPtr> ClientPrint => ClientPrintFunc.Invoke;

    private static readonly Lazy<MemoryFunctionVoid<HudDestination, string, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>> _clientPrintAllFunc =
        new(() => new(GameData.GetSignature("UTIL_ClientPrintAll")));
    public static MemoryFunctionVoid<HudDestination, string, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> ClientPrintAllFunc => _clientPrintAllFunc.Value;
    public static Action<HudDestination, string, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> ClientPrintAll => ClientPrintAllFunc.Invoke;

    // void (*FnGiveNamedItem)(void* itemService,const char* pchName, void* iSubType,void* pScriptItem, void* a5,void* a6) = nullptr;
    private static readonly Lazy<MemoryFunctionWithReturn<IntPtr, string, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>> _giveNamedItemFunc =
        new(() => new(GameData.GetSignature("GiveNamedItem")));
    public static MemoryFunctionWithReturn<IntPtr, string, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> GiveNamedItemFunc => _giveNamedItemFunc.Value;
    public static Func<IntPtr, string, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> GiveNamedItem => GiveNamedItemFunc.Invoke;

    private static readonly Lazy<MemoryFunctionVoid<IntPtr, byte>> _switchTeamFunc =
        new(() => new(GameData.GetSignature("CCSPlayerController_SwitchTeam")));
    public static MemoryFunctionVoid<IntPtr, byte> SwitchTeamFunc => _switchTeamFunc.Value;
    public static Action<IntPtr, byte> SwitchTeam => SwitchTeamFunc.Invoke;

    // void(*UTIL_Remove)(CEntityInstance*);
    private static readonly Lazy<MemoryFunctionVoid<IntPtr>> _utilRemoveFunc =
        new(() => new(GameData.GetSignature("UTIL_Remove")));
    public static MemoryFunctionVoid<IntPtr> UTIL_RemoveFunc => _utilRemoveFunc.Value;
    public static Action<IntPtr> UTIL_Remove => UTIL_RemoveFunc.Invoke;

    // void(*CBaseModelEntity_SetModel)(CBaseModelEntity*, const char*);
    private static readonly Lazy<MemoryFunctionVoid<IntPtr, string>> _setModelFunc =
        new(() => new(GameData.GetSignature("CBaseModelEntity_SetModel")));
    public static MemoryFunctionVoid<IntPtr, string> SetModelFunc => _setModelFunc.Value;
    public static Action<IntPtr, string> SetModel => SetModelFunc.Invoke;

    private static readonly Lazy<MemoryFunctionVoid<IntPtr, RoundEndReason, float, IntPtr, byte>> _terminateRoundFunc =
        new(() => new(GameData.GetSignature("CCSGameRules_TerminateRound")));
    [Obsolete("Use TerminateRoundFuncLinux or TerminateRoundFuncWindows instead")]
    public static MemoryFunctionVoid<IntPtr, RoundEndReason, float, IntPtr, byte> TerminateRoundFunc => _terminateRoundFunc.Value;

    [Obsolete("Use TerminateRoundLinux or TerminateRoundWindows instead")]
    public static Action<IntPtr, RoundEndReason, float, IntPtr, byte> TerminateRound => _terminateRoundFunc.Value.Invoke;

    private static readonly Lazy<MemoryFunctionVoid<IntPtr, RoundEndReason, float, IntPtr, byte>> _terminateRoundFuncLinux =
        new(() => new(GameData.GetSignature("CCSGameRules_TerminateRound")));
    public static MemoryFunctionVoid<IntPtr, RoundEndReason, float, IntPtr, byte> TerminateRoundFuncLinux => _terminateRoundFuncLinux.Value;
    public static Action<IntPtr, RoundEndReason, float, IntPtr, byte> TerminateRoundLinux => TerminateRoundFuncLinux.Invoke;

    private static readonly Lazy<MemoryFunctionVoid<IntPtr, float, RoundEndReason, IntPtr, byte>> _terminateRoundFuncWindows =
        new(() => new(GameData.GetSignature("CCSGameRules_TerminateRound")));
    public static MemoryFunctionVoid<IntPtr, float, RoundEndReason, IntPtr, byte> TerminateRoundFuncWindows => _terminateRoundFuncWindows.Value;
    public static Action<IntPtr, float, RoundEndReason, IntPtr, byte> TerminateRoundWindows => TerminateRoundFuncWindows.Invoke;

    private static readonly Lazy<MemoryFunctionWithReturn<string, int, IntPtr>> _utilCreateEntityByNameFunc =
        new(() => new(GameData.GetSignature("UTIL_CreateEntityByName")));
    public static MemoryFunctionWithReturn<string, int, IntPtr> UTIL_CreateEntityByNameFunc => _utilCreateEntityByNameFunc.Value;
    public static Func<string, int, IntPtr> UTIL_CreateEntityByName => UTIL_CreateEntityByNameFunc.Invoke;

    private static readonly Lazy<MemoryFunctionVoid<IntPtr, IntPtr>> _cBaseEntityDispatchSpawnFunc =
        new(() => new(GameData.GetSignature("CBaseEntity_DispatchSpawn")));
    public static MemoryFunctionVoid<IntPtr, IntPtr> CBaseEntity_DispatchSpawnFunc => _cBaseEntityDispatchSpawnFunc.Value;
    public static Action<IntPtr, IntPtr> CBaseEntity_DispatchSpawn => CBaseEntity_DispatchSpawnFunc.Invoke;

    private static readonly Lazy<MemoryFunctionVoid<CBasePlayerController, CBasePlayerPawn, bool, bool>> _cBasePlayerControllerSetPawnFunc =
        new(() => new(GameData.GetSignature("CBasePlayerController_SetPawn")));
    public static MemoryFunctionVoid<CBasePlayerController, CBasePlayerPawn, bool, bool> CBasePlayerController_SetPawnFunc => _cBasePlayerControllerSetPawnFunc.Value;

    private static readonly Lazy<MemoryFunctionVoid<CEntityInstance, CTakeDamageInfo, CTakeDamageResult>> _cBaseEntityTakeDamageOldFunc =
        new(() => new(GameData.GetSignature("CBaseEntity_TakeDamageOld")));
    [Obsolete("Use Listeners.OnEntityTakeDamagePre instead")]
    public static MemoryFunctionVoid<CEntityInstance, CTakeDamageInfo, CTakeDamageResult> CBaseEntity_TakeDamageOldFunc => _cBaseEntityTakeDamageOldFunc.Value;

    public static Action<CEntityInstance, CTakeDamageInfo, CTakeDamageResult> CBaseEntity_TakeDamageOld => _cBaseEntityTakeDamageOldFunc.Value.Invoke;

    // Compatibility alias used by older third-party plugins (e.g. WC3) that hook the entity TakeDamage
    // via DynamicHook with two parameters (entity, info). The underlying native function is the same one
    // backing CBaseEntity_TakeDamageOldFunc above; declaring it with two generic params here is intentional —
    // hook callbacks read parameters by index via DynoHook regardless of declared arity, so plugins reading
    // hook.GetParam<CEntityInstance>(0) and hook.GetParam<CTakeDamageInfo>(1) work without modification.
    // For invoking the function directly, prefer CBaseEntity_TakeDamageOldFunc (correct 3-arg ABI) or the
    // OnEntityTakeDamagePre/Post listeners.
    private static readonly Lazy<MemoryFunctionVoid<CEntityInstance, CTakeDamageInfo>> _cBaseEntityTakeDamageFunc =
        new(() => new(GameData.GetSignature("CBaseEntity_TakeDamage")));
    public static MemoryFunctionVoid<CEntityInstance, CTakeDamageInfo> CBaseEntity_TakeDamageFunc => _cBaseEntityTakeDamageFunc.Value;

    private static readonly Lazy<MemoryFunctionWithReturn<CCSPlayer_WeaponServices, CBasePlayerWeapon, bool>> _cCSPlayerWeaponServicesCanUseFunc =
        new(() => new(GameData.GetSignature("CCSPlayer_WeaponServices_CanUse")));
    public static MemoryFunctionWithReturn<CCSPlayer_WeaponServices, CBasePlayerWeapon, bool> CCSPlayer_WeaponServices_CanUseFunc => _cCSPlayerWeaponServicesCanUseFunc.Value;
    public static Func<CCSPlayer_WeaponServices, CBasePlayerWeapon, bool> CCSPlayer_WeaponServices_CanUse => CCSPlayer_WeaponServices_CanUseFunc.Invoke;

    private static readonly Lazy<MemoryFunctionWithReturn<int, string, CCSWeaponBaseVData>> _getCSWeaponDataFromKeyFunc =
        new(() => new(GameData.GetSignature("GetCSWeaponDataFromKey")));
    public static MemoryFunctionWithReturn<int, string, CCSWeaponBaseVData> GetCSWeaponDataFromKeyFunc => _getCSWeaponDataFromKeyFunc.Value;
    public static Func<int, string, CCSWeaponBaseVData> GetCSWeaponDataFromKey => GetCSWeaponDataFromKeyFunc.Invoke;

    private static readonly Lazy<MemoryFunctionWithReturn<CCSPlayer_ItemServices, CEconItemView, AcquireMethod, IntPtr, AcquireResult>> _cCSPlayerItemServicesCanAcquireFunc =
        new(() => new(GameData.GetSignature("CCSPlayer_ItemServices_CanAcquire")));
    public static MemoryFunctionWithReturn<CCSPlayer_ItemServices, CEconItemView, AcquireMethod, IntPtr, AcquireResult> CCSPlayer_ItemServices_CanAcquireFunc => _cCSPlayerItemServicesCanAcquireFunc.Value;
    public static Func<CCSPlayer_ItemServices, CEconItemView, AcquireMethod, IntPtr, AcquireResult> CCSPlayer_ItemServices_CanAcquire => CCSPlayer_ItemServices_CanAcquireFunc.Invoke;

    private static readonly Lazy<MemoryFunctionVoid<CCSPlayerPawnBase>> _cCSPlayerPawnBasePostThinkFunc =
        new(() => new(GameData.GetSignature("CCSPlayerPawnBase_PostThink")));
    public static MemoryFunctionVoid<CCSPlayerPawnBase> CCSPlayerPawnBase_PostThinkFunc => _cCSPlayerPawnBasePostThinkFunc.Value;
    public static Action<CCSPlayerPawnBase> CCSPlayerPawnBase_PostThink => CCSPlayerPawnBase_PostThinkFunc.Invoke;

    private static readonly Lazy<MemoryFunctionVoid<CBaseTrigger, CBaseEntity>> _cBaseTriggerStartTouchFunc =
        new(() => new(GameData.GetSignature("CBaseTrigger_StartTouch")));
    public static MemoryFunctionVoid<CBaseTrigger, CBaseEntity> CBaseTrigger_StartTouchFunc => _cBaseTriggerStartTouchFunc.Value;
    public static Action<CBaseTrigger, CBaseEntity> CBaseTrigger_StartTouch => CBaseTrigger_StartTouchFunc.Invoke;

    private static readonly Lazy<MemoryFunctionVoid<CBaseTrigger, CBaseEntity>> _cBaseTriggerEndTouchFunc =
        new(() => new(GameData.GetSignature("CBaseTrigger_EndTouch")));
    public static MemoryFunctionVoid<CBaseTrigger, CBaseEntity> CBaseTrigger_EndTouchFunc => _cBaseTriggerEndTouchFunc.Value;
    public static Action<CBaseTrigger, CBaseEntity> CBaseTrigger_EndTouch => CBaseTrigger_EndTouchFunc.Invoke;

    private static readonly Lazy<MemoryFunctionVoid<IntPtr, IntPtr>> _removePlayerItemFunc =
        new(() => new(GameData.GetSignature("CBasePlayerPawn_RemovePlayerItem")));
    public static MemoryFunctionVoid<IntPtr, IntPtr> RemovePlayerItemFunc => _removePlayerItemFunc.Value;
    public static Action<IntPtr, IntPtr> RemovePlayerItemVirtual => RemovePlayerItemFunc.Invoke;
}
