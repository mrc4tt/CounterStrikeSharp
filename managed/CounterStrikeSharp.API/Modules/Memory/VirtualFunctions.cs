using System;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;

namespace CounterStrikeSharp.API.Modules.Memory;

// Each engine function is an eager static FIELD whose gamedata SIGNATURE is resolved LAZILY,
// on first invoke/hook, via the deferred `new(() => GameData.GetSignature("Foo"))` ctor.
//
// Two constraints are satisfied at once:
//
//  1. FIELD ABI. Plugins compiled against the field-based API emit `ldsfld FooFunc`. A Lazy-backed
//     PROPERTY only exposes get_FooFunc + a private `_fooFunc` backing field, so that ldsfld fails
//     at load with MissingFieldException — silently breaking every such plugin (e.g. Deathmatch).
//     Keeping these as fields preserves the ABI.
//
//  2. PER-MEMBER ISOLATION. A plain eager `= new(GameData.GetSignature("Foo"))` field initializer
//     resolves the key inside the static constructor, so a SINGLE missing gamedata key throws
//     TypeInitializationException for the WHOLE class — taking down every plugin that touches ANY
//     VirtualFunctions.* member. The deferred `() => GameData.GetSignature(...)` factory defers the
//     lookup to first use (resolved in BaseMemoryFunction.EnsureNativeHandle), so a missing "Foo"
//     key throws only when something actually invokes/hooks VirtualFunctions.FooFunc, leaving every
//     other binding usable. The constructed handle is cached, so hook then later unhook operate on
//     one object.
//
// Do NOT convert these fields back to properties — it reintroduces the MissingFieldException break.
public static class VirtualFunctions
{
    // Kept as eager public static FIELDS (not Lazy-backed properties like the rest of this class)
    // for binary compatibility with plugins compiled against upstream CounterStrikeSharp.API
    // (e.g. NuGet 1.0.369), where ClientPrint / ClientPrintFunc / ClientPrintAll / ClientPrintAllFunc
    // are all public static FIELDS. Those plugins emit `ldsfld ClientPrintAll`; a property only
    // exposes get_ClientPrintAll + a private backing field, so the ldsfld fails at load with
    // MissingFieldException. Field signatures here match the NuGet metadata exactly (Action/
    // MemoryFunctionVoid arg shapes verified against the 1.0.369 assembly). Eager resolution means a
    // missing "ClientPrint"/"UTIL_ClientPrintAll" key throws TypeInitializationException for the whole
    // class — acceptable since these are core sigs that are always present.
    // Func fields must be declared AFTER their backing *Func field (static field init is textual order).
    public static readonly MemoryFunctionVoid<IntPtr, HudDestination, string, IntPtr, IntPtr, IntPtr, IntPtr> ClientPrintFunc =
        new(GameData.GetSignature("ClientPrint"));
    public static readonly Action<IntPtr, HudDestination, string, IntPtr, IntPtr, IntPtr, IntPtr> ClientPrint = ClientPrintFunc.Invoke;

    public static readonly MemoryFunctionVoid<HudDestination, string, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> ClientPrintAllFunc =
        new(GameData.GetSignature("UTIL_ClientPrintAll"));
    public static readonly Action<HudDestination, string, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> ClientPrintAll = ClientPrintAllFunc.Invoke;

    // void (*FnGiveNamedItem)(void* itemService,const char* pchName, void* iSubType,void* pScriptItem, void* a5,void* a6) = nullptr;
    // NOTE: kept as an eager public static FIELD (not a Lazy-backed property like the others)
    // for binary compatibility with plugins compiled against upstream CounterStrikeSharp.API
    // (e.g. NuGet 1.0.369), where this is a field. Those plugins emit `ldsfld GiveNamedItemFunc`;
    // a property only exposes get_GiveNamedItemFunc + a `_giveNamedItemFunc` backing field, so the
    // ldsfld fails at load with MissingFieldException. Eager resolution means a missing "GiveNamedItem"
    // gamedata key throws TypeInitializationException for the whole class — acceptable here since the
    // sig is required and present.
    public static readonly MemoryFunctionWithReturn<IntPtr, string, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> GiveNamedItemFunc =
        new(GameData.GetSignature("GiveNamedItem"));
    public static Func<IntPtr, string, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> GiveNamedItem => GiveNamedItemFunc.Invoke;

