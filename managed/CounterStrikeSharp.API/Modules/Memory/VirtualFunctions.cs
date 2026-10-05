using System;
using System.ComponentModel;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;

namespace CounterStrikeSharp.API.Modules.Memory;

// Binary compatibility contract — VirtualFunctionsAbiTests guards every rule below.
//
//  1. FIELD ABI. Every public member is a public static FIELD with the same name and type as in
//     upstream CounterStrikeSharp (1.0.376): the MemoryFunction (`FooFunc`) and its Action/Func
//     companion (`Foo = FooFunc.Invoke`). Plugins built against upstream emit `ldsfld Foo`, which
//     binds on name AND field type; a property, rename or retype throws MissingFieldException when
//     the referencing plugin method is JIT-compiled. Do NOT convert these to properties.
//
//  2. ACCESSOR SHIMS. Fork releases 1.0.389-1.0.406 shipped 17 companions as get-only properties, so
//     plugins built against those emit `call get_Foo()`. The hidden `get_Foo()` methods at the bottom
//     keep them binding. C# only reserves the get_Foo name when a property Foo exists, so the method
//     sits beside the field.
//
//  3. READONLY. Companions are `static readonly` (upstream's are writable). Core and every plugin
//     share these statics, so a writable companion lets one plugin reroute or null core behaviour for
//     all of them and pin its unloaded AssemblyLoadContext. ldsfld binds to initonly fields, so this
//     costs no compatibility, and the JIT can treat the value as a constant. Core calls
//     `FooFunc.Invoke` directly rather than going through a companion.
//
//  4. PER-MEMBER ISOLATION. Most bindings use the deferred `new(() => GameData.GetSignature("Foo"))`
//     ctor, which resolves the gamedata key on first invoke/hook (BaseMemoryFunction.EnsureNativeHandle),
//     so a missing key fails only that binding. An eager `new(GameData.GetSignature("Foo"))` resolves
//     inside the static constructor, where one missing key throws TypeInitializationException for the
//     whole class. ClientPrint, UTIL_ClientPrintAll and GiveNamedItem are eager on purpose: those sigs
//     are always present. Creating a companion from FooFunc.Invoke does not resolve the signature.
//
//  5. ORDER. Each companion must be declared AFTER its *Func field: static field initializers run in
//     textual order, and a companion bound to a still-null *Func throws inside the static constructor,
//     taking down the whole class.
public static class VirtualFunctions
{
    public static readonly MemoryFunctionVoid<IntPtr, HudDestination, string, IntPtr, IntPtr, IntPtr, IntPtr> ClientPrintFunc =
        new(GameData.GetSignature("ClientPrint"));
    public static readonly Action<IntPtr, HudDestination, string, IntPtr, IntPtr, IntPtr, IntPtr> ClientPrint = ClientPrintFunc.Invoke;

    public static readonly MemoryFunctionVoid<HudDestination, string, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> ClientPrintAllFunc =
        new(GameData.GetSignature("UTIL_ClientPrintAll"));
    public static readonly Action<HudDestination, string, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> ClientPrintAll = ClientPrintAllFunc.Invoke;

    // void (*FnGiveNamedItem)(void* itemService,const char* pchName, void* iSubType,void* pScriptItem, void* a5,void* a6) = nullptr;
    public static readonly MemoryFunctionWithReturn<IntPtr, string, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> GiveNamedItemFunc =
        new(GameData.GetSignature("GiveNamedItem"));
    public static readonly Func<IntPtr, string, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> GiveNamedItem = GiveNamedItemFunc.Invoke;

    // ── Deferred signature resolution from here on ──
    public static readonly MemoryFunctionVoid<IntPtr, byte> SwitchTeamFunc =
        new(() => GameData.GetSignature("CCSPlayerController_SwitchTeam"));
    public static readonly Action<IntPtr, byte> SwitchTeam = SwitchTeamFunc.Invoke;

