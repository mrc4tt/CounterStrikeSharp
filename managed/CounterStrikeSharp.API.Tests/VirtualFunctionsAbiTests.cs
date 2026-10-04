using System.Linq;
using System.Reflection;
using CounterStrikeSharp.API.Modules.Memory;

namespace CounterStrikeSharp.API.Tests;

// VirtualFunctions must keep upstream CounterStrikeSharp's FIELD ABI: plugins built against upstream
// emit ldsfld for these members and fail with MissingFieldException if any becomes a property.
// Reflection over fields does not run the static constructor, so no gamedata is needed.
public class VirtualFunctionsAbiTests
{
    // Public static fields of VirtualFunctions in upstream CounterStrikeSharp.API 1.0.369, plus the
    // CBaseEntity_TakeDamageFunc alias older plugins (e.g. WC3) hook.
    private static readonly string[] UpstreamFields =
    {
        "ClientPrintFunc", "ClientPrint",
        "ClientPrintAllFunc", "ClientPrintAll",
        "GiveNamedItemFunc", "GiveNamedItem",
        "SwitchTeamFunc", "SwitchTeam",
        "UTIL_RemoveFunc", "UTIL_Remove",
        "SetModelFunc", "SetModel",
        "TerminateRoundFunc", "TerminateRound",
        "TerminateRoundFuncLinux", "TerminateRoundLinux",
        "TerminateRoundFuncWindows", "TerminateRoundWindows",
        "UTIL_CreateEntityByNameFunc", "UTIL_CreateEntityByName",
        "CBaseEntity_DispatchSpawnFunc", "CBaseEntity_DispatchSpawn",
        "CBasePlayerController_SetPawnFunc",
        "CBaseEntity_TakeDamageOldFunc", "CBaseEntity_TakeDamageOld",
        "CBaseEntity_TakeDamageFunc",
        "CCSPlayer_WeaponServices_CanUseFunc", "CCSPlayer_WeaponServices_CanUse",
        "GetCSWeaponDataFromKeyFunc", "GetCSWeaponDataFromKey",
        "CCSPlayer_ItemServices_CanAcquireFunc", "CCSPlayer_ItemServices_CanAcquire",
        "CCSPlayerPawnBase_PostThinkFunc", "CCSPlayerPawnBase_PostThink",
        "CBaseTrigger_StartTouchFunc", "CBaseTrigger_StartTouch",
        "CBaseTrigger_EndTouchFunc", "CBaseTrigger_EndTouch",
        "RemovePlayerItemFunc", "RemovePlayerItemVirtual",
    };

    [Fact]
    public void HasNoPublicStaticProperties()
    {
        var properties = typeof(VirtualFunctions).GetProperties(BindingFlags.Public | BindingFlags.Static);
        Assert.Empty(properties.Select(p => p.Name));
    }

    [Fact]
    public void KeepsEveryUpstreamMemberAsPublicStaticField()
    {
        var missing = UpstreamFields
            .Where(name => typeof(VirtualFunctions).GetField(name, BindingFlags.Public | BindingFlags.Static) == null)
            .ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void DelegateCompanionsAreWritableLikeUpstream()
    {
        var readonlyCompanions = typeof(VirtualFunctions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => typeof(System.Delegate).IsAssignableFrom(f.FieldType) && f.IsInitOnly)
            .Select(f => f.Name)
            .ToList();
        Assert.Empty(readonlyCompanions);
    }
}