    // ── Eager static FIELDS with DEFERRED signature resolution ──
    // These are public static FIELDS (not Lazy-backed properties) so plugins compiled against the
    // field-based API resolve them via `ldsfld FooFunc` without MissingFieldException. The
    // `new(() => GameData.GetSignature("..."))` ctor stores a signature factory and does NOT resolve
    // the gamedata key until the function is first invoked/hooked (see BaseMemoryFunction's deferred
    // ctor + EnsureNativeHandle). So a missing key throws only when THAT function is used — the same
    // per-member isolation Lazy<T> gave — while keeping field ABI. Do NOT convert these back to
    // properties: it silently breaks every plugin that references them as fields.
    public static readonly MemoryFunctionVoid<IntPtr, byte> SwitchTeamFunc =
        new(() => GameData.GetSignature("CCSPlayerController_SwitchTeam"));
    public static Action<IntPtr, byte> SwitchTeam => SwitchTeamFunc.Invoke;

    // void(*UTIL_Remove)(CEntityInstance*);
    public static readonly MemoryFunctionVoid<IntPtr> UTIL_RemoveFunc =
        new(() => GameData.GetSignature("UTIL_Remove"));
    public static Action<IntPtr> UTIL_Remove => UTIL_RemoveFunc.Invoke;

    // void(*CBaseModelEntity_SetModel)(CBaseModelEntity*, const char*);
    public static readonly MemoryFunctionVoid<IntPtr, string> SetModelFunc =
        new(() => GameData.GetSignature("CBaseModelEntity_SetModel"));
    public static Action<IntPtr, string> SetModel => SetModelFunc.Invoke;

    [Obsolete("Use TerminateRoundFuncLinux or TerminateRoundFuncWindows instead")]
    public static readonly MemoryFunctionVoid<IntPtr, RoundEndReason, float, IntPtr, byte> TerminateRoundFunc =
        new(() => GameData.GetSignature("CCSGameRules_TerminateRound"));

    [Obsolete("Use TerminateRoundLinux or TerminateRoundWindows instead")]
    public static Action<IntPtr, RoundEndReason, float, IntPtr, byte> TerminateRound => TerminateRoundFunc.Invoke;

    public static readonly MemoryFunctionVoid<IntPtr, RoundEndReason, float, IntPtr, byte> TerminateRoundFuncLinux =
        new(() => GameData.GetSignature("CCSGameRules_TerminateRound"));
    public static Action<IntPtr, RoundEndReason, float, IntPtr, byte> TerminateRoundLinux => TerminateRoundFuncLinux.Invoke;

    public static readonly MemoryFunctionVoid<IntPtr, float, RoundEndReason, IntPtr, byte> TerminateRoundFuncWindows =
        new(() => GameData.GetSignature("CCSGameRules_TerminateRound"));
    public static Action<IntPtr, float, RoundEndReason, IntPtr, byte> TerminateRoundWindows => TerminateRoundFuncWindows.Invoke;

    public static readonly MemoryFunctionWithReturn<string, int, IntPtr> UTIL_CreateEntityByNameFunc =
        new(() => GameData.GetSignature("UTIL_CreateEntityByName"));
    public static Func<string, int, IntPtr> UTIL_CreateEntityByName => UTIL_CreateEntityByNameFunc.Invoke;

    public static readonly MemoryFunctionVoid<IntPtr, IntPtr> CBaseEntity_DispatchSpawnFunc =
        new(() => GameData.GetSignature("CBaseEntity_DispatchSpawn"));
    public static Action<IntPtr, IntPtr> CBaseEntity_DispatchSpawn => CBaseEntity_DispatchSpawnFunc.Invoke;

    public static readonly MemoryFunctionVoid<CBasePlayerController, CBasePlayerPawn, bool, bool> CBasePlayerController_SetPawnFunc =
        new(() => GameData.GetSignature("CBasePlayerController_SetPawn"));

    [Obsolete("Use Listeners.OnEntityTakeDamagePre instead")]
    public static readonly MemoryFunctionVoid<CEntityInstance, CTakeDamageInfo, CTakeDamageResult> CBaseEntity_TakeDamageOldFunc =
        new(() => GameData.GetSignature("CBaseEntity_TakeDamageOld"));

