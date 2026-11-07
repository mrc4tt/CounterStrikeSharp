using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace CounterStrikeSharp.API.Core.Plugin;

/// <summary>
/// Compiles a freshly loaded plugin's methods ahead of their first call (CoreConfig.PluginPreJitEnabled).
/// </summary>
/// <remarks>
/// Without this, every plugin method is JIT-compiled the first time it runs: a handler's first event, a
/// command's first use, the first round of a mode. That lands in the middle of a game frame, and a
/// plugin's first pass through its code costs several milliseconds of compilation on top of the work
/// (the swiftly-solution/profiler "cold" rows were mostly this).
///
/// Runs on the game thread, in slices of <see cref="BudgetMs"/> per frame, continued with NextFrame.
/// Not on a worker thread: the JIT may run beforefieldinit static constructors while it compiles, and
/// plugin static initializers call natives (ConVar.Find, GameData.GetSignature, ...) that must run on
/// the game thread. Tiered compilation later re-optimises hot methods on its own background thread, as
/// it does for methods compiled on first call.
/// </remarks>
internal sealed class PluginPreJit
{
    private const double BudgetMs = 0.5;

    private readonly ILogger _logger;
    private readonly string _name;
    private IEnumerator<MethodBase>? _methods;
    private readonly Stopwatch _total = new();
    private int _prepared;
    private int _failed;
    private int _frames;

    private PluginPreJit(Assembly assembly, ILogger logger, string name)
    {
        _logger = logger;
        _name = name;
        _methods = EnumerateMethods(assembly).GetEnumerator();
    }

    public static PluginPreJit Start(Assembly assembly, ILogger logger, string name)
    {
        var preJit = new PluginPreJit(assembly, logger, name);
        Server.NextFrame(preJit.Step);
        return preJit;
    }

    /// <summary>Stops at the next slice and drops the references into the plugin's assembly.</summary>
    public void Cancel()
    {
        _methods?.Dispose();
        _methods = null;
    }

    private void Step()
    {
        var methods = _methods;
        if (methods == null) return;

        _frames++;
        _total.Start();
        var slice = Stopwatch.StartNew();
        var done = false;
        try
        {
            while (slice.Elapsed.TotalMilliseconds < BudgetMs)
            {
                if (!methods.MoveNext())
                {
                    done = true;
                    break;
                }

                try
                {
                    RuntimeHelpers.PrepareMethod(methods.Current.MethodHandle);
                    _prepared++;
                }
                catch (Exception)
                {
                    // A method whose signature references a type that does not load, an extern
                    // without a body, ... The JIT would fail the same way on first call; skip it.
                    _failed++;
                }
            }
        }
        catch (Exception ex)
        {
            // Enumerating the assembly itself failed (unloadable dependency). Nothing left to do.
            _logger.LogDebug(ex, "Pre-JIT of {Name} stopped", _name);
            done = true;
        }
        finally
        {
            _total.Stop();
        }

        if (done)
        {
            Cancel();
            _logger.LogDebug("Pre-JIT of {Name}: {Prepared} methods in {Ms:F1} ms over {Frames} frames ({Failed} skipped)",
                _name, _prepared, _total.Elapsed.TotalMilliseconds, _frames, _failed);
            return;
        }

        if (_methods != null) Server.NextFrame(Step);
    }

    private static IEnumerable<MethodBase> EnumerateMethods(Assembly assembly)
    {
        const BindingFlags Flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.Static;

        foreach (var type in GetLoadableTypes(assembly))
        {
            // Open generic types and methods have no code until instantiated.
            if (type.ContainsGenericParameters || type.IsInterface) continue;

            MethodInfo[] methods;
            ConstructorInfo[] constructors;
            try
            {
                methods = type.GetMethods(Flags);
                constructors = type.GetConstructors(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic |
                                                    BindingFlags.Instance);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var method in methods)
            {
                if (method.IsAbstract || method.ContainsGenericParameters || !HasBody(method)) continue;
                yield return method;
            }

            // Instance constructors only: a static constructor runs when the runtime decides, not here.
            foreach (var constructor in constructors)
            {
                if (HasBody(constructor)) yield return constructor;
            }
        }
    }

    private static bool HasBody(MethodBase method)
    {
        if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0) return false;
        var impl = method.MethodImplementationFlags;
        return (impl & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime)) == 0;
    }

    // Same tolerance as PluginContext's domain-wide scans: types whose dependencies don't resolve are skipped.
    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null)!;
        }
    }
}
