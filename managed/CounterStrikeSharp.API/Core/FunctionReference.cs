/*
 *  This file is part of CounterStrikeSharp.
 *  CounterStrikeSharp is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  CounterStrikeSharp is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with CounterStrikeSharp.  If not, see <https://www.gnu.org/licenses/>. *
 */

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CounterStrikeSharp.API.Core
{
    /// <summary>
    /// Describes the lifetime of a function reference.
    /// </summary>
    public enum FunctionLifetime
    {
        /// <summary>Delegate will be removed after the first invocation.</summary>
        SingleUse,

        /// <summary>Delegate will remain in memory for the lifetime of the application (or until <see cref="FunctionReference.Remove"/> is called).</summary>
        Permanent
    }

    /// <summary>
    /// Represents a reference to a function that can be called from native code.
    /// </summary>
    public class FunctionReference
    {
        public unsafe delegate void CallbackDelegate(fxScriptContext* context);

        private static readonly ConcurrentDictionary<int, FunctionReference> IdToFunctionReferencesMap = new();
        private static readonly ConcurrentDictionary<Delegate, FunctionReference> TargetMethodToFunctionReferencesMap = new();

        // Roots the native ABI delegates for the process lifetime so that a function
        // pointer handed to native code can NEVER be invoked on a garbage-collected
        // delegate. That use-after-free is what produces the hard crash:
        //   "A callback was made on a garbage collected delegate of type
        //    ...FunctionReference+CallbackDelegate::Invoke"
        // It happens when a plugin is unloaded/reloaded (or throws in OnLoad after
        // registering hooks/commands/timers, e.g. a MySQL plugin that fails to
        // connect) while native code still holds the raw pointer. Each stub captures
        // only an int id (NOT the plugin target), so rooting it does not pin the
        // plugin's AssemblyLoadContext.
        private static readonly ConcurrentDictionary<int, CallbackDelegate> NativeCallbackKeepAlive = new();

        // id -> owning plugin name, retained AFTER the reference is removed. Lets a
        // native call into an already-removed id (plugin unloaded/reloaded, or a
        // single-use callback already fired) be blamed on the right plugin instead of
        // being an anonymous no-op. Bounded so high-volume single-use traffic cannot
        // grow it without limit; DeadCallbackLogged throttles to one log line per id.
        private static readonly ConcurrentDictionary<int, string> RemovedOwners = new();
        private static readonly ConcurrentDictionary<int, byte> DeadCallbackLogged = new();
        // owner -> reported once already (atomic single-fire gate). A broken plugin keeps
        // hitting dead ids across map loads forever; the load banner is re-pasted exactly
        // once at runtime (the original printed at load time), then we go silent.
        private static readonly ConcurrentDictionary<string, byte> DeadCallbackOwnerReported = new();
        // owner -> the one-time "further occurrences suppressed" note has been printed.
        private static readonly ConcurrentDictionary<string, byte> DeadCallbackOwnerSuppressNoted = new();
        // owner assembly name -> the rendered PLUGIN LOAD FAILURE banner that left its
        // callbacks dangling. Set by PluginContext when a plugin fails to load. The runtime
        // dead-callback path re-pastes this exact banner instead of a useless "see above".
        private static readonly ConcurrentDictionary<string, string> LoadFailureBanners = new();
        // owner assembly name -> the plugin's friendly ModuleName (e.g. "[CS2] [ jRandomSkills ]").
        // Dead callbacks only know the assembly name; this lets the report show the same
        // name the operator saw in the load-failure banner so they connect the two.
        private static readonly ConcurrentDictionary<string, string> LoadFailureDisplayNames = new();
        private const int RemovedOwnersCap = 4096;

        // Records the rendered load-failure banner, keyed by assembly name (matches the
        // owner attribution used for dead callbacks). Called from PluginContext.
        public static void RecordLoadFailure(string ownerAssembly, string moduleName, string banner)
        {
            if (string.IsNullOrEmpty(ownerAssembly) || string.IsNullOrEmpty(banner)) return;
            LoadFailureBanners[ownerAssembly] = banner;
            if (!string.IsNullOrEmpty(moduleName) && moduleName != ownerAssembly)
                LoadFailureDisplayNames[ownerAssembly] = moduleName;
        }

        // (Solution A) Bounded retention window for single-use stubs that already fired
        // normally. A legit native path (NextFrame/timer) calls the pointer exactly
        // once, so reclaiming right after the fire is normally safe. But a buggy/racing
        // native re-call AFTER reclaim would hit a garbage-collected delegate -> fatal
        // crash. So instead of reclaiming immediately we keep the last N fired stubs
        // ROOTED; a late re-call within the window is a safe defused no-op. Only when
        // the window overflows do we reclaim the OLDEST (least likely to be re-called).
        // Bounds memory while making the collected-delegate crash effectively
        // unreachable for the common paths.
        private static readonly System.Collections.Concurrent.ConcurrentQueue<int> FiredStubWindow = new();
        private static int _firedStubWindowCount;
        private const int FiredStubWindowCap = 2048;

        private static readonly object ReferenceCounterLock = new();
        private static int _referenceCounter;

        private readonly Delegate _targetMethod;
        private CallbackDelegate _nativeCallback;

        // Compiled (target, args[]) -> result invoker, cached by MethodInfo. Replaces
        // Delegate.DynamicInvoke on the per-tick / per-event dispatch path, which is
        // ~50-100x slower than a direct compiled call. Keyed by MethodInfo (NOT per
        // FunctionReference) so single-use callbacks (NextFrame/timer create a fresh
        // reference every frame) reuse one compiled invoker instead of compiling each time.
        // A null entry means "this method shape is unsupported (multicast/ref/compile
        // failure) -> fall back to DynamicInvoke" and is cached so we never retry the compile.
        private static readonly ConcurrentDictionary<MethodInfo, Func<object, object[], object>?> InvokerCache = new();

        private readonly Func<object, object[], object>? _invoker;
        private readonly object? _invokerTarget;

        // Returns a compiled invoker for the delegate's method, or null if the shape is not
        // safely compilable (multicast delegates must call every target; ref/out/pointer
        // params can't be marshalled through object[]). Callers fall back to DynamicInvoke.
        internal static Func<object, object[], object>? GetCompiledInvoker(Delegate method)
        {
            if (method.GetInvocationList().Length > 1) return null;
            return InvokerCache.GetOrAdd(method.Method, BuildInvokerForMethod);
        }

        private static Func<object, object[], object>? BuildInvokerForMethod(MethodInfo mi)
        {
            try
            {
                var targetParam = Expression.Parameter(typeof(object), "target");
                var argsParam = Expression.Parameter(typeof(object[]), "args");

                var ps = mi.GetParameters();
                var callArgs = new Expression[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    // (Tn)args[i] — unboxes value types, same conversion DynamicInvoke does.
                    callArgs[i] = Expression.Convert(
                        Expression.ArrayIndex(argsParam, Expression.Constant(i)), ps[i].ParameterType);
                }

                Expression? instance = mi.IsStatic ? null : Expression.Convert(targetParam, mi.DeclaringType!);
                Expression call = Expression.Call(instance, mi, callArgs);

                Expression body = mi.ReturnType == typeof(void)
                    ? Expression.Block(call, Expression.Constant(null, typeof(object)))
                    : Expression.Convert(call, typeof(object));

                return Expression.Lambda<Func<object, object[], object>>(body, targetParam, argsParam).Compile();
            }
            catch
            {
                // Unsupported shape (e.g. by-ref/pointer params). Cache null -> DynamicInvoke.
                return null;
            }
        }

        // Plugin assembly that owns the handler. Captured once at creation so a native
        // call into an already-removed reference can be blamed on the right plugin.
        private readonly string _ownerName;

        // Cached once instead of reflecting on every native dispatch (this runs
        // per-tick / per-event). GetParameters() allocates a fresh array each
        // call, so the old code allocated 3 arrays + a LINQ pipeline per tick.
        private readonly ParameterInfo[] _parameters;
        private readonly bool _isManualScriptContext;

        private readonly TaskCompletionSource _taskCompletionSource = new();

        private FunctionReference(Delegate method, FunctionLifetime lifetime)
        {
            Lifetime = lifetime;
            _targetMethod = method;
            _invoker = GetCompiledInvoker(method);
            _invokerTarget = method.Target;
            _ownerName = method.Method.DeclaringType?.Assembly.GetName().Name ?? "unknown";
            _parameters = method.Method.GetParameters();
            _isManualScriptContext =
                _parameters.Length > 0 && _parameters[0].ParameterType == typeof(ScriptContext);
        }

        /// <summary>
        /// <inheritdoc cref="FunctionLifetime"/>
        /// </summary>
        public FunctionLifetime Lifetime { get; }

        /// <summary>
        /// For <see cref="FunctionLifetime.SingleUse"/> function references, this task will complete when
        /// the function has finished invoking.
        /// </summary>
        public Task CompletionTask => _taskCompletionSource.Task;

        public int Identifier { get; private set; }

        // The delegate whose function pointer is given to native code. It captures
        // ONLY the int id and the static map (never the plugin target), so it can be
        // rooted forever without pinning the plugin. If the reference was removed
        // (plugin unloaded / single-use already consumed) the lookup misses and the
        // call is a safe no-op instead of a use-after-free crash or a call into a
        // dead AssemblyLoadContext.
        private static unsafe CallbackDelegate CreateNativeStub(int id)
        {
            return context =>
            {
                if (IdToFunctionReferencesMap.TryGetValue(id, out var self))
                {
                    self.Dispatch(context);
                }
                else
                {
                    LogDeadCallback(id);
                }
            };
        }

        // Native invoked a function pointer whose managed reference is gone. The rooted
        // stub turns what used to be a use-after-free crash
        //   "A callback was made on a garbage collected delegate ...CallbackDelegate::Invoke"
        // into this defused no-op; this names the owning plugin so the cause is obvious
        // instead of silent. Logged once per id to avoid per-tick spam.
        private static void LogDeadCallback(int id)
        {
            if (!DeadCallbackLogged.TryAdd(id, 0)) return;

            var owner = RemovedOwners.TryGetValue(id, out var name) ? name : "unknown";
            // Friendly name from the load-failure banner, if any, so the header names the
            // same plugin the operator saw before (assembly "jRandomSkills" == "[CS2] [ jRandomSkills ]").
            var display = LoadFailureDisplayNames.TryGetValue(owner, out var dn) ? owner + " (" + dn + ")" : owner;

            // Atomic single-fire gate per owner: TryAdd returns true exactly once even
            // under concurrent dead-callback dispatches, so the report can never print
            // twice for the same plugin (the previous count-based version could race).
            if (!DeadCallbackOwnerReported.TryAdd(owner, 0))
            {
                // Already reported this owner once. Print ONE suppression note, then silent.
                if (DeadCallbackOwnerSuppressNoted.TryAdd(owner, 0))
                    Application.Instance?.Logger?.LogError(
                        "Plugin '{Owner}' keeps calling dead callbacks; further occurrences suppressed. Remove/fix the plugin.",
                        display);
                return;
            }

            // Prefer re-pasting the plugin's actual load-failure banner: an operator who
            // missed it (scrolled off, joined late) gets the full, actionable report again
            // instead of a "see above" pointer to something they cannot see. Fires once.
            if (LoadFailureBanners.TryGetValue(owner, out var banner))
            {
                Application.Instance?.Logger?.LogError(
                    "Plugin '{Owner}' is still calling native callbacks after a failed load (defused no-op). Re-showing its load failure:\n{Report}",
                    display, banner);
                return;
            }

            // No stored banner: the plugin unloaded without a recorded load failure (e.g.
            // a clean unload that left a hook). One concise line is enough.
            Application.Instance?.Logger?.LogError(
                "Plugin '{Owner}' called a dead callback after unload (defused no-op). It left a hook/timer/NextFrame registered; remove/fix the plugin.",
                display);
        }

        private unsafe void Dispatch(fxScriptContext* context)
        {
            {
                try
                {
                    var scriptContext = new ScriptContext(context);

                    // Allow for manual handling of the script context
                    if (_isManualScriptContext)
                    {
                        object? returnValue;
                        // Fast path: every plugin listener wraps to this exact delegate type
                        // (see BasePlugin.RegisterListener), so a direct typed call avoids
                        // both DynamicInvoke and the 1-element object[] the invoker needs.
                        if (_targetMethod is Func<ScriptContext, HookResult> typedHandler)
                        {
                            returnValue = typedHandler(scriptContext);
                        }
                        else if (_invoker != null)
                        {
                            returnValue = _invoker(_invokerTarget!, new object[] { scriptContext });
                        }
                        else
                        {
                            returnValue = _targetMethod.DynamicInvoke(scriptContext);
                        }

                        if (returnValue != null)
                        {
                            scriptContext.SetResult(returnValue, context);
                        }

                        return;
                    }

                    // Hot path: game-event / typed callbacks land here per-event, and at high
                    // player counts that is thousands of dispatches/sec. The compiled invoker
                    // only reads indices [0.._parameters.Length), so a pooled (possibly larger)
                    // buffer is safe and removes the per-dispatch Gen0 array allocation.
                    //
                    // DynamicInvoke is excluded: it requires an EXACT-length argument array and
                    // throws on an oversized one. It is already the rare fallback for shapes the
                    // compiled invoker cannot handle, so keeping its exact alloc costs nothing hot.
                    object returnObj;
                    if (_invoker != null)
                    {
                        var parameterList = ArrayPool<object>.Shared.Rent(_parameters.Length);
                        try
                        {
                            for (int i = 0; i < _parameters.Length; i++)
                            {
                                parameterList[i] = scriptContext.GetArgument(_parameters[i].ParameterType, i);
                            }

                            // Compiled invoker indexes only [0.._parameters.Length); an
                            // oversized pooled buffer is therefore safe.
                            returnObj = _invoker(_invokerTarget!, parameterList);
                        }
                        finally
                        {
                            // clearArray:true so pooled slots don't root GameEvent wrappers /
                            // captured objects until the next rent overwrites them.
                            ArrayPool<object>.Shared.Return(parameterList, clearArray: true);
                        }
                    }
                    else
                    {
                        var parameterList = new object[_parameters.Length];
                        for (int i = 0; i < _parameters.Length; i++)
                        {
                            parameterList[i] = scriptContext.GetArgument(_parameters[i].ParameterType, i);
                        }

                        returnObj = _targetMethod.DynamicInvoke(parameterList);
                    }

                    if (returnObj != null)
                    {
                        scriptContext.SetResult(returnObj, context);
                    }
                }
                catch (Exception e)
                {
                    if ((e.InnerException ?? e) is Plugin.PluginTerminationException pluginEx)
                    {
                        return;
                    }

                    var owner = _targetMethod.Method.DeclaringType?.Assembly.GetName().Name ?? "unknown";
                    var throttleKey = owner + "|" + _targetMethod.Method.Name + "|" + e.GetBaseException().GetType().Name;
                    var decision = Diagnostics.PluginDiagnostics.RecordError(owner, throttleKey);

                    if (decision.LogFull)
                    {
                        Application.Instance.Logger.LogError(e, "Error invoking callback");
                        Application.Instance.Logger.LogError("\n{Report}", BuildCallbackErrorReport(e, _targetMethod));
                    }
                    else if (decision.LogSuppressionNotice)
                    {
                        Application.Instance.Logger.LogError(
                            "Plugin '{Owner}' handler '{Handler}' still throwing {Error} ({Count}x total). Report suppressed; see earlier blame report.",
                            owner, _targetMethod.Method.Name, e.GetBaseException().GetType().Name, decision.TimesSeen);
                    }
                }
                finally
                {
                    if (Lifetime == FunctionLifetime.SingleUse)
                    {
                        RemoveSelf();
                    }

                    _taskCompletionSource.TrySetResult();
                }
            }
        }

        // Names the plugin that owns the crashing handler so a runtime exception in
        // third-party plugin code isn't mistaken for a CounterStrikeSharp bug. The
        // server keeps running (this exception was caught); the report just makes the
        // culprit obvious in the log.
        private static string BuildCallbackErrorReport(Exception ex, Delegate target)
        {
            var root = ex.GetBaseException();
            var handler = target.Method;
            var owner = handler.DeclaringType?.Assembly.GetName().Name ?? "unknown";

            // Deepest frame inside the owning plugin assembly -> file:line of the bug.
            string loc = null;
            var pluginAsm = handler.DeclaringType?.Assembly;
            foreach (var f in new StackTrace(root, true).GetFrames() ?? Array.Empty<StackFrame>())
            {
                var m = f.GetMethod();
                if (m?.DeclaringType?.Assembly != pluginAsm) continue;
                var file = f.GetFileName();
                loc = m.DeclaringType.FullName + "." + m.Name + "()"
                    + (file != null ? " at " + file + ":" + f.GetFileLineNumber() : "");
                break;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("==================== PLUGIN RUNTIME ERROR ====================");
            sb.AppendLine("Plugin:    " + owner);
            sb.AppendLine("Handler:   " + (handler.DeclaringType?.FullName + "." + handler.Name + "()"));
            sb.AppendLine("Error:     " + root.GetType().Name + ": " + root.Message);
            if (loc != null)
                sb.AppendLine("Location:  " + loc);
            sb.AppendLine("Blame:     Plugin '" + owner + "' — this exception came from inside its own handler.");
            sb.AppendLine("           The server kept running, but that plugin's action did not complete.");
            sb.AppendLine("Action:    Report this trace to the plugin author; disable the plugin if it spams.");
            sb.Append("=============================================================");
            return sb.ToString();
        }

        public static FunctionReference Create(Delegate method, FunctionLifetime lifetime = FunctionLifetime.Permanent)
        {
            // We always want to create a new reference if the lifetime is single use.
            if (lifetime == FunctionLifetime.Permanent && TargetMethodToFunctionReferencesMap.TryGetValue(method, out var existingReference))
            {
                return existingReference;
            }

            var reference = new FunctionReference(method, lifetime);
            Register(reference);

            return reference;
        }


        private static void Register(FunctionReference reference)
        {
            lock (ReferenceCounterLock)
            {
                var thisRefId = _referenceCounter;
                reference.Identifier = thisRefId;

                // Build the native stub now that the id is known and root it so the
                // function pointer stays valid for native code regardless of when the
                // managed reference is removed.
                var stub = CreateNativeStub(thisRefId);
                reference._nativeCallback = stub;
                NativeCallbackKeepAlive[thisRefId] = stub;

                IdToFunctionReferencesMap[thisRefId] = reference;
                TargetMethodToFunctionReferencesMap[reference._targetMethod] = reference;

                unchecked
                {
                    _referenceCounter++;
                }
            }
        }

        public IntPtr GetFunctionPointer() => Marshal.GetFunctionPointerForDelegate(_nativeCallback);

        private void RemoveSelf()
        {
            // firedNormally: the single-use callback just ran to completion inside
            // Dispatch, so native has already consumed the pointer and will not call it
            // again -> safe to reclaim the rooted stub.
            Remove(Identifier, firedNormally: true);
        }

        public static void Remove(int reference) => Remove(reference, firedNormally: false);

        // (Solution A) Push a just-fired single-use stub into the bounded retention
        // window, evicting (reclaiming) the oldest if the window is full.
        private static void RetainFiredStub(int reference)
        {
            FiredStubWindow.Enqueue(reference);
            var count = System.Threading.Interlocked.Increment(ref _firedStubWindowCount);

            while (count > FiredStubWindowCap && FiredStubWindow.TryDequeue(out var oldest))
            {
                count = System.Threading.Interlocked.Decrement(ref _firedStubWindowCount);
                NativeCallbackKeepAlive.TryRemove(oldest, out _);
            }
        }

        private static void Remove(int reference, bool firedNormally)
        {
            if (IdToFunctionReferencesMap.TryRemove(reference, out var functionReference))
            {
                TargetMethodToFunctionReferencesMap.TryRemove(functionReference._targetMethod, out _);

                // Retain owner attribution so a later native call into this now-removed
                // id is blamed on the right plugin (see LogDeadCallback) instead of being
                // an anonymous no-op. Bounded to stop high-volume single-use traffic
                // growing it without limit.
                if (RemovedOwners.Count < RemovedOwnersCap)
                    RemovedOwners[reference] = functionReference._ownerName;

                // The stub may ONLY be reclaimed when we know native will never call the
                // pointer again. That is true only for a single-use callback that already
                // fired (firedNormally) -> RemoveSelf. Reclaiming it then keeps NextFrame/
                // timer volume from leaking one delegate per call.
                //
                // Any OTHER removal (plugin unload/reload, BasePlugin cleanup, single-use
                // removed BEFORE it fired) must KEEP the stub rooted: native may still hold
                // the pointer and call it later. This is the load-failure crash: a plugin
                // schedules NextFrame/timer in OnLoad, OnLoad throws, Unload removes the
                // reference, and next frame native calls the (now garbage-collected) stub:
                //   "A callback was made on a garbage collected delegate ...CallbackDelegate::Invoke"
                // -> hard "Process terminated". Keeping the stub rooted turns that fatal
                // crash into LogDeadCallback's named, defused no-op instead.
                if (firedNormally && functionReference.Lifetime == FunctionLifetime.SingleUse)
                {
                    // (Solution A) Don't reclaim immediately; keep this fired stub rooted
                    // inside a bounded window so a late native re-call is a safe no-op
                    // instead of a garbage-collected-delegate crash. Reclaim only the
                    // oldest stub once the window overflows.
                    RetainFiredStub(reference);
                }

                Application.Instance.Logger.LogDebug("Removing function/callback reference: {Reference}", reference);
            }
        }
    }
}