    public static Action<CEntityInstance, CTakeDamageInfo, CTakeDamageResult> CBaseEntity_TakeDamageOld => CBaseEntity_TakeDamageOldFunc.Invoke;

    // Compatibility alias used by older third-party plugins (e.g. WC3) that hook the entity TakeDamage
    // via DynamicHook with two parameters (entity, info). The underlying native function is the same one
    // backing CBaseEntity_TakeDamageOldFunc above; declaring it with two generic params here is intentional —
    // hook callbacks read parameters by index via DynoHook regardless of declared arity, so plugins reading
    // hook.GetParam<CEntityInstance>(0) and hook.GetParam<CTakeDamageInfo>(1) work without modification.
    // For invoking the function directly, prefer CBaseEntity_TakeDamageOldFunc (correct 3-arg ABI) or the
    // OnEntityTakeDamagePre/Post listeners.
    public static readonly MemoryFunctionVoid<CEntityInstance, CTakeDamageInfo> CBaseEntity_TakeDamageFunc =
        new(() => GameData.GetSignature("CBaseEntity_TakeDamage"));

    public static readonly MemoryFunctionWithReturn<CCSPlayer_WeaponServices, CBasePlayerWeapon, bool> CCSPlayer_WeaponServices_CanUseFunc =
        new(() => GameData.GetSignature("CCSPlayer_WeaponServices_CanUse"));
    public static Func<CCSPlayer_WeaponServices, CBasePlayerWeapon, bool> CCSPlayer_WeaponServices_CanUse => CCSPlayer_WeaponServices_CanUseFunc.Invoke;

    public static readonly MemoryFunctionWithReturn<int, string, CCSWeaponBaseVData> GetCSWeaponDataFromKeyFunc =
        new(() => GameData.GetSignature("GetCSWeaponDataFromKey"));
    public static Func<int, string, CCSWeaponBaseVData> GetCSWeaponDataFromKey => GetCSWeaponDataFromKeyFunc.Invoke;

    // Eager FIELD with deferred signature resolution (see the block comment above SwitchTeamFunc).
    // Field ABI is required by plugins that emit `ldsfld CCSPlayer_ItemServices_CanAcquireFunc`
    // (e.g. Deathmatch); a property would break them with MissingFieldException. The `() => ...`
    // factory defers the gamedata lookup to first invoke.
    public static readonly MemoryFunctionWithReturn<CCSPlayer_ItemServices, CEconItemView, AcquireMethod, IntPtr, AcquireResult> CCSPlayer_ItemServices_CanAcquireFunc =
        new(() => GameData.GetSignature("CCSPlayer_ItemServices_CanAcquire"));
    public static Func<CCSPlayer_ItemServices, CEconItemView, AcquireMethod, IntPtr, AcquireResult> CCSPlayer_ItemServices_CanAcquire => CCSPlayer_ItemServices_CanAcquireFunc.Invoke;

    public static readonly MemoryFunctionVoid<CCSPlayerPawnBase> CCSPlayerPawnBase_PostThinkFunc =
        new(() => GameData.GetSignature("CCSPlayerPawnBase_PostThink"));
    public static Action<CCSPlayerPawnBase> CCSPlayerPawnBase_PostThink => CCSPlayerPawnBase_PostThinkFunc.Invoke;

    public static readonly MemoryFunctionVoid<CBaseTrigger, CBaseEntity> CBaseTrigger_StartTouchFunc =
        new(() => GameData.GetSignature("CBaseTrigger_StartTouch"));
    public static Action<CBaseTrigger, CBaseEntity> CBaseTrigger_StartTouch => CBaseTrigger_StartTouchFunc.Invoke;

    public static readonly MemoryFunctionVoid<CBaseTrigger, CBaseEntity> CBaseTrigger_EndTouchFunc =
        new(() => GameData.GetSignature("CBaseTrigger_EndTouch"));
    public static Action<CBaseTrigger, CBaseEntity> CBaseTrigger_EndTouch => CBaseTrigger_EndTouchFunc.Invoke;

    public static readonly MemoryFunctionVoid<IntPtr, IntPtr> RemovePlayerItemFunc =
        new(() => GameData.GetSignature("CBasePlayerPawn_RemovePlayerItem"));
    public static Action<IntPtr, IntPtr> RemovePlayerItemVirtual => RemovePlayerItemFunc.Invoke;
}
