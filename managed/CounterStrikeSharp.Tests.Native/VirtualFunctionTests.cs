using System.Linq;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using Xunit;

namespace NativeTestsPlugin;

public class VirtualFunctionTests
{
    // DynamicHook installs through KHook's insert worker (SetupHook async), so the detour goes live a
    // little after Hook() returns. Waiting exactly one frame for the first call was a race that failed
    // now and then with "Expected invocation on the mock at least once".
    private const int HookInstallFrames = 64;

    [Fact]
    public async Task ShouldHook()
    {
        var calls = 0;

        var hookHandler = (DynamicHook hook) =>
        {
            calls++;
            return HookResult.Continue;
        };

        // PostThink only runs for a live pawn.
        await EnsureAliveBot();

        try
        {
            VirtualFunctions.CCSPlayerPawnBase_PostThinkFunc.Hook(hookHandler, HookMode.Pre);

            // Verify hook
            Assert.True(await WaitUntil(() => calls > 0, HookInstallFrames), "PostThink pre-hook never ran");
        }
        finally
        {
            VirtualFunctions.CCSPlayerPawnBase_PostThinkFunc.Unhook(hookHandler, HookMode.Pre);
        }

        // Verify unhook: the managed callback is dropped synchronously, so nothing may run after it.
        calls = 0;
        await WaitFrames(3);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ShouldPreventPostHooks()
    {
        var preCalls = 0;
        var postCalls = 0;

        var preHookHandler = (DynamicHook hook) =>
        {
            preCalls++;
            return HookResult.Stop;
        };

        var postHookHandler = (DynamicHook hook) =>
        {
            postCalls++;
            return HookResult.Continue;
        };

        await EnsureAliveBot();

        try
        {
            VirtualFunctions.CCSPlayerPawnBase_PostThinkFunc.Hook(preHookHandler, HookMode.Pre);
            VirtualFunctions.CCSPlayerPawnBase_PostThinkFunc.Hook(postHookHandler, HookMode.Post);

            // Without waiting for the pre hook to actually run, a hook that was not installed yet
            // passed this test trivially.
            Assert.True(await WaitUntil(() => preCalls > 0, HookInstallFrames), "PostThink pre-hook never ran");
            await WaitFrames(3);
            Assert.True(preCalls > 1, $"expected the pre-hook to keep running, got {preCalls} calls");
            Assert.Equal(0, postCalls);
        }
        finally
        {
            VirtualFunctions.CCSPlayerPawnBase_PostThinkFunc.Unhook(preHookHandler, HookMode.Pre);
            VirtualFunctions.CCSPlayerPawnBase_PostThinkFunc.Unhook(postHookHandler, HookMode.Post);
        }
    }
}