    // void(*UTIL_Remove)(CEntityInstance*);
    public static readonly MemoryFunctionVoid<IntPtr> UTIL_RemoveFunc =
        new(() => GameData.GetSignature("UTIL_Remove"));
    public static readonly Action<IntPtr> UTIL_Remove = UTIL_RemoveFunc.Invoke;

    // void(*CBaseModelEntity_SetModel)(CBaseModelEntity*, const char*);
    public static readonly MemoryFunctionVoid<IntPtr, string> SetModelFunc =
        new(() => GameData.GetSignature("CBaseModelEntity_SetModel"));
    public static readonly Action<IntPtr, string> SetModel = SetModelFunc.Invoke;

    [Obsolete("Use TerminateRoundFuncLinux or TerminateRoundFuncWindows instead")]
    public static readonly MemoryFunctionVoid<IntPtr, RoundEndReason, float, IntPtr, byte> TerminateRoundFunc =
        new(() => GameData.GetSignature("CCSGameRules_TerminateRound"));

    [Obsolete("Use TerminateRoundLinux or TerminateRoundWindows instead")]
    public static readonly Action<IntPtr, RoundEndReason, float, IntPtr, byte> TerminateRound = TerminateRoundFunc.Invoke;

    public static readonly MemoryFunctionVoid<IntPtr, RoundEndReason, float, IntPtr, byte> TerminateRoundFuncLinux =
        new(() => GameData.GetSignature("CCSGameRules_TerminateRound"));
    public static readonly Action<IntPtr, RoundEndReason, float, IntPtr, byte> TerminateRoundLinux = TerminateRoundFuncLinux.Invoke;

    public static readonly MemoryFunctionVoid<IntPtr, float, RoundEndReason, IntPtr, byte> TerminateRoundFuncWindows =
        new(() => GameData.GetSignature("CCSGameRules_TerminateRound"));
    public static readonly Action<IntPtr, float, RoundEndReason, IntPtr, byte> TerminateRoundWindows = TerminateRoundFuncWindows.Invoke;

    public static readonly MemoryFunctionWithReturn<string, int, IntPtr> UTIL_CreateEntityByNameFunc =
        new(() => GameData.GetSignature("UTIL_CreateEntityByName"));
    public static readonly Func<string, int, IntPtr> UTIL_CreateEntityByName = UTIL_CreateEntityByNameFunc.Invoke;

    public static readonly MemoryFunctionVoid<IntPtr, IntPtr> CBaseEntity_DispatchSpawnFunc =
        new(() => GameData.GetSignature("CBaseEntity_DispatchSpawn"));
    public static readonly Action<IntPtr, IntPtr> CBaseEntity_DispatchSpawn = CBaseEntity_DispatchSpawnFunc.Invoke;

    // SetPawn takes four bool flags; this binding only passes two, so the last two
    // arrive as whatever was left in those argument registers. Kept (not removed) so
    // plugins compiled against the field still load — new code uses SetPawnFullFunc.
    [Obsolete("Passes only two of SetPawn's four flags. Use CBasePlayerController_SetPawnFullFunc instead")]
    public static readonly MemoryFunctionVoid<CBasePlayerController, CBasePlayerPawn, bool, bool> CBasePlayerController_SetPawnFunc =
        new(() => GameData.GetSignature("CBasePlayerController_SetPawn"));

    public static readonly MemoryFunctionVoid<CBasePlayerController, CBasePlayerPawn, bool, bool, bool, bool> CBasePlayerController_SetPawnFullFunc =
        new(() => GameData.GetSignature("CBasePlayerController_SetPawn"));

    [Obsolete("Use Listeners.OnEntityTakeDamagePre instead")]
    public static readonly MemoryFunctionVoid<CEntityInstance, CTakeDamageInfo, CTakeDamageResult> CBaseEntity_TakeDamageOldFunc =
        new(() => GameData.GetSignature("CBaseEntity_TakeDamageOld"));

