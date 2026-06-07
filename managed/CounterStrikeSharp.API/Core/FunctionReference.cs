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

using System.Collections.Concurrent;
using System.Diagnostics;
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

        private static readonly object ReferenceCounterLock = new();
        private static int _referenceCounter;

        private readonly Delegate _targetMethod;
        private readonly CallbackDelegate _nativeCallback;

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
            _parameters = method.Method.GetParameters();
            _isManualScriptContext =
                _parameters.Length > 0 && _parameters[0].ParameterType == typeof(ScriptContext);
            _nativeCallback = CreateWrappedCallback();
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

        private unsafe CallbackDelegate CreateWrappedCallback()
        {
            return context =>
            {
                try
                {
                    var scriptContext = new ScriptContext(context);

                    // Allow for manual handling of the script context
                    if (_isManualScriptContext)
                    {
                        var returnValue = _targetMethod.DynamicInvoke(scriptContext);
                        if (returnValue != null)
                        {
                            scriptContext.SetResult(returnValue, context);
                        }

                        return;
                    }

                    var parameterList = new object[_parameters.Length];
                    for (int i = 0; i < _parameters.Length; i++)
                    {
                        parameterList[i] = scriptContext.GetArgument(_parameters[i].ParameterType, i);
                    }

                    var returnObj = _targetMethod.DynamicInvoke(parameterList);

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
            };
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
            sb.AppendLine("Blame:     The plugin '" + owner + "', NOT CounterStrikeSharp.");
            sb.AppendLine("           This exception came from inside the plugin's own handler. The server");
            sb.AppendLine("           kept running, but that plugin's action did not complete.");
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
            var referenceId = Register(reference);

            reference.Identifier = referenceId;

            return reference;
        }


        private static int Register(FunctionReference reference)
        {
            lock (ReferenceCounterLock)
            {
                var thisRefId = _referenceCounter;
                IdToFunctionReferencesMap[thisRefId] = reference;
                TargetMethodToFunctionReferencesMap[reference._targetMethod] = reference;

                unchecked
                {
                    _referenceCounter++;
                }

                return thisRefId;
            }
        }

        public IntPtr GetFunctionPointer() => Marshal.GetFunctionPointerForDelegate(_nativeCallback);

        private void RemoveSelf()
        {
            Remove(Identifier);
        }

        public static void Remove(int reference)
        {
            if (IdToFunctionReferencesMap.TryGetValue(reference, out var functionReference))
            {
                if (TargetMethodToFunctionReferencesMap.ContainsKey(functionReference._targetMethod))
                {
                    TargetMethodToFunctionReferencesMap.Remove(functionReference._targetMethod, out _);
                }

                IdToFunctionReferencesMap.Remove(reference, out _);

                Application.Instance.Logger.LogDebug("Removing function/callback reference: {Reference}", reference);
            }
        }
    }
}
