using System;
using System.Reflection;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;

public abstract class BaseMemoryFunction : NativeObject
{
    private static Dictionary<string, IntPtr> _createdFunctions = new();

    internal static Dictionary<string, IntPtr> _createdOffsetFunctions = new();

    // Active dynamic-function hooks, tracked so a plugin unload can remove the ones a
    // plugin forgot to Unhook. Memory functions (e.g. VirtualFunctions.*) are static and
    // shared across all plugins, so neither the function object nor BasePlugin tracks the
    // hook. Without this the handler delegate stays rooted in FunctionReference's maps and
    // pins the plugin's AssemblyLoadContext permanently — one dead ALC per reload. Cleanup
    // is therefore keyed by the handler's owning assembly.
    private readonly record struct HookRegistration(IntPtr Handle, Func<DynamicHook, HookResult> Handler, bool Post, int ReferenceId);

    private static readonly List<HookRegistration> _activeHooks = new();
    private static readonly object _hooksLock = new();

    private static IntPtr CreateValveFunctionBySignature(string signature, DataType returnType,
        DataType[] argumentTypes)
    {
        if (!_createdFunctions.TryGetValue(signature, out var function))
        {
            try
            {
                function = NativeAPI.CreateVirtualFunctionBySignature(IntPtr.Zero, Addresses.ServerPath, signature,
                    argumentTypes.Length, (int)returnType, argumentTypes.Cast<object>().ToArray());
                _createdFunctions[signature] = function;
            }
            catch (Exception ex)
            {
                // Don't swallow silently: a failed resolution leaves `function` at
                // IntPtr.Zero, and invoking that later jumps to address 0 and crashes
                // with no clue why. Log so the bad signature is diagnosable.
                Application.Instance.Logger.LogError(ex,
                    "Failed to resolve native function for signature \"{Signature}\"", signature);
            }
        }

        return function;
    }

    private static IntPtr CreateValveFunctionBySignature(string signature, string binarypath, DataType returnType,
        DataType[] argumentTypes)
    {
        if (!_createdFunctions.TryGetValue(signature, out var function))
        {
            try
            {
                function = NativeAPI.CreateVirtualFunctionBySignature(IntPtr.Zero, binarypath, signature,
                    argumentTypes.Length, (int)returnType, argumentTypes.Cast<object>().ToArray());
                _createdFunctions[signature] = function;
            }
            catch (Exception ex)
            {
                Application.Instance.Logger.LogError(ex,
                    "Failed to resolve native function for signature \"{Signature}\" in {Binary}", signature, binarypath);
            }
        }

        return function;
    }

    private static IntPtr CreateValveFunctionByOffset(string symbolName, int offset, DataType returnType,
        DataType[] argumentTypes, Func<nint> nativeCaller)
    {
        string constructKey = $"{symbolName}_{offset}";

        if (!_createdOffsetFunctions.TryGetValue(constructKey, out var function))
        {
            try
            {
                function = nativeCaller();
                _createdOffsetFunctions[constructKey] = function;
            }
            catch (Exception ex)
            {
                Application.Instance.Logger.LogError(ex,
                    "Failed to resolve native function by offset \"{Key}\"", constructKey);
            }
        }

        return function;
    }

    private static IntPtr CreateValveFunctionByOffset(IntPtr objectPtr, string symbolName, int offset, DataType returnType,
        DataType[] argumentTypes)
    {
        return CreateValveFunctionByOffset(symbolName, offset, returnType, argumentTypes, () =>
        {
            return NativeAPI.CreateVirtualFunction(objectPtr, offset, argumentTypes.Length,
                (int)returnType, argumentTypes.Cast<object>().ToArray());
        });
    }

    private static IntPtr CreateValveFunctionBySymbol(string symbolName, string binaryPath, int offset, DataType returnType,
        DataType[] argumentTypes)
    {
        return CreateValveFunctionByOffset(symbolName, offset, returnType, argumentTypes, () =>
        {
            return NativeAPI.CreateVirtualFunctionBySymbol(binaryPath, symbolName, offset, argumentTypes.Length,
                (int)returnType, argumentTypes.Cast<object>().ToArray());
        });
    }

    private static IntPtr CreateValveFunctionFromVTable(string symbolName, IntPtr vtable, int offset, DataType returnType,
        DataType[] argumentTypes)
    {
        return CreateValveFunctionByOffset(symbolName, offset, returnType, argumentTypes, () =>
        {
            return NativeAPI.CreateVirtualFunctionFromVTable(vtable, offset, argumentTypes.Length,
                (int)returnType, argumentTypes.Cast<object>().ToArray());
        });
    }

    public BaseMemoryFunction(string signature, DataType returnType, DataType[] parameters) : base(
        CreateValveFunctionBySignature(signature, returnType, parameters))
    {
    }

