using System;
using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace CounterStrikeSharp.API.Modules.Memory;

// Resolves signature-scanned native functions once per (binary, signature, return type, argument types).
//
// The key must include the types: the native ValveFunction fixes its argument list at creation, and
// bindings like CBasePlayerController_SetPawnFunc (4 args) / SetPawnFullFunc (6 args) or the
// TerminateRound Linux/Windows variants share one signature. Keyed by signature alone, whichever
// resolved first won and later callers passed the wrong argument count/order to the game.
//
// Failures are cached too. A stale signature makes the native side scan the whole binary (~120 ms)
// and throw; without a negative entry every later invoke of that binding repeated the scan on the
// game thread. The signature string is part of the key, so a gamedata change still re-resolves.
internal static class NativeFunctionCache
{
    private static readonly Dictionary<string, IntPtr> Resolved = new();
    private static readonly HashSet<string> Failed = new();
    private static readonly object Lock = new();

    public static IntPtr BySignature(string binaryPath, string signature, DataType returnType, DataType[] argumentTypes)
    {
        var key = $"{binaryPath}\n{signature}\n{(int)returnType}\n{string.Join(",", argumentTypes.Select(t => (int)t))}";

        lock (Lock)
        {
            if (Resolved.TryGetValue(key, out var cached)) return cached;
            if (Failed.Contains(key)) return IntPtr.Zero;

            try
            {
                var function = NativeAPI.CreateVirtualFunctionBySignature(IntPtr.Zero, binaryPath, signature,
                    argumentTypes.Length, (int)returnType, argumentTypes.Cast<object>().ToArray());
                Resolved[key] = function;
                return function;
            }
            catch (Exception ex)
            {
                // Logged once per key. The binding then holds IntPtr.Zero, and invoking or hooking it
                // fails fast with "Invalid function pointer" instead of rescanning.
                Failed.Add(key);
                Application.Instance.Logger.LogError(ex,
                    "Failed to resolve native function for signature \"{Signature}\" in {Binary}",
                    SignatureFormat.ToIdaStyle(signature), binaryPath);
                return IntPtr.Zero;
            }
        }
    }
}
