using System;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Xunit;

namespace NativeTestsPlugin;

public class GameRulesTests
{
    private static bool _askedOnLoad;

    internal static void AskOnLoad()
    {
        // On a cold start Load runs before the entity system exists; GetGameRules must return null
        // there instead of throwing, and must not remember the miss.
        var rules = Utilities.GetGameRules();
        Console.WriteLine(rules == null
            ? "[GameRulesTests] GetGameRules during Load returned null (cold start)"
            : "[GameRulesTests] GetGameRules during Load returned the game rules: map already up, cold start case not exercised");
        _askedOnLoad = true;
    }

    [Fact]
    public void GetGameRules_MatchesTheEntityScan()
    {
        var scanned = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First().GameRules;
        var cached = Utilities.GetGameRules();

        Assert.NotNull(scanned);
        Assert.NotNull(cached);
        Assert.Equal(scanned!.Handle, cached!.Handle);
    }

    [Fact]
    public void GetGameRulesProxy_MatchesTheEntityScan()
    {
        var scanned = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First();
        var cached = Utilities.GetGameRulesProxy();

        Assert.NotNull(cached);
        Assert.Equal(scanned.Handle, cached!.Handle);
    }

    [Fact]
    public void GetGameRules_AskedDuringLoad_ResolvesOnceTheMapIsUp()
    {
        Assert.True(_askedOnLoad);
        Assert.NotNull(Utilities.GetGameRules());
    }

    [Fact]
    public void GetGameRules_ReturnsTheSameWrapperTwice()
    {
        Assert.Same(Utilities.GetGameRules(), Utilities.GetGameRules());
    }
}
