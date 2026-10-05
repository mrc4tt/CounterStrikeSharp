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

    [Fact]
    public void HandlerCallingItsOwnFunction_StopsAtDepthLimit()
    {
        // A pure lookup, safe to call any number of times.
        var function = VirtualFunctions.GetCSWeaponDataFromKeyFunc;
        var handlerCalls = 0;

        Func<DynamicHook, HookResult> handler = _ =>
        {
            handlerCalls++;
            // No bypass: this re-enters the hook we are in.
            function.Invoke(-1, "weapon_ak47");
            return HookResult.Continue;
        };

        function.Hook(handler, HookMode.Pre);
        try
        {
            var data = function.Invoke(-1, "weapon_ak47");

            Assert.NotNull(data);
            Assert.Equal(MaxHandlerDepth, handlerCalls);
        }
        finally
        {
            function.Unhook(handler, HookMode.Pre);
        }
    }

    // Below the limit every nested call still reaches the handler.
    [Fact]
    public void ShallowNesting_RunsHandlerEveryTime()
    {
        var function = VirtualFunctions.GetCSWeaponDataFromKeyFunc;
        var handlerCalls = 0;
        var nested = 0;

        Func<DynamicHook, HookResult> handler = _ =>
        {
            handlerCalls++;
            if (nested < 2)
            {
                nested++;
                function.Invoke(-1, "weapon_ak47");
            }
            return HookResult.Continue;
        };

        function.Hook(handler, HookMode.Pre);
        try
        {
            function.Invoke(-1, "weapon_ak47");
            Assert.Equal(3, handlerCalls);
        }
        finally
        {
            function.Unhook(handler, HookMode.Pre);
        }
    }
}