    // Companion of the obsolete field above; referencing it here is intended.
#pragma warning disable CS0618
    public static readonly Action<CEntityInstance, CTakeDamageInfo, CTakeDamageResult> CBaseEntity_TakeDamageOld = CBaseEntity_TakeDamageOldFunc.Invoke;
#pragma warning restore CS0618

    // Compatibility alias used by older third-party plugins (e.g. WC3) that hook the entity TakeDamage
    // via DynamicHook with two parameters (entity, info). The underlying native function is the same one
    // backing CBaseEntity_TakeDamageOldFunc above; declaring it with two generic params here is intentional —
    // DynamicHookContext reads parameters by index regardless of declared arity, so plugins reading
    // hook.GetParam<CEntityInstance>(0) and hook.GetParam<CTakeDamageInfo>(1) work without modification.
    // For invoking the function directly, prefer CBaseEntity_TakeDamageOldFunc (correct 3-arg ABI) or the
    // OnEntityTakeDamagePre/Post listeners.
    public static readonly MemoryFunctionVoid<CEntityInstance, CTakeDamageInfo> CBaseEntity_TakeDamageFunc =
        new(() => GameData.GetSignature("CBaseEntity_TakeDamage"));

    public static readonly MemoryFunctionWithReturn<CCSPlayer_WeaponServices, CBasePlayerWeapon, bool> CCSPlayer_WeaponServices_CanUseFunc =
        new(() => GameData.GetSignature("CCSPlayer_WeaponServices_CanUse"));
    public static readonly Func<CCSPlayer_WeaponServices, CBasePlayerWeapon, bool> CCSPlayer_WeaponServices_CanUse = CCSPlayer_WeaponServices_CanUseFunc.Invoke;

    public static readonly MemoryFunctionWithReturn<int, string, CCSWeaponBaseVData> GetCSWeaponDataFromKeyFunc =
        new(() => GameData.GetSignature("GetCSWeaponDataFromKey"));
    public static readonly Func<int, string, CCSWeaponBaseVData> GetCSWeaponDataFromKey = GetCSWeaponDataFromKeyFunc.Invoke;

    public static readonly MemoryFunctionWithReturn<CCSPlayer_ItemServices, CEconItemView, AcquireMethod, IntPtr, AcquireResult> CCSPlayer_ItemServices_CanAcquireFunc =
        new(() => GameData.GetSignature("CCSPlayer_ItemServices_CanAcquire"));
    public static readonly Func<CCSPlayer_ItemServices, CEconItemView, AcquireMethod, IntPtr, AcquireResult> CCSPlayer_ItemServices_CanAcquire = CCSPlayer_ItemServices_CanAcquireFunc.Invoke;

    public static readonly MemoryFunctionVoid<CCSPlayerPawnBase> CCSPlayerPawnBase_PostThinkFunc =
        new(() => GameData.GetSignature("CCSPlayerPawnBase_PostThink"));
    public static readonly Action<CCSPlayerPawnBase> CCSPlayerPawnBase_PostThink = CCSPlayerPawnBase_PostThinkFunc.Invoke;

    public static readonly MemoryFunctionVoid<CBaseTrigger, CBaseEntity> CBaseTrigger_StartTouchFunc =
        new(() => GameData.GetSignature("CBaseTrigger_StartTouch"));
    public static readonly Action<CBaseTrigger, CBaseEntity> CBaseTrigger_StartTouch = CBaseTrigger_StartTouchFunc.Invoke;

    public static readonly MemoryFunctionVoid<CBaseTrigger, CBaseEntity> CBaseTrigger_EndTouchFunc =
        new(() => GameData.GetSignature("CBaseTrigger_EndTouch"));
    public static readonly Action<CBaseTrigger, CBaseEntity> CBaseTrigger_EndTouch = CBaseTrigger_EndTouchFunc.Invoke;

