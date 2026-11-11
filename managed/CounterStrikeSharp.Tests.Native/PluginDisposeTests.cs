using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using Xunit;
using static TestUtils;

namespace NativeTestsPlugin;

// One handler registered under two keys (two commands, or one event in both hook modes) used to
// overwrite the first entry in BasePlugin's handler-keyed dictionaries: Dispose and the explicit
// remove methods then unhooked only one of them, and the other kept firing after unload.
public class PluginDisposeTests
{
    private sealed class ThrowawayPlugin : BasePlugin
    {
        public override string ModuleName => "Dispose test";
        public override string ModuleVersion => "1.0.0";
    }

    [Fact]
    public async Task Dispose_UnhooksACommandListenerRegisteredForTwoCommands()
    {
        var calls = 0;
        HookResult Listener(CCSPlayerController? player, CommandInfo info)
        {
            calls++;
            return HookResult.Continue;
        }

        var plugin = new ThrowawayPlugin();
        plugin.AddCommandListener("echo", Listener);
        plugin.AddCommandListener("say", Listener);

        NativeAPI.IssueServerCommand("echo before dispose");
        NativeAPI.IssueServerCommand("say before dispose");
        Assert.True(await WaitUntil(() => calls == 2), $"expected both listeners to fire, got {calls} calls");

        plugin.Dispose();
        NativeAPI.IssueServerCommand("echo after dispose");
        NativeAPI.IssueServerCommand("say after dispose");
        await WaitForSeconds(0.25f);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Dispose_UnhooksAnEventHandlerRegisteredInBothModes()
    {
        var calls = 0;
        HookResult Handler(EventShowSurvivalRespawnStatus @event, GameEventInfo info)
        {
            calls++;
            return HookResult.Continue;
        }

        var plugin = new ThrowawayPlugin();
        plugin.RegisterEventHandler<EventShowSurvivalRespawnStatus>(Handler, HookMode.Pre);
        plugin.RegisterEventHandler<EventShowSurvivalRespawnStatus>(Handler, HookMode.Post);

        new EventShowSurvivalRespawnStatus(true).FireEvent(true);
        Assert.True(await WaitUntil(() => calls == 2), $"expected Pre and Post to fire, got {calls} calls");

        plugin.Dispose();
        new EventShowSurvivalRespawnStatus(true).FireEvent(true);
        await WaitFrames(3);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RemoveCommandListener_OfOneHandlerRegisteredForTwoCommands_RemovesTheOneAskedFor()
    {
        var calls = 0;
        HookResult Listener(CCSPlayerController? player, CommandInfo info)
        {
            calls++;
            return HookResult.Continue;
        }

        var plugin = new ThrowawayPlugin();
        plugin.AddCommandListener("echo", Listener);
        plugin.AddCommandListener("say", Listener);

        try
        {
            plugin.RemoveCommandListener("echo", Listener, HookMode.Pre);
            NativeAPI.IssueServerCommand("echo removed");
            await WaitForSeconds(0.25f);
            Assert.Equal(0, calls);

            NativeAPI.IssueServerCommand("say kept");
            Assert.True(await WaitUntil(() => calls == 1), "expected the listener kept on say to fire");
        }
        finally
        {
            plugin.Dispose();
        }
    }
}
