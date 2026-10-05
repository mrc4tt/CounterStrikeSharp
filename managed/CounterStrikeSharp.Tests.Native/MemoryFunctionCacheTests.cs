using System.Diagnostics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using Xunit;

namespace NativeTestsPlugin;

// Guards NativeFunctionCache and BaseMemoryFunction.Hook.
public class MemoryFunctionCacheTests
{
    // A pattern that matches nothing in the server binary.
    private const string MissingSignature = "DE AD BE EF CA FE BA BE 13 37 42 42 DE AD BE EF CA FE BA BE";

    // A plugin binding SetPawn with 4 args must not hand its 4-arg native function to core's 6-arg one.
    [Fact]
    public void SameSignatureWithDifferentArguments_GetsSeparateNativeFunctions()
    {
        var fourArgs = new MemoryFunctionVoid<CBasePlayerController, CBasePlayerPawn, bool, bool>(
            GameData.GetSignature("CBasePlayerController_SetPawn"));
        var sixArgs = VirtualFunctions.CBasePlayerController_SetPawnFullFunc;

        Assert.NotEqual(IntPtr.Zero, fourArgs.Handle);
        Assert.NotEqual(IntPtr.Zero, sixArgs.Handle);
        Assert.NotEqual(fourArgs.Handle, sixArgs.Handle);
    }

    [Fact]
    public void SameSignatureAndArguments_ShareNativeFunction()
    {
        var first = new MemoryFunctionVoid<IntPtr>(GameData.GetSignature("UTIL_Remove"));
        var second = new MemoryFunctionVoid<IntPtr>(GameData.GetSignature("UTIL_Remove"));

        Assert.NotEqual(IntPtr.Zero, first.Handle);
        Assert.Equal(first.Handle, second.Handle);
    }

    // The first lookup scans the whole binary; later ones must hit the negative cache.
    [Fact]
    public void UnresolvedSignature_IsNotRescanned()
    {
        var first = new MemoryFunctionVoid<IntPtr, int>(MissingSignature);
        Assert.Equal(IntPtr.Zero, first.Handle);

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(IntPtr.Zero, new MemoryFunctionVoid<IntPtr, int>(MissingSignature).Handle);
        }
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 50, $"20 cached lookups took {stopwatch.ElapsedMilliseconds} ms");
    }

    // A failed hook must not leave the handler rooted in FunctionReference.
    [Fact]
    public void FailedHook_ReleasesHandlerReference()
    {
        var function = new MemoryFunctionVoid<IntPtr, float>(MissingSignature);
        Func<DynamicHook, HookResult> handler = _ => HookResult.Continue;

        Assert.ThrowsAny<Exception>(() => function.Hook(handler, HookMode.Pre));
        Assert.False(FunctionReference.IsRegistered(handler));
    }
}