    public BaseMemoryFunction(string signature, string binarypath, DataType returnType, DataType[] parameters) : base(
        CreateValveFunctionBySignature(signature, binarypath, returnType, parameters))
    {
    }

    /// <summary>
    /// <b>WARNING:</b> this is only supposed to be used with <see cref="VirtualFunctionVoid{TArg1}"/> and <see cref="VirtualFunctionWithReturn{TArg1, TResult}"/> variants.
    /// </summary>
    internal BaseMemoryFunction(IntPtr objectPtr, string symbolName, int offset, DataType returnType, DataType[] parameters) : base(
        CreateValveFunctionByOffset(objectPtr, symbolName, offset, returnType, parameters))
    {
    }

    /// <summary>
    /// <b>WARNING:</b> this is only supposed to be used with <see cref="VirtualFunctionVoid{TArg1}"/> and <see cref="VirtualFunctionWithReturn{TArg1, TResult}"/> variants.
    /// </summary>
    internal BaseMemoryFunction(string symbolName, string binaryPath, int offset, DataType returnType, DataType[] parameters) : base(
        CreateValveFunctionBySymbol(symbolName, binaryPath, offset, returnType, parameters))
    {
    }

    /// <summary>
    /// <b>WARNING:</b> this is only supposed to be used with <see cref="VirtualFunctionVoid{TArg1}"/> and <see cref="VirtualFunctionWithReturn{TArg1, TResult}"/> variants.
    /// </summary>
    internal BaseMemoryFunction(string symbolName, int offset, DataType returnType, DataType[] parameters) : base(
        CreateValveFunctionBySymbol(symbolName, Addresses.ServerPath, offset, returnType, parameters))
    {
    }

    /// <summary>
    /// <b>WARNING:</b> this is only supposed to be used with <see cref="VirtualFunctionVoid{TArg1}"/> and <see cref="VirtualFunctionWithReturn{TArg1, TResult}"/> variants.
    /// </summary>
    internal BaseMemoryFunction(string symbolName, IntPtr vtable, int offset, DataType returnType, DataType[] parameters) : base(
        CreateValveFunctionFromVTable(symbolName, vtable, offset, returnType, parameters))
    {
    }

    public void Hook(Func<DynamicHook, HookResult> handler, HookMode mode)
    {
        bool post = mode == HookMode.Post;

        // Create the reference explicitly so we own its identifier for later cleanup.
        // (NativeAPI.HookFunction would otherwise create the same reference implicitly.)
        var reference = FunctionReference.Create(handler);
        NativeAPI.HookFunction(Handle, reference, post);

        lock (_hooksLock)
            _activeHooks.Add(new HookRegistration(Handle, handler, post, reference.Identifier));
    }

    public void Unhook(Func<DynamicHook, HookResult> handler, HookMode mode)
    {
        bool post = mode == HookMode.Post;

        NativeAPI.UnhookFunction(Handle, handler, post);

        int referenceId;
        lock (_hooksLock)
        {
            int idx = _activeHooks.FindIndex(h => h.Handle == Handle && h.Handler == handler && h.Post == post);
            if (idx < 0) return;

            referenceId = _activeHooks[idx].ReferenceId;
            _activeHooks.RemoveAt(idx);
        }

        // Drop the managed reference so the handler delegate (and the plugin ALC it
        // captures) is no longer rooted. Previously Unhook left it pinned forever.
        FunctionReference.Remove(referenceId);
    }

    /// <summary>
    /// Removes every dynamic-function hook installed by handlers belonging to
    /// <paramref name="assembly"/>. Called on plugin unload so hooks the plugin did not
    /// explicitly <see cref="Unhook"/> do not keep its AssemblyLoadContext alive.
    /// </summary>
    internal static void RemoveHooksForAssembly(Assembly assembly)
    {
        List<HookRegistration> toRemove;
        lock (_hooksLock)
        {
            toRemove = _activeHooks.Where(h => h.Handler.Method.DeclaringType?.Assembly == assembly).ToList();
            if (toRemove.Count == 0) return;

            _activeHooks.RemoveAll(h => h.Handler.Method.DeclaringType?.Assembly == assembly);
        }

        foreach (var h in toRemove)
        {
            // The target function (server binary) is still loaded at unload time, so the
            // native unhook is safe. Guard anyway: even if it fails we must still drop the
            // managed reference, which is what actually releases the ALC.
            try { NativeAPI.UnhookFunction(h.Handle, h.Handler, h.Post); }
            catch { /* ignore — still remove the managed reference below */ }

            FunctionReference.Remove(h.ReferenceId);
        }
    }

    protected T InvokeInternal<T>(bool bypass, params object[] args)
    {
        return NativeAPI.ExecuteVirtualFunction<T>(Handle, bypass, args);
    }

    protected void InvokeInternalVoid(bool bypass, params object[] args)
    {
        NativeAPI.ExecuteVirtualFunction<object>(Handle, bypass, args);
    }
}