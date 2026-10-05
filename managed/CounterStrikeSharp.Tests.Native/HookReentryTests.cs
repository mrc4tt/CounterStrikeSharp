using System.Threading.Tasks;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using Xunit;

namespace NativeTestsPlugin;

// Guards the DynamicHook re-entry limit (src/core/dynamic_hook.cpp, kMaxHandlerDepth = 8). A handler
// that calls the function it hooks without bypass used to recurse until "Stack overflow." killed the
// server.
public class HookReentryTests
{
    private const int MaxHandlerDepth = 8;

    // A pure lookup, safe to call any number of times.
    private static readonly MemoryFunctionWithReturn<int, string, CCSWeaponBaseVData> Function =
        VirtualFunctions.GetCSWeaponDataFromKeyFunc;

    private static CCSWeaponBaseVData Call() => Function.Invoke(-1, "weapon_ak47");

    // KHook inserts a hook asynchronously (a worker thread, ~5 ms) when the function already has a detour,
    // e.g. from a hook removed moments ago, so Hook() returning does not mean the handler is live yet.
    private static async Task HookAndWaitUntilLive(Func<DynamicHook, HookResult> handler, Func<bool> probed)
    {
        Function.Hook(handler, HookMode.Pre);
        Assert.True(await WaitUntil(() =>
        {
            Call();
            return probed();
        }), "hook never became live");
    }

    [Fact]
    public async Task HandlerCallingItsOwnFunction_StopsAtDepthLimit()
    {
        var live = false;
        var handlerCalls = 0;

        Func<DynamicHook, HookResult> handler = _ =>
        {
            if (!live)
            {
                live = true;
                return HookResult.Continue;
            }

            handlerCalls++;
            // No bypass: this re-enters the hook we are in.
            Call();
            return HookResult.Continue;
        };

        await HookAndWaitUntilLive(handler, () => live);
        try
        {
            Assert.NotNull(Call());
            Assert.Equal(MaxHandlerDepth, handlerCalls);
        }
        finally
        {
            Function.Unhook(handler, HookMode.Pre);
        }
    }

    // Below the limit every nested call still reaches the handler.
    [Fact]
    public async Task ShallowNesting_RunsHandlerEveryTime()
    {
        var live = false;
        var handlerCalls = 0;
        var nested = 0;

        Func<DynamicHook, HookResult> handler = _ =>
        {
            if (!live)
            {
                live = true;
                return HookResult.Continue;
            }

            handlerCalls++;
            if (nested < 2)
            {
                nested++;
                Call();
            }
            return HookResult.Continue;
        };

        await HookAndWaitUntilLive(handler, () => live);
        try
        {
            Call();
            Assert.Equal(3, handlerCalls);
        }
        finally
        {
            Function.Unhook(handler, HookMode.Pre);
        }
    }
}
