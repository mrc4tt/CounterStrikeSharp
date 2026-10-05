using System.Reflection;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;

namespace CounterStrikeSharp.API.Tests;

// Guards the binary contract documented at the top of VirtualFunctions.cs. Plugins bind these members
// by name AND type (ldsfld Foo / call get_Foo()), so a rename, retype or field->property change throws
// MissingFieldException / MissingMethodException in already-built plugins. Reflection does not run the
// static constructor, so no gamedata is needed.
public class VirtualFunctionsAbiTests
{
    private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;

#pragma warning disable CS0618
    // Public static fields of upstream CounterStrikeSharp.API 1.0.376 with their exact types, plus the
    // fork-only CBaseEntity_TakeDamageFunc alias older plugins (e.g. WC3) hook.
    public static readonly TheoryData<string, Type> Fields = new()
    {
        { "ClientPrintFunc", typeof(MemoryFunctionVoid<IntPtr, HudDestination, string, IntPtr, IntPtr, IntPtr, IntPtr>) },
        { "ClientPrint", typeof(Action<IntPtr, HudDestination, string, IntPtr, IntPtr, IntPtr, IntPtr>) },
        { "ClientPrintAllFunc", typeof(MemoryFunctionVoid<HudDestination, string, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>) },
        { "ClientPrintAll", typeof(Action<HudDestination, string, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>) },
        { "GiveNamedItemFunc", typeof(MemoryFunctionWithReturn<IntPtr, string, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>) },
        { "GiveNamedItem", typeof(Func<IntPtr, string, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>) },
        { "SwitchTeamFunc", typeof(MemoryFunctionVoid<IntPtr, byte>) },
        { "SwitchTeam", typeof(Action<IntPtr, byte>) },
        { "UTIL_RemoveFunc", typeof(MemoryFunctionVoid<IntPtr>) },
        { "UTIL_Remove", typeof(Action<IntPtr>) },
        { "SetModelFunc", typeof(MemoryFunctionVoid<IntPtr, string>) },
        { "SetModel", typeof(Action<IntPtr, string>) },
        { "TerminateRoundFunc", typeof(MemoryFunctionVoid<IntPtr, RoundEndReason, float, IntPtr, byte>) },
        { "TerminateRound", typeof(Action<IntPtr, RoundEndReason, float, IntPtr, byte>) },
        { "TerminateRoundFuncLinux", typeof(MemoryFunctionVoid<IntPtr, RoundEndReason, float, IntPtr, byte>) },
        { "TerminateRoundLinux", typeof(Action<IntPtr, RoundEndReason, float, IntPtr, byte>) },
        { "TerminateRoundFuncWindows", typeof(MemoryFunctionVoid<IntPtr, float, RoundEndReason, IntPtr, byte>) },
        { "TerminateRoundWindows", typeof(Action<IntPtr, float, RoundEndReason, IntPtr, byte>) },
        { "UTIL_CreateEntityByNameFunc", typeof(MemoryFunctionWithReturn<string, int, IntPtr>) },
        { "UTIL_CreateEntityByName", typeof(Func<string, int, IntPtr>) },
        { "CBaseEntity_DispatchSpawnFunc", typeof(MemoryFunctionVoid<IntPtr, IntPtr>) },
        { "CBaseEntity_DispatchSpawn", typeof(Action<IntPtr, IntPtr>) },
        { "CBasePlayerController_SetPawnFunc", typeof(MemoryFunctionVoid<CBasePlayerController, CBasePlayerPawn, bool, bool>) },
        { "CBasePlayerController_SetPawnFullFunc", typeof(MemoryFunctionVoid<CBasePlayerController, CBasePlayerPawn, bool, bool, bool, bool>) },
        { "CBaseEntity_TakeDamageOldFunc", typeof(MemoryFunctionVoid<CEntityInstance, CTakeDamageInfo, CTakeDamageResult>) },
        { "CBaseEntity_TakeDamageOld", typeof(Action<CEntityInstance, CTakeDamageInfo, CTakeDamageResult>) },
        { "CBaseEntity_TakeDamageFunc", typeof(MemoryFunctionVoid<CEntityInstance, CTakeDamageInfo>) },
        { "CCSPlayer_WeaponServices_CanUseFunc", typeof(MemoryFunctionWithReturn<CCSPlayer_WeaponServices, CBasePlayerWeapon, bool>) },
        { "CCSPlayer_WeaponServices_CanUse", typeof(Func<CCSPlayer_WeaponServices, CBasePlayerWeapon, bool>) },
        { "GetCSWeaponDataFromKeyFunc", typeof(MemoryFunctionWithReturn<int, string, CCSWeaponBaseVData>) },
        { "GetCSWeaponDataFromKey", typeof(Func<int, string, CCSWeaponBaseVData>) },
        { "CCSPlayer_ItemServices_CanAcquireFunc", typeof(MemoryFunctionWithReturn<CCSPlayer_ItemServices, CEconItemView, AcquireMethod, IntPtr, AcquireResult>) },
        { "CCSPlayer_ItemServices_CanAcquire", typeof(Func<CCSPlayer_ItemServices, CEconItemView, AcquireMethod, IntPtr, AcquireResult>) },
        { "CCSPlayerPawnBase_PostThinkFunc", typeof(MemoryFunctionVoid<CCSPlayerPawnBase>) },
        { "CCSPlayerPawnBase_PostThink", typeof(Action<CCSPlayerPawnBase>) },
        { "CBaseTrigger_StartTouchFunc", typeof(MemoryFunctionVoid<CBaseTrigger, CBaseEntity>) },
        { "CBaseTrigger_StartTouch", typeof(Action<CBaseTrigger, CBaseEntity>) },
        { "CBaseTrigger_EndTouchFunc", typeof(MemoryFunctionVoid<CBaseTrigger, CBaseEntity>) },
        { "CBaseTrigger_EndTouch", typeof(Action<CBaseTrigger, CBaseEntity>) },
        { "RemovePlayerItemFunc", typeof(MemoryFunctionVoid<IntPtr, IntPtr>) },
        { "RemovePlayerItemVirtual", typeof(Action<IntPtr, IntPtr>) },
    };
#pragma warning restore CS0618

