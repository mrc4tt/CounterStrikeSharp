using System.Threading.Tasks;
using CounterStrikeSharp.API.Core;
using Xunit;
using static TestUtils;

namespace NativeTestsPlugin;

public class ListenerRegistrationTests
{
    // Registering one handler twice used to overwrite the first subscriber in BasePlugin.Listeners:
    // RemoveListener then removed only the second, and the first kept firing (and rooted the plugin).
    [Fact]
    public async Task RegisterListener_Twice_RemoveListenerRemovesBoth()
    {
        var plugin = NativeTestsPlugin.Instance;
        var calls = 0;
        Listeners.OnTick handler = () => calls++;

        plugin.RegisterListener(handler);
        plugin.RegisterListener(handler);
        try
        {
            await WaitFrames(2);
            Assert.True(calls >= 2, $"expected both registrations to fire, got {calls} calls");
        }
        finally
        {
            plugin.RemoveListener(handler);
        }

        await WaitOneFrame();
        calls = 0;
        await WaitFrames(3);
        Assert.Equal(0, calls);
        Assert.False(plugin.Listeners.ContainsKey(handler));
    }
}
