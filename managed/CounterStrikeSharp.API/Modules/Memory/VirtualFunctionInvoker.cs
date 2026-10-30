using System;
using System.Runtime.CompilerServices;
using CounterStrikeSharp.API.Core;

namespace CounterStrikeSharp.API.Modules.Memory;

// Typed twin of NativeAPI.ExecuteVirtualFunction. That one takes object[], so every call allocated
// the array and boxed each value-type argument and the result; VirtualFunction delegates and
// MemoryFunction.Invoke run per frame. Same native (0x376A0359), same argument bytes:
// ScriptContext.PushArg/GetResultArg mirror Push(object)/GetResult(Type) for the primitives and
// fall back to them for everything else.
internal static class VirtualFunctionInvoker
{
    private const ulong ExecuteVirtualFunctionId = 0x376A0359;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ScriptContext Begin(IntPtr function, bool bypass)
    {
        var ctx = ScriptContext.GlobalScriptContext;
        ctx.Reset();
        ctx.PushPrimitive(function);
        ctx.PushPrimitive(bypass);
        return ctx;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Run(ScriptContext ctx)
    {
        ctx.SetIdentifier(ExecuteVirtualFunctionId);
        ctx.Invoke();
        ctx.CheckErrors();
    }

    public static void InvokeVoid(IntPtr function, bool bypass)
    {
        var ctx = Begin(function, bypass);
        Run(ctx);
    }

    public static TResult Invoke<TResult>(IntPtr function, bool bypass)
    {
        var ctx = Begin(function, bypass);
        Run(ctx);
        return ctx.GetResultArg<TResult>();
    }

    public static void InvokeVoid<TArg1>(IntPtr function, bool bypass, TArg1 arg1)
    {
        var ctx = Begin(function, bypass);
        ctx.PushArg(arg1);
        Run(ctx);
    }

    public static TResult Invoke<TArg1, TResult>(IntPtr function, bool bypass, TArg1 arg1)
    {
        var ctx = Begin(function, bypass);
        ctx.PushArg(arg1);
        Run(ctx);
        return ctx.GetResultArg<TResult>();
    }

    public static void InvokeVoid<TArg1, TArg2>(IntPtr function, bool bypass, TArg1 arg1, TArg2 arg2)
    {
        var ctx = Begin(function, bypass);
        ctx.PushArg(arg1);
        ctx.PushArg(arg2);
        Run(ctx);
    }

    public static TResult Invoke<TArg1, TArg2, TResult>(IntPtr function, bool bypass, TArg1 arg1, TArg2 arg2)
    {
        var ctx = Begin(function, bypass);
        ctx.PushArg(arg1);
        ctx.PushArg(arg2);
        Run(ctx);
        return ctx.GetResultArg<TResult>();
    }

    public static void InvokeVoid<TArg1, TArg2, TArg3>(IntPtr function, bool bypass, TArg1 arg1, TArg2 arg2, TArg3 arg3)
    {
        var ctx = Begin(function, bypass);
        ctx.PushArg(arg1);
        ctx.PushArg(arg2);
        ctx.PushArg(arg3);
        Run(ctx);
    }

    public static TResult Invoke<TArg1, TArg2, TArg3, TResult>(IntPtr function, bool bypass, TArg1 arg1, TArg2 arg2, TArg3 arg3)
    {
        var ctx = Begin(function, bypass);
        ctx.PushArg(arg1);
        ctx.PushArg(arg2);
        ctx.PushArg(arg3);
        Run(ctx);
        return ctx.GetResultArg<TResult>();
    }

    public static void InvokeVoid<TArg1, TArg2, TArg3, TArg4>(IntPtr function, bool bypass, TArg1 arg1, TArg2 arg2, TArg3 arg3, TArg4 arg4)
    {
        var ctx = Begin(function, bypass);
        ctx.PushArg(arg1);
        ctx.PushArg(arg2);
        ctx.PushArg(arg3);
        ctx.PushArg(arg4);
        Run(ctx);
    }

    public static TResult Invoke<TArg1, TArg2, TArg3, TArg4, TResult>(IntPtr function, bool bypass, TArg1 arg1, TArg2 arg2, TArg3 arg3, TArg4 arg4)
    {
        var ctx = Begin(function, bypass);
        ctx.PushArg(arg1);
        ctx.PushArg(arg2);
        ctx.PushArg(arg3);
        ctx.PushArg(arg4);
        Run(ctx);
        return ctx.GetResultArg<TResult>();
    }

    public static void InvokeVoid<TArg1, TArg2, TArg3, TArg4, TArg5>(IntPtr function, bool bypass, TArg1 arg1, TArg2 arg2, TArg3 arg3, TArg4 arg4, TArg5 arg5)
    {
        var ctx = Begin(function, bypass);
        ctx.PushArg(arg1);
        ctx.PushArg(arg2);
        ctx.PushArg(arg3);
        ctx.PushArg(arg4);
        ctx.PushArg(arg5);
        Run(ctx);
    }

    public static TResult Invoke<TArg1, TArg2, TArg3, TArg4, TArg5, TResult>(IntPtr function, bool bypass, TArg1 arg1, TArg2 arg2, TArg3 arg3, TArg4 arg4, TArg5 arg5)
    {
        var ctx = Begin(function, bypass);
        ctx.PushArg(arg1);
        ctx.PushArg(arg2);
        ctx.PushArg(arg3);
        ctx.PushArg(arg4);
        ctx.PushArg(arg5);
        Run(ctx);
        return ctx.GetResultArg<TResult>();
    }

    public static void InvokeVoid<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6>(IntPtr function, bool bypass, TArg1 arg1, TArg2 arg2, TArg3 arg3, TArg4 arg4, TArg5 arg5, TArg6 arg6)
    {
        var ctx = Begin(function, bypass);
        ctx.PushArg(arg1);
        ctx.PushArg(arg2);
        ctx.PushArg(arg3);
        ctx.PushArg(arg4);
        ctx.PushArg(arg5);
        ctx.PushArg(arg6);
        Run(ctx);
    }

    public static TResult Invoke<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TResult>(IntPtr function, bool bypass, TArg1 arg1, TArg2 arg2, TArg3 arg3, TArg4 arg4, TArg5 arg5, TArg6 arg6)
    {
        var ctx = Begin(function, bypass);
        ctx.PushArg(arg1);
        ctx.PushArg(arg2);
        ctx.PushArg(arg3);
        ctx.PushArg(arg4);
        ctx.PushArg(arg5);
        ctx.PushArg(arg6);
        Run(ctx);
        return ctx.GetResultArg<TResult>();
    }

    public static void InvokeVoid<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TArg7>(IntPtr function, bool bypass, TArg1 arg1, TArg2 arg2, TArg3 arg3, TArg4 arg4, TArg5 arg5, TArg6 arg6, TArg7 arg7)
    {
        var ctx = Begin(function, bypass);
        ctx.PushArg(arg1);
        ctx.PushArg(arg2);
        ctx.PushArg(arg3);
        ctx.PushArg(arg4);
        ctx.PushArg(arg5);
        ctx.PushArg(arg6);
        ctx.PushArg(arg7);
        Run(ctx);
    }

    public static TResult Invoke<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TArg7, TResult>(IntPtr function, bool bypass, TArg1 arg1, TArg2 arg2, TArg3 arg3, TArg4 arg4, TArg5 arg5, TArg6 arg6, TArg7 arg7)
    {
        var ctx = Begin(function, bypass);
        ctx.PushArg(arg1);
        ctx.PushArg(arg2);
        ctx.PushArg(arg3);
        ctx.PushArg(arg4);
        ctx.PushArg(arg5);
        ctx.PushArg(arg6);
        ctx.PushArg(arg7);
        Run(ctx);
        return ctx.GetResultArg<TResult>();
    }

    public static void InvokeVoid<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TArg7, TArg8>(IntPtr function, bool bypass, TArg1 arg1, TArg2 arg2, TArg3 arg3, TArg4 arg4, TArg5 arg5, TArg6 arg6, TArg7 arg7, TArg8 arg8)
    {
        var ctx = Begin(function, bypass);
        ctx.PushArg(arg1);
        ctx.PushArg(arg2);
        ctx.PushArg(arg3);
        ctx.PushArg(arg4);
        ctx.PushArg(arg5);
        ctx.PushArg(arg6);
        ctx.PushArg(arg7);
        ctx.PushArg(arg8);
        Run(ctx);
    }

    public static TResult Invoke<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TArg7, TArg8, TResult>(IntPtr function, bool bypass, TArg1 arg1, TArg2 arg2, TArg3 arg3, TArg4 arg4, TArg5 arg5, TArg6 arg6, TArg7 arg7, TArg8 arg8)
    {
        var ctx = Begin(function, bypass);
        ctx.PushArg(arg1);
        ctx.PushArg(arg2);
        ctx.PushArg(arg3);
        ctx.PushArg(arg4);
        ctx.PushArg(arg5);
        ctx.PushArg(arg6);
        ctx.PushArg(arg7);
        ctx.PushArg(arg8);
        Run(ctx);
        return ctx.GetResultArg<TResult>();
    }

    public static void InvokeVoid<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TArg7, TArg8, TArg9>(IntPtr function, bool bypass, TArg1 arg1, TArg2 arg2, TArg3 arg3, TArg4 arg4, TArg5 arg5, TArg6 arg6, TArg7 arg7, TArg8 arg8, TArg9 arg9)
    {
        var ctx = Begin(function, bypass);
        ctx.PushArg(arg1);
        ctx.PushArg(arg2);
        ctx.PushArg(arg3);
        ctx.PushArg(arg4);
        ctx.PushArg(arg5);
        ctx.PushArg(arg6);
        ctx.PushArg(arg7);
        ctx.PushArg(arg8);
        ctx.PushArg(arg9);
        Run(ctx);
    }

    public static TResult Invoke<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TArg7, TArg8, TArg9, TResult>(IntPtr function, bool bypass, TArg1 arg1, TArg2 arg2, TArg3 arg3, TArg4 arg4, TArg5 arg5, TArg6 arg6, TArg7 arg7, TArg8 arg8, TArg9 arg9)
    {
        var ctx = Begin(function, bypass);
        ctx.PushArg(arg1);
        ctx.PushArg(arg2);
        ctx.PushArg(arg3);
        ctx.PushArg(arg4);
        ctx.PushArg(arg5);
        ctx.PushArg(arg6);
        ctx.PushArg(arg7);
        ctx.PushArg(arg8);
        ctx.PushArg(arg9);
        Run(ctx);
        return ctx.GetResultArg<TResult>();
    }

    public static void InvokeVoid<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TArg7, TArg8, TArg9, TArg10>(IntPtr function, bool bypass, TArg1 arg1, TArg2 arg2, TArg3 arg3, TArg4 arg4, TArg5 arg5, TArg6 arg6, TArg7 arg7, TArg8 arg8, TArg9 arg9, TArg10 arg10)
    {
        var ctx = Begin(function, bypass);
        ctx.PushArg(arg1);
        ctx.PushArg(arg2);
        ctx.PushArg(arg3);
        ctx.PushArg(arg4);
        ctx.PushArg(arg5);
        ctx.PushArg(arg6);
        ctx.PushArg(arg7);
        ctx.PushArg(arg8);
        ctx.PushArg(arg9);
        ctx.PushArg(arg10);
        Run(ctx);
    }

    public static TResult Invoke<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TArg7, TArg8, TArg9, TArg10, TResult>(IntPtr function, bool bypass, TArg1 arg1, TArg2 arg2, TArg3 arg3, TArg4 arg4, TArg5 arg5, TArg6 arg6, TArg7 arg7, TArg8 arg8, TArg9 arg9, TArg10 arg10)
    {
        var ctx = Begin(function, bypass);
        ctx.PushArg(arg1);
        ctx.PushArg(arg2);
        ctx.PushArg(arg3);
        ctx.PushArg(arg4);
        ctx.PushArg(arg5);
        ctx.PushArg(arg6);
        ctx.PushArg(arg7);
        ctx.PushArg(arg8);
        ctx.PushArg(arg9);
        ctx.PushArg(arg10);
        Run(ctx);
        return ctx.GetResultArg<TResult>();
    }
}