    // Companions fork 1.0.389-1.0.406 shipped as get-only properties; plugins built against those call get_Foo().
    public static readonly TheoryData<string> ForkPropertyCompanions = new()
    {
        "GiveNamedItem", "SwitchTeam", "UTIL_Remove", "SetModel",
        "TerminateRound", "TerminateRoundLinux", "TerminateRoundWindows",
        "UTIL_CreateEntityByName", "CBaseEntity_DispatchSpawn", "CBaseEntity_TakeDamageOld",
        "CCSPlayer_WeaponServices_CanUse", "GetCSWeaponDataFromKey", "CCSPlayer_ItemServices_CanAcquire",
        "CCSPlayerPawnBase_PostThink", "CBaseTrigger_StartTouch", "CBaseTrigger_EndTouch",
        "RemovePlayerItemVirtual",
    };

    [Theory]
    [MemberData(nameof(Fields))]
    public void KeepsUpstreamFieldWithExactType(string name, Type type)
    {
        var field = typeof(VirtualFunctions).GetField(name, PublicStatic);
        Assert.NotNull(field);
        Assert.Equal(type, field.FieldType);
    }

    [Theory]
    [MemberData(nameof(ForkPropertyCompanions))]
    public void KeepsForkAccessorShim(string name)
    {
        var getter = typeof(VirtualFunctions).GetMethod("get_" + name, PublicStatic, Type.EmptyTypes);
        Assert.NotNull(getter);
        Assert.Equal(typeof(VirtualFunctions).GetField(name, PublicStatic)!.FieldType, getter.ReturnType);
    }

    [Fact]
    public void HasNoPublicStaticProperties()
    {
        Assert.Empty(typeof(VirtualFunctions).GetProperties(PublicStatic));
    }

    [Fact]
    public void AllFieldsAreReadonly()
    {
        var writable = typeof(VirtualFunctions).GetFields(PublicStatic).Where(f => !f.IsInitOnly).Select(f => f.Name);
        Assert.Empty(writable);
    }
}