    public static readonly MemoryFunctionVoid<IntPtr, IntPtr> RemovePlayerItemFunc =
        new(() => GameData.GetSignature("CBasePlayerPawn_RemovePlayerItem"));
    public static readonly Action<IntPtr, IntPtr> RemovePlayerItemVirtual = RemovePlayerItemFunc.Invoke;

    // ── Accessor shims for plugins built against fork 1.0.389-1.0.406 (rule 2 above) ──
    // Not for new code: read the field instead.
    [EditorBrowsable(EditorBrowsableState.Never)] public static Func<IntPtr, string, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> get_GiveNamedItem() => GiveNamedItem;
    [EditorBrowsable(EditorBrowsableState.Never)] public static Action<IntPtr, byte> get_SwitchTeam() => SwitchTeam;
    [EditorBrowsable(EditorBrowsableState.Never)] public static Action<IntPtr> get_UTIL_Remove() => UTIL_Remove;
    [EditorBrowsable(EditorBrowsableState.Never)] public static Action<IntPtr, string> get_SetModel() => SetModel;
#pragma warning disable CS0618
    [EditorBrowsable(EditorBrowsableState.Never)] public static Action<IntPtr, RoundEndReason, float, IntPtr, byte> get_TerminateRound() => TerminateRound;
#pragma warning restore CS0618
    [EditorBrowsable(EditorBrowsableState.Never)] public static Action<IntPtr, RoundEndReason, float, IntPtr, byte> get_TerminateRoundLinux() => TerminateRoundLinux;
    [EditorBrowsable(EditorBrowsableState.Never)] public static Action<IntPtr, float, RoundEndReason, IntPtr, byte> get_TerminateRoundWindows() => TerminateRoundWindows;
    [EditorBrowsable(EditorBrowsableState.Never)] public static Func<string, int, IntPtr> get_UTIL_CreateEntityByName() => UTIL_CreateEntityByName;
    [EditorBrowsable(EditorBrowsableState.Never)] public static Action<IntPtr, IntPtr> get_CBaseEntity_DispatchSpawn() => CBaseEntity_DispatchSpawn;
    [EditorBrowsable(EditorBrowsableState.Never)] public static Action<CEntityInstance, CTakeDamageInfo, CTakeDamageResult> get_CBaseEntity_TakeDamageOld() => CBaseEntity_TakeDamageOld;
    [EditorBrowsable(EditorBrowsableState.Never)] public static Func<CCSPlayer_WeaponServices, CBasePlayerWeapon, bool> get_CCSPlayer_WeaponServices_CanUse() => CCSPlayer_WeaponServices_CanUse;
    [EditorBrowsable(EditorBrowsableState.Never)] public static Func<int, string, CCSWeaponBaseVData> get_GetCSWeaponDataFromKey() => GetCSWeaponDataFromKey;
    [EditorBrowsable(EditorBrowsableState.Never)] public static Func<CCSPlayer_ItemServices, CEconItemView, AcquireMethod, IntPtr, AcquireResult> get_CCSPlayer_ItemServices_CanAcquire() => CCSPlayer_ItemServices_CanAcquire;
    [EditorBrowsable(EditorBrowsableState.Never)] public static Action<CCSPlayerPawnBase> get_CCSPlayerPawnBase_PostThink() => CCSPlayerPawnBase_PostThink;
    [EditorBrowsable(EditorBrowsableState.Never)] public static Action<CBaseTrigger, CBaseEntity> get_CBaseTrigger_StartTouch() => CBaseTrigger_StartTouch;
    [EditorBrowsable(EditorBrowsableState.Never)] public static Action<CBaseTrigger, CBaseEntity> get_CBaseTrigger_EndTouch() => CBaseTrigger_EndTouch;
    [EditorBrowsable(EditorBrowsableState.Never)] public static Action<IntPtr, IntPtr> get_RemovePlayerItemVirtual() => RemovePlayerItemVirtual;
}
