using System.Linq;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Xunit;

namespace NativeTestsPlugin;

public class EntityTests
{
    [Fact]
    public void FindEntityAndAccessSchemaMembers()
    {
        var world = Utilities.FindAllEntitiesByDesignerName<CWorld>("worldent").FirstOrDefault();

        Assert.NotNull(world);
        Assert.Equal("worldent", world.DesignerName);
        Assert.Equal((uint)0, world.Index);

        Assert.Multiple(() =>
        {
            Assert.Equal(0, world.AbsOrigin!.X);
            Assert.Equal(0, world.AbsOrigin.Y);
            Assert.Equal(0, world.AbsOrigin.Z);
        });
    }

    [Fact]
    public void GetGameRulesMatchesEntityWalk()
    {
        var proxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        Assert.NotNull(proxy);

        var cachedProxy = Utilities.GetGameRulesProxy();
        Assert.NotNull(cachedProxy);
        Assert.Equal(proxy.Handle, cachedProxy.Handle);
        Assert.Same(cachedProxy, Utilities.GetGameRulesProxy());

        var rules = Utilities.GetGameRules();
        Assert.NotNull(rules);
        Assert.Equal(proxy.GameRules!.Handle, rules.Handle);
        Assert.Same(rules, Utilities.GetGameRules());
    }
}
