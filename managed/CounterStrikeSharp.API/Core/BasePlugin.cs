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

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Core.Commands;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Config;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.UserMessages;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace CounterStrikeSharp.API.Core
{
    public abstract class BasePlugin : IPlugin
    {
        private bool _disposed;

        public BasePlugin()
        {
            RegisterListener<Listeners.OnMapEnd>(() =>
            {
                foreach (var callback in EntitySingleOutputHooks.Values.SelectMany(list => list).ToList())
                {
                    UnhookEntityOutput(callback.Classname, callback.Output, callback.Handler);
                }

                EntitySingleOutputHooks.Clear();
            });
        }

        public abstract string ModuleName { get; }
        public abstract string ModuleVersion { get; }

        public virtual string ModuleAuthor { get; } = "";

        public virtual string ModuleDescription { get; } = "";

        // Set by the host right after construction.
        public string ModulePath { get; set; } = "";

        public string ModuleDirectory => Path.GetDirectoryName(ModulePath)!;
        // Logger/CommandManager/Localizer/SelfControl are injected by the host
        // immediately after construction, so they are non-null in plugin code.
        public ILogger Logger { get; set; } = null!;

        public ICommandManager CommandManager { get; set; } = null!;

        public IStringLocalizer Localizer { get; set; } = null!;

        internal Plugin.ISelfPluginControl SelfControl { get; set; } = null!;

        public void TerminateSelf(string reason)
        {
            SelfControl?.TerminateSelf(reason);
        }

        public virtual void Load(bool hotReload)
        {
        }

        public virtual void Unload(bool hotReload)
        {
        }

        public virtual void OnAllPluginsLoaded(bool hotReload)
        {
        }

        public class CallbackSubscriber : IDisposable
        {
            private Delegate _underlyingMethod;
            private readonly int _functionReferenceIdentifier;

            private readonly InputArgument _inputArgument;
            private readonly Action _dispose;

            public CallbackSubscriber(Delegate underlyingMethod, Delegate wrapperMethod, Action dispose)
                : this(underlyingMethod, wrapperMethod, dispose, null, null)
            {
            }

            internal CallbackSubscriber(Delegate underlyingMethod, Delegate wrapperMethod, Action dispose, string? crashKind,
                string? crashDetail, bool crashActivity = true, bool uniqueReference = false)
            {
                _dispose = dispose;
                _underlyingMethod = underlyingMethod;

                // uniqueReference: the subscriber owns its reference outright. With the shared one
                // from Create(), one handler hooked twice (Pre and Post, or two message ids) got a
                // single reference, and removing either registration killed the other.
                var functionReference = uniqueReference
                    ? FunctionReference.CreateUnique(wrapperMethod, FunctionLifetime.Permanent)
                    : FunctionReference.Create(wrapperMethod);
                if (crashKind != null)
                    functionReference.SetCrashLabel(crashKind, crashDetail ?? crashKind, crashActivity,
                        FunctionReference.OwnerOf(underlyingMethod));
                _inputArgument = (InputArgument)functionReference;

                _functionReferenceIdentifier = functionReference.Identifier;
            }

            public InputArgument GetInputArgument()
            {
                return _inputArgument;
            }

            public int GetReferenceIdentifier()
            {
                return _functionReferenceIdentifier;
            }

            public void Dispose()
            {
                _dispose();
            }
        }

        /// <summary>
        /// Most recent subscriber per handler (game events and user messages). Kept for
        /// compatibility; a handler registered several times appears once. The full set lives in
        /// <see cref="_handlerRegistrations"/>.
        /// </summary>
        public readonly Dictionary<Delegate, CallbackSubscriber> Handlers =
            new Dictionary<Delegate, CallbackSubscriber>();

        // Every event / user-message registration, keyed by (kind, name or id, mode, handler).
        // Keying by the handler alone (Handlers) meant registering one handler for Pre and Post,
        // or for two message ids, overwrote the first: only one was unhooked on unload and the
        // other stayed hooked natively, calling into a removed reference.
        private sealed record HandlerRegistration(string Kind, string Key, Delegate Handler, bool Post,
            CallbackSubscriber Subscriber, Action Unhook);

        private readonly List<HandlerRegistration> _handlerRegistrations = new();

        private void AddHandlerRegistration(HandlerRegistration registration)
        {
            _handlerRegistrations.Add(registration);
            Handlers[registration.Handler] = registration.Subscriber;
        }

        // Removes every matching registration, as before: with the old shared reference native
        // unhooked all copies of a handler registered twice for the same event and mode.
        private void RemoveHandlerRegistration(string kind, string key, Delegate handler, bool post)
        {
            foreach (var registration in _handlerRegistrations
                         .Where(r => r.Kind == kind && r.Key == key && r.Post == post && r.Handler.Equals(handler))
                         .ToList())
            {
                RemoveHandlerRegistration(registration.Subscriber);
            }
        }

        private void RemoveHandlerRegistration(CallbackSubscriber subscriber)
        {
            var index = _handlerRegistrations.FindIndex(r => ReferenceEquals(r.Subscriber, subscriber));
            if (index < 0) return;

            var registration = _handlerRegistrations[index];
            _handlerRegistrations.RemoveAt(index);

            registration.Unhook();
            // Native has dropped the pointer, so the stub may go to the bounded window instead of
            // staying rooted for the life of the process (one per unregister).
            FunctionReference.RemoveReleased(subscriber.GetReferenceIdentifier());

            // Keep the compatibility map pointing at a live registration of this handler, if any is left.
            var remaining = _handlerRegistrations.FindLastIndex(r => r.Handler.Equals(registration.Handler));
            if (remaining >= 0)
                Handlers[registration.Handler] = _handlerRegistrations[remaining].Subscriber;
            else
                Handlers.Remove(registration.Handler);
        }

        /// <summary>
        /// Most recent subscriber per handler. Kept for compatibility; a handler registered for several
        /// commands appears here once. The full set lives in <see cref="_commandListenerRegistrations"/>.
        /// </summary>
        public readonly Dictionary<Delegate, CallbackSubscriber> CommandListeners =
            new Dictionary<Delegate, CallbackSubscriber>();

        // Every AddCommandListener call, so one handler registered for several commands (or modes) is
        // removed for each of them. Keying only by the handler lost all but the last registration, and
        // the rest stayed hooked natively after unload, still calling into the unloaded plugin.
        private readonly List<(string? Name, Delegate Handler, bool Post, CallbackSubscriber Subscriber)>
            _commandListenerRegistrations = new();

        /// <summary>
        /// Most recent subscriber per handler. Kept for compatibility; the full set lives in
        /// <see cref="_listenerRegistrations"/>.
        /// </summary>
        public readonly Dictionary<Delegate, CallbackSubscriber> Listeners =
            new Dictionary<Delegate, CallbackSubscriber>();

        // Every RegisterListener call. Keyed only by handler, registering one handler twice (e.g. again
        // in OnMapStart) overwrote the first subscriber: its native listener was never removed and kept
        // calling the plugin, and its reference rooted the plugin after unload.
        private readonly List<(string Name, Delegate Handler, CallbackSubscriber Subscriber)> _listenerRegistrations = new();

        /// <summary>
        /// Most recent subscriber per handler. Kept for compatibility; the full set lives in
        /// <see cref="_entityOutputRegistrations"/>.
        /// </summary>
        public readonly Dictionary<Delegate, CallbackSubscriber> EntityOutputHooks =
            new Dictionary<Delegate, CallbackSubscriber>();

        // Every HookEntityOutput call. Same problem as listeners: one handler hooked for two outputs (or
        // Pre and Post) lost the first registration, which stayed hooked natively after unload.
        private readonly List<(string Classname, string Output, HookMode Mode, Delegate Handler, CallbackSubscriber Subscriber)>
            _entityOutputRegistrations = new();

        // Per plugin handler, every single-entity hook made with it: the usual pattern hooks one handler
        // for many entities, and keeping only the last left the others hooked until the plugin unloaded.
        internal readonly Dictionary<Delegate, List<EntityIO.EntityOutputCallback>> EntitySingleOutputHooks =
            new Dictionary<Delegate, List<EntityIO.EntityOutputCallback>>();

        internal readonly List<ConVarBase> ConVars = [];

        public readonly List<CommandDefinition> CommandDefinitions = new List<CommandDefinition>();

        public readonly List<Timer> Timers = new List<Timer>();

        public delegate HookResult GameEventHandler<T>(T @event, GameEventInfo info) where T : GameEvent;

        private void RegisterEventHandlerInternal<T>(string name, GameEventHandler<T> handler, bool post)
            where T : GameEvent
        {
            CallbackSubscriber? subscriber = null;
            subscriber = new CallbackSubscriber(handler, handler, () => RemoveHandlerRegistration(subscriber!), "event",
                post ? name : name + " (pre)", uniqueReference: true);

            NativeAPI.HookEvent(name, subscriber.GetInputArgument(), post);
            var input = subscriber.GetInputArgument();
            AddHandlerRegistration(new HandlerRegistration("event", name, handler, post, subscriber,
                () => NativeAPI.UnhookEvent(name, input, post)));
        }

        /// <summary>
        /// Registers a game event handler.
        /// </summary>
        /// <typeparam name="T">The type of the game event.</typeparam>
        /// <param name="handler">The event handler to register.</param>
        /// <param name="hookMode">The mode in which the event handler is hooked. Default is `HookMode.Post`.</param>
        public void RegisterEventHandler<T>(GameEventHandler<T> handler, HookMode hookMode = HookMode.Post) where T : GameEvent
        {
            var name = typeof(T).GetCustomAttribute<EventNameAttribute>()?.Name;
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("Game event of type T is invalid and does not have a name attribute", nameof(T));
            }

            RegisterEventHandlerInternal(name, handler, hookMode == HookMode.Post);
        }

        /// <summary>
        /// De-registers a game event handler.
        /// </summary>
        /// <inheritdoc cref="RegisterEventHandler{T}"/>
        public void DeregisterEventHandler<T>(GameEventHandler<T> handler, HookMode hookMode = HookMode.Post) where T : GameEvent
        {
            var name = typeof(T).GetCustomAttribute<EventNameAttribute>()!.Name;
            RemoveHandlerRegistration("event", name, handler, hookMode == HookMode.Post);
        }

        [Obsolete("Use the generic version of this method")]
        public void DeregisterEventHandler(string name, Delegate handler, bool post)
        {
            RemoveHandlerRegistration("event", name, handler, post);
        }


        /// <summary>
        /// Registers a new server command.
        /// </summary>
        /// <param name="name">The name of the command.</param>
        /// <param name="description">The description of the command.</param>
        /// <param name="handler">The callback function to be invoked when the command is executed.</param>
        public void AddCommand(string name, string description, CommandInfo.CommandCallback handler)
        {
            var definition = new CommandDefinition(name, description, handler) { PluginName = ModuleName };
            CommandDefinitions.Add(definition);
            CommandManager.RegisterCommand(definition);
        }

        private void AddCommand(CommandDefinition definition)
        {
            CommandDefinitions.Add(definition);
            CommandManager.RegisterCommand(definition);
        }

        /// <summary>
        /// Adds a command listener which will be called before or after the command is executed on the server by a player.
        /// </summary>
        /// <param name="name">Name of the command, e.g. `jointeam`</param>
        /// <param name="handler">Code to run when command is executed. Return <see cref="HookResult.Handled"/> or higher to prevent command execution.</param>
        /// <param name="mode">Whether to hook before or after the command is executed.</param>
        public void AddCommandListener(string? name, CommandInfo.CommandListenerCallback handler, HookMode mode = HookMode.Pre)
        {
            var wrappedHandler = new Func<int, IntPtr, HookResult>((i, ptr) =>
            {
                var caller = (i != -1) ? new CCSPlayerController(NativeAPI.GetEntityFromIndex(i + 1)) : null;

                var command = new CommandInfo(ptr, caller);
                return handler.Invoke(caller, command);
            });

            CallbackSubscriber? subscriber = null;
            subscriber = new CallbackSubscriber(handler, wrappedHandler, () => RemoveCommandListenerRegistration(subscriber!),
                "cmdlistener", name ?? "*");
            NativeAPI.AddCommandListener(name, subscriber.GetInputArgument(), mode == HookMode.Post);
            _commandListenerRegistrations.Add((name, handler, mode == HookMode.Post, subscriber));
            CommandListeners[handler] = subscriber;
        }

        /// <summary>
        /// Removes a server command.
        /// </summary>
        /// <param name="name">The name of the command.</param>
        /// <param name="handler">The callback function to be invoked when the command is executed.</param>
        public void RemoveCommand(string name, CommandInfo.CommandCallback handler)
        {
            var definition = CommandDefinitions.FirstOrDefault(
                definition => definition.Name == name && definition.Callback == handler);

            if (definition != null)
            {
                CommandDefinitions.Remove(definition);
                CommandManager.RemoveCommand(definition);
            }
        }

        /// <summary>
        /// Remove a command listener.
        /// </summary>
        /// <inheritdoc cref="AddCommandListener"/>
        public void RemoveCommandListener(string? name, CommandInfo.CommandListenerCallback handler, HookMode mode)
        {
            var post = mode == HookMode.Post;
            var index = _commandListenerRegistrations.FindLastIndex(r =>
                r.Name == name && r.Post == post && r.Handler.Equals(handler));
            if (index < 0) return;

            RemoveCommandListenerRegistration(_commandListenerRegistrations[index].Subscriber);
        }

        private void RemoveCommandListenerRegistration(CallbackSubscriber subscriber)
        {
            var index = _commandListenerRegistrations.FindIndex(r => ReferenceEquals(r.Subscriber, subscriber));
            if (index < 0) return;

            var registration = _commandListenerRegistrations[index];
            _commandListenerRegistrations.RemoveAt(index);

            NativeAPI.RemoveCommandListener(registration.Name, subscriber.GetInputArgument(), registration.Post);
            FunctionReference.RemoveReleased(subscriber.GetReferenceIdentifier());

            // Keep the compatibility map pointing at a live registration of this handler, if any is left.
            var remaining = _commandListenerRegistrations.FindLastIndex(r => r.Handler.Equals(registration.Handler));
            if (remaining >= 0)
                CommandListeners[registration.Handler] = _commandListenerRegistrations[remaining].Subscriber;
            else
                CommandListeners.Remove(registration.Handler);
        }

        /// <summary>
        /// Registers a global listener, e.g. <see cref="Listeners.OnTick"/>, <see cref="Listeners.OnClientConnect"/>.
        /// </summary>
        /// <param name="handler"></param>
        /// <typeparam name="T">Listener delegate type</typeparam>
        /// <exception cref="ArgumentException">Invalid listener <see cref="T"/> provided</exception>
        /// <example>
        /// <code lang="C#">
        /// RegisterListener&lt;Listeners.OnTick&gt;(OnTick);
        /// </code>
        /// </example>
        private static HookResult ToHookResult(object? result) => result is HookResult hookResult ? hookResult : HookResult.Continue;

        // value => new TParameter((TCastFrom)value), the conversion Activator.CreateInstance(parameterType,
        // value) performed. Falls back to Activator when there is no constructor taking exactly castFrom.
        private static Func<object, object> BuildCastFactory(Type parameterType, Type castFrom)
        {
            var constructor = parameterType.GetConstructor(new[] { castFrom });
            if (constructor == null)
                return value => Activator.CreateInstance(parameterType, value)!;

            var input = System.Linq.Expressions.Expression.Parameter(typeof(object), "value");
            var body = System.Linq.Expressions.Expression.Convert(
                System.Linq.Expressions.Expression.New(constructor, System.Linq.Expressions.Expression.Convert(input, castFrom)),
                typeof(object));
            return System.Linq.Expressions.Expression.Lambda<Func<object, object>>(body, input).Compile();
        }

        public void RegisterListener<T>(T handler) where T : Delegate
        {
            var listenerName = typeof(T).GetCustomAttribute<ListenerNameAttribute>()?.Name;
            if (string.IsNullOrEmpty(listenerName))
            {
                throw new ArgumentException("Listener of type T is invalid and does not have a name attribute",
                    nameof(T));
            }

            // Every delegate type has an Invoke method.
            var invokeParameters = typeof(T).GetMethod("Invoke")!.GetParameters();
            var parameterTypes = invokeParameters.Select(p => p.ParameterType).ToArray();
            var castedParameterTypes = invokeParameters
                .Select(p => p.GetCustomAttribute<CastFromAttribute>()?.Type)
                .ToArray();

            Application.Instance.Logger.LogDebug("Registering listener for {ListenerName} with {ParameterCount} parameters",
                listenerName, parameterTypes.Length);

            // Compiled invoker for the plugin's listener delegate, built once at
            // registration (cached by MethodInfo). Replaces handler.DynamicInvoke(args) on
            // the per-tick path — OnTick etc. fire every frame. Null for unsupported shapes,
            // in which case we keep the DynamicInvoke fallback.
            var handlerInvoker = FunctionReference.GetCompiledInvoker(handler);
            var handlerTarget = handler.Target;

            // [CastFrom] parameters (CheckTransmit's CCheckTransmitInfoList, OnEntitySpawned's entity...)
            // were built with Activator.CreateInstance on every call: reflection over the constructors
            // plus an object[] for its arguments, on listeners that fire per tick or per entity.
            // One compiled factory per parameter, built here, does the same conversion.
            var castFactories = new Func<object, object>?[parameterTypes.Length];
            for (int i = 0; i < parameterTypes.Length; i++)
            {
                if (castedParameterTypes[i] is { } castFrom)
                    castFactories[i] = BuildCastFactory(parameterTypes[i], castFrom);
            }

            var wrappedHandler = new Func<ScriptContext, HookResult>(context =>
            {
                // Reuse the empty-array singleton for zero-arg listeners (OnTick
                // etc. fire every frame) instead of allocating object[0] per call.
                if (parameterTypes.Length == 0)
                {
                    return ToHookResult(handlerInvoker != null
                        ? handlerInvoker(handlerTarget!, Array.Empty<object>())
                        : handler.DynamicInvoke(Array.Empty<object>()));
                }

                // The compiled invoker reads only [0..parameterTypes.Length), so a pooled (possibly
                // larger) buffer is safe; DynamicInvoke needs the exact length and keeps its own array.
                var pooled = handlerInvoker != null;
                var args = pooled ? ArrayPool<object>.Shared.Rent(parameterTypes.Length) : new object[parameterTypes.Length];
                try
                {
                    for (int i = 0; i < parameterTypes.Length; i++)
                    {
                        var value = context.GetArgument(castedParameterTypes[i] ?? parameterTypes[i], i);
                        args[i] = castFactories[i] is { } factory ? factory(value) : value;
                    }

                    return ToHookResult(pooled ? handlerInvoker!(handlerTarget!, args) : handler.DynamicInvoke(args));
                }
                finally
                {
                    // clearArray: pooled slots must not keep entity wrappers alive until the next rent.
                    if (pooled) ArrayPool<object>.Shared.Return(args, clearArray: true);
                }
            });

#pragma warning disable CS0618 // intentional internal use of the non-generic RemoveListener
            var subscriber =
                new CallbackSubscriber(handler, wrappedHandler, () => { RemoveListener(listenerName, handler); },
                    "listener", listenerName, !CrashRecorder.IsHighFrequencyListener(listenerName));
#pragma warning restore CS0618

            NativeAPI.AddListener(listenerName, subscriber.GetInputArgument());
            _listenerRegistrations.Add((listenerName, handler, subscriber));
            Listeners[handler] = subscriber;
        }

        /// <summary>
        /// Removes a global listener.
        /// </summary>
        /// <param name="handler"></param>
        /// <typeparam name="T"></typeparam>
        /// <exception cref="ArgumentException">Invalid listener <see cref="T"/> provided</exception>
        public void RemoveListener<T>(T handler) where T : Delegate
        {
            var listenerName = typeof(T).GetCustomAttribute<ListenerNameAttribute>()?.Name;
            if (string.IsNullOrEmpty(listenerName))
            {
                throw new ArgumentException("Listener of type T is invalid and does not have a name attribute",
                    nameof(T));
            }

            RemoveListenerRegistrations(listenerName, handler);
        }

        /// <summary>
        /// Removes a global listener.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="handler"></param>
        [Obsolete("Use the generic version of this method")]
        public void RemoveListener(string name, Delegate handler)
        {
            RemoveListenerRegistrations(name, handler);
        }

        // Removes every registration of the handler for this listener (a handler registered twice was
        // called twice, so it is unregistered from both).
        private void RemoveListenerRegistrations(string name, Delegate handler)
        {
            var matching = _listenerRegistrations.Where(r => r.Name == name && r.Handler.Equals(handler)).ToList();
            if (matching.Count == 0) return;

            foreach (var registration in matching)
            {
                _listenerRegistrations.Remove(registration);
                NativeAPI.RemoveListener(name, registration.Subscriber.GetInputArgument());
                FunctionReference.RemoveReleased(registration.Subscriber.GetReferenceIdentifier());
            }

            var remaining = _listenerRegistrations.FindLastIndex(r => r.Handler.Equals(handler));
            if (remaining >= 0)
                Listeners[handler] = _listenerRegistrations[remaining].Subscriber;
            else
                Listeners.Remove(handler);
        }

        /// <summary>
        /// Adds a timer that will call the given callback after the specified amount of seconds.
        /// By default will only run once unless the <see cref="TimerFlags.REPEAT"/> flag is set.
        /// </summary>
        /// <param name="interval">Interval/Delay in seconds</param>
        /// <param name="callback">Code to run when timer elapses</param>
        /// <param name="flags">Controls if the timer is a one-off, repeat or stops on map change etc.</param>
        /// <returns>An instance of the <see cref="Timer"/></returns>
        // Finished fires when the timer can never run again (one-shot fired, Kill(), or a
        // STOP_ON_MAPCHANGE timer cut by the map change). Finished timers used to stay in this
        // list, with their closures, until the plugin unloaded.
        private void TrackTimer(Timer timer)
        {
            Timers.Add(timer);
            timer.Finished += t => Timers.Remove(t);
        }

        public Timer AddTimer(float interval, Action callback, TimerFlags? flags = null)
        {
            var timer = new Timer(interval, callback, flags ?? 0);
            TrackTimer(timer);
            return timer;
        }

        /// <summary>
        /// Adds a timer that will call the given callback after the specified amount of ticks.
        /// By default will only run once unless the <see cref="TimerFlags.REPEAT"/> flag is set.
        /// </summary>
        /// <param name="interval">Interval/Delay in ticks</param>
        /// <param name="callback">Code to run when timer elapses</param>
        /// <param name="flags">Controls if the timer is a one-off, repeat or stops on map change etc.</param>
        /// <returns>An instance of the <see cref="Timer"/></returns>
        public Timer AddTickTimer(int tickInterval, Action callback, TimerFlags? flags = null)
        {
            var timer = new Timer(tickInterval * Server.TickInterval, callback, flags ?? 0);
            TrackTimer(timer);
            return timer;
        }

        /// <summary>
        /// Registers all attribute handlers on the given instance.
        /// Can be used to register event handlers, console commands, entity outputs etc. from classes that are not derived from `BasePlugin`.
        /// </summary>
        /// <param name="instance"></param>
        public void RegisterAllAttributes(object instance)
        {
            this.RegisterAttributeHandlers(instance);
            this.RegisterConsoleCommandAttributeHandlers(instance);
            this.RegisterEntityOutputAttributeHandlers(instance);
            this.RegisterConVars(instance);
            this.RegisterFakeConVars(instance);
        }

        public void InitializeConfig(object instance, Type pluginType)
        {
            Type[] interfaces = pluginType.GetInterfaces();
            Func<Type, bool> predicate = (i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPluginConfig<>));

            // if the plugin has set a configuration type (implements IPluginConfig<>)
            if (interfaces.Any(predicate))
            {
                // IPluginConfig<>
                Type @interface = interfaces.Where(predicate).FirstOrDefault()!;

                // custom config type passed as generic
                Type genericType = @interface!.GetGenericArguments().First();

                var config = typeof(ConfigManager)
                    .GetMethod("Load")!
                    .MakeGenericMethod(genericType)
                    .Invoke(null, new object[] { Path.GetFileName(ModuleDirectory) }) as IBasePluginConfig;

                // we KNOW that we can do this "safely"
                var onConfigParsed = pluginType.GetRuntimeMethod("OnConfigParsed", new Type[] { genericType })
                                     ?? throw new InvalidOperationException(
                                         $"{pluginType.FullName} implements IPluginConfig<{genericType.Name}> but has no OnConfigParsed({genericType.Name}) method");
                onConfigParsed.Invoke(instance, new object?[] { config });
            }
        }

        /// <summary>
        /// Registers all game event handlers that are decorated with the <see cref="GameEventHandlerAttribute"/> and <see cref="ListenerHandlerAttribute{T}"/> attribute.
        /// </summary>
        /// <param name="instance">The instance of the object where the event handlers are defined.</param>
        public void RegisterAttributeHandlers(object instance)
        {
            var methods = instance.GetType().GetMethods();

            var eventHandlers = methods
                .Where(method => method.GetCustomAttribute<GameEventHandlerAttribute>() != null)
                .Where(method =>
                    method.GetParameters().FirstOrDefault()?.ParameterType.IsSubclassOf(typeof(GameEvent)) == true)
                .ToArray();

            var listenerHandlers = methods
                .Where(method => method.GetCustomAttribute(typeof(ListenerHandlerAttribute<>)) != null)
                .ToArray();

            var registerEvent = typeof(BasePlugin).GetMethod(nameof(RegisterEventHandlerInternal), BindingFlags.NonPublic |
                BindingFlags.Instance)!;
            var registerListener = GetType().GetMethod(nameof(RegisterListener))!;

            foreach (var eventHandler in eventHandlers)
            {
                var parameterType = eventHandler.GetParameters().First().ParameterType;
                var attribute = parameterType.GetCustomAttribute<EventNameAttribute>();
                var eventName = attribute?.Name;
                var hookMode = eventHandler.GetCustomAttribute<GameEventHandlerAttribute>()!.Mode;

                var actionType = typeof(GameEventHandler<>).MakeGenericType(parameterType);
                var action = Delegate.CreateDelegate(actionType, instance, eventHandler);

                var registerEventGeneric = registerEvent.MakeGenericMethod(parameterType);
                registerEventGeneric.Invoke(this, [eventName, action, hookMode == HookMode.Post]);
            }

            foreach (var listnerHandler in listenerHandlers)
            {
                var attribute = listnerHandler.GetCustomAttribute(typeof(ListenerHandlerAttribute<>))!;
                var listenerType = attribute.GetType().GetGenericArguments().First();

                if (listenerType.GetCustomAttribute<ListenerNameAttribute>() == null)
                    throw new ArgumentException("Listener of type T is invalid and does not have a name attribute",
                        listenerType.Name);

                var listenerDelegate = Delegate.CreateDelegate(listenerType, instance, listnerHandler);

                registerListener.MakeGenericMethod(listenerType).Invoke(this, [listenerDelegate]);
            }
        }

        /// <summary>
        /// Registers all console command handlers that are decorated with the <see cref="ConsoleCommandAttribute"/> attribute.
        /// </summary>
        /// <param name="instance">The instance of the object where the console command handlers are defined.</param>
        public void RegisterConsoleCommandAttributeHandlers(object instance)
        {
            var eventHandlers = instance.GetType()
                .GetMethods()
                .Where(method => method.GetCustomAttributes<ConsoleCommandAttribute>().Any())
                .ToArray();

            foreach (var eventHandler in eventHandlers)
            {
                var attributes = eventHandler.GetCustomAttributes<ConsoleCommandAttribute>();
                var helperAttribute = eventHandler.GetCustomAttribute<CommandHelperAttribute>();
                foreach (var commandInfo in attributes)
                {
                    var definition = new CommandDefinition()
                    {
                        Name = commandInfo.Command,
                        Description = commandInfo.Description,
                        Callback = eventHandler.CreateDelegate<CommandInfo.CommandCallback>(instance),
                        MinArgs = helperAttribute?.MinArgs,
                        UsageHint = helperAttribute?.Usage,
                        ExecutableBy = helperAttribute?.WhoCanExcecute ?? CommandUsage.CLIENT_AND_SERVER,
                        PluginName = ModuleName,
                    };
                    AddCommand(definition);
                }
            }
        }

        /// <summary>
        /// Registers all entity output handlers that are decorated with the <see cref="EntityOutputHookAttribute"/> attribute.
        /// </summary>
        /// <param name="instance">The instance of the object where entity output hook handlers are defined.</param>
        public void RegisterEntityOutputAttributeHandlers(object instance)
        {
            var handlers = instance.GetType()
                .GetMethods()
                .Where(method => method.GetCustomAttributes<EntityOutputHookAttribute>().Any())
                .ToArray();

            foreach (var handler in handlers)
            {
                var attributes = handler.GetCustomAttributes<EntityOutputHookAttribute>();
                foreach (var outputInfo in attributes)
                {
                    HookEntityOutput(outputInfo.Classname, outputInfo.OutputName, handler.CreateDelegate<EntityIO.EntityOutputHandler>(instance));
                }
            }
        }

        public void RegisterFakeConVars(Type type, object? instance = null)
        {
            var convars = type
                .GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(prop => prop.FieldType.IsGenericType &&
                               prop.FieldType.GetGenericTypeDefinition() == typeof(FakeConVar<>));

            foreach (var prop in convars)
            {
                object? propValue = prop.GetValue(instance); // FakeConvar<?> instance
                // An unassigned field (or an instance field probed without an
                // instance) has no convar to bind.
                if (propValue == null) continue;

                // Name, Description and ExecuteCommand are members of FakeConVar<>,
                // which the field type is a closed instance of.
                var name = (string)prop.FieldType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)!
                    .GetValue(propValue)!;

                var description = (string)prop.FieldType.GetProperty("Description", BindingFlags.Public | BindingFlags.Instance)!
                    .GetValue(propValue)!;

                MethodInfo executeCommandMethod = prop.FieldType
                    .GetMethod("ExecuteCommand", BindingFlags.Instance | BindingFlags.NonPublic)!;

                this.AddCommand(name, description, (caller, command) =>
                {
                    executeCommandMethod.Invoke(propValue, new object?[] { caller, command });
                });
            }
        }

        public void RegisterConVars(Type type, object? instance = null)
        {
            // Every owned ConVar<T> found here is deleted on unload. Fields-only (upstream) missed
            // `public ConVar<int> Foo { get; } = new(...)` and private fields, so those outlived the
            // plugin and the next hot reload / css_plugins reload failed with
            // "Convar with name '...' already exists.". Probe properties and non-public members too;
            // auto-property backing fields make duplicates likely, hence the Contains check.
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                        (instance != null ? BindingFlags.Instance : 0);

            var values = type.GetFields(flags)
                .Where(field => typeof(ConVarBase).IsAssignableFrom(field.FieldType))
                .Select(field => field.GetValue(instance))
                .Concat(type.GetProperties(flags)
                    .Where(prop => typeof(ConVarBase).IsAssignableFrom(prop.PropertyType) &&
                                   prop.CanRead && prop.GetIndexParameters().Length == 0)
                    .Select(prop =>
                    {
                        // A getter can throw or compute a fresh wrapper; neither may fail plugin load.
                        try { return prop.GetValue(instance); }
                        catch { return null; }
                    }));

            foreach (var value in values)
            {
                if (value is ConVarBase { Owned: true } conVar && !ConVars.Contains(conVar))
                    ConVars.Add(conVar);
            }
        }

        /// <summary>
        /// Used to bind a fake ConVar to a plugin command. Only required for ConVars that are not public properties of the plugin class.
        /// </summary>
        /// <param name="convar"></param>
        /// <typeparam name="T"></typeparam>
        public void RegisterFakeConVars(object instance)
        {
            RegisterFakeConVars(instance.GetType(), instance);
        }

        public void RegisterConVars(object instance)
        {
            RegisterConVars(instance.GetType(), instance);
        }

        /// <summary>
        /// Hooks an <a href="https://developer.valvesoftware.com/wiki/Inputs_and_Outputs">entity output</a>.
        /// </summary>
        /// <param name="classname">Classname to hook, or `*` for wildcard</param>
        /// <param name="outputName">Output name to hook, or `*` for wildcard</param>
        /// <param name="handler">Handler to call</param>
        public void HookEntityOutput(string classname, string outputName, EntityIO.EntityOutputHandler handler, HookMode mode = HookMode.Pre)
        {
            CallbackSubscriber? subscriber = null;
            // Own reference per registration (uniqueReference): with the shared one, removing either of
            // two registrations of the same handler removed the reference the other was still using.
            subscriber = new CallbackSubscriber(handler, handler, () => RemoveEntityOutputRegistration(subscriber!),
                null, null, uniqueReference: true);

            NativeAPI.HookEntityOutput(classname, outputName, subscriber.GetInputArgument(), mode);
            _entityOutputRegistrations.Add((classname, outputName, mode, handler, subscriber));
            EntityOutputHooks[handler] = subscriber;
        }

        public void HookUserMessage(int messageId, UserMessage.UserMessageHandler handler, HookMode mode = HookMode.Pre)
        {
            CallbackSubscriber? subscriber = null;
            subscriber = new CallbackSubscriber(handler, handler, () => RemoveHandlerRegistration(subscriber!), null, null,
                uniqueReference: true);

            NativeAPI.HookUsermessage(messageId, subscriber.GetInputArgument(), mode);
            var input = subscriber.GetInputArgument();
            AddHandlerRegistration(new HandlerRegistration("usermessage", messageId.ToString(), handler,
                mode == HookMode.Post, subscriber, () => NativeAPI.UnhookUsermessage(messageId, input, mode)));
        }

        public void UnhookUserMessage(int messageId, UserMessage.UserMessageHandler handler, HookMode mode = HookMode.Pre)
        {
            RemoveHandlerRegistration("usermessage", messageId.ToString(), handler, mode == HookMode.Post);
        }

        /// <summary>
        /// Unhooks an entity output.
        /// </summary>
        /// <inheritdoc cref="HookEntityOutput"/>
        public void UnhookEntityOutput(string classname, string outputName, EntityIO.EntityOutputHandler handler, HookMode mode = HookMode.Pre)
        {
            foreach (var registration in _entityOutputRegistrations
                         .Where(r => r.Classname == classname && r.Output == outputName && r.Mode == mode &&
                                     r.Handler.Equals(handler))
                         .ToList())
            {
                RemoveEntityOutputRegistration(registration.Subscriber);
            }
        }

        private void RemoveEntityOutputRegistration(CallbackSubscriber subscriber)
        {
            var index = _entityOutputRegistrations.FindIndex(r => ReferenceEquals(r.Subscriber, subscriber));
            if (index < 0) return;

            var registration = _entityOutputRegistrations[index];
            _entityOutputRegistrations.RemoveAt(index);

            // Mode comes from the registration: native keeps separate pre and post listener lists.
            NativeAPI.UnhookEntityOutput(registration.Classname, registration.Output, subscriber.GetInputArgument(),
                registration.Mode);
            FunctionReference.RemoveReleased(subscriber.GetReferenceIdentifier());

            var remaining = _entityOutputRegistrations.FindLastIndex(r => r.Handler.Equals(registration.Handler));
            if (remaining >= 0)
                EntityOutputHooks[registration.Handler] = _entityOutputRegistrations[remaining].Subscriber;
            else
                EntityOutputHooks.Remove(registration.Handler);
        }

        /// <summary>
        /// Hooks an entity output for a single entity instance.
        /// </summary>
        /// <param name="entityInstance">Entity instance to hook</param>
        /// <param name="outputName">Output name to hook, or `*` for wildcard</param>
        /// <param name="handler">Handler to call</param>
        public void HookSingleEntityOutput(CEntityInstance entityInstance, string outputName, EntityIO.EntityOutputHandler handler)
        {
            // since we wrap around the plugin handler we need to do this to ensure that the plugin callback is only called
            // if the entity instance is the same.
            EntityIO.EntityOutputHandler internalHandler = (output, name, activator, caller, value, delay) =>
            {
                if (caller == entityInstance)
                {
                    return handler(output, name, activator, caller, value, delay);
                }

                return HookResult.Continue;
            };

            HookEntityOutput(entityInstance.DesignerName, outputName, internalHandler);

            // because of ^ we would not be able to unhook since we passed the 'internalHandler' and that's what is being stored, not the original handler
            // but the plugin could only pass the original handler for unhooking.
            // (this dictionary does not needed to be cleared on dispose as it has no unmanaged reference and those are already being disposed, but on map end)
            // (the internal class is needed to be able to remove them on map start)
            if (!EntitySingleOutputHooks.TryGetValue(handler, out var hooks))
                EntitySingleOutputHooks[handler] = hooks = new List<EntityIO.EntityOutputCallback>();
            hooks.Add(new EntityIO.EntityOutputCallback(entityInstance.DesignerName, outputName, internalHandler)
            {
                EntityHandle = entityInstance.EntityHandle.Raw
            });
        }

        /// <summary>
        /// Unhooks an entity output for a single entity instance.
        /// </summary>
        /// <inheritdoc cref="HookSingleEntityOutput"/>
        public void UnhookSingleEntityOutput(CEntityInstance entityInstance, string outputName, EntityIO.EntityOutputHandler handler)
        {
            if (!EntitySingleOutputHooks.TryGetValue(handler, out var hooks)) return;

            var raw = entityInstance.EntityHandle.Raw;
            foreach (var hook in hooks.Where(h => h.EntityHandle == raw && h.Output == outputName).ToList())
            {
                UnhookEntityOutput(hook.Classname, hook.Output, hook.Handler);
                hooks.Remove(hook);
            }

            if (hooks.Count == 0)
                EntitySingleOutputHooks.Remove(handler);
        }

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (!disposing) return;

            // Snapshot each collection: subscriber.Dispose() calls back into
            // Deregister*/Remove*, which mutates the same dictionary. Iterating
            // .Values directly would throw InvalidOperationException partway
            // through the first loop and leak the remaining subscribers/timers.
            foreach (var registration in _handlerRegistrations.ToList())
            {
                registration.Subscriber.Dispose();
            }

            foreach (var subscriber in Handlers.Values.ToList())
            {
                subscriber.Dispose();
            }

            foreach (var registration in _commandListenerRegistrations.ToList())
            {
                registration.Subscriber.Dispose();
            }

            foreach (var registration in _listenerRegistrations.ToList())
            {
                registration.Subscriber.Dispose();
            }

            foreach (var subscriber in Listeners.Values.ToList())
            {
                subscriber.Dispose();
            }

            foreach (var registration in _entityOutputRegistrations.ToList())
            {
                registration.Subscriber.Dispose();
            }

            foreach (var subscriber in EntityOutputHooks.Values.ToList())
            {
                subscriber.Dispose();
            }

            foreach (var convar in ConVars.ToList())
            {
                if (!convar.IsValid) continue;

                convar.Delete();
            }

            foreach (var definition in CommandDefinitions.ToList())
            {
                CommandManager.RemoveCommand(definition);
            }

            foreach (var timer in Timers.ToList())
            {
                timer.Kill();
            }

            // Drop any capability suppliers this plugin registered. Their
            // delegates live in static Providers dictionaries and would
            // otherwise pin this plugin's AssemblyLoadContext forever.
            Capabilities.Capabilities.RemoveProvidersForAssembly(GetType().Assembly);

            // Remove any dynamic-function hooks the plugin installed via
            // MemoryFunction*.Hook but never explicitly Unhooked. These are not tracked
            // per-plugin (the function objects are static/shared), so their handler
            // delegates would otherwise pin this plugin's AssemblyLoadContext forever.
            Modules.Memory.DynamicFunctions.BaseMemoryFunction.RemoveHooksForAssembly(GetType().Assembly);

            // Timers made with `new Timer(...)` instead of AddTimer are not in Timers.
            Timer.KillTimersForAssembly(GetType().Assembly);

            // Compiled invokers for this plugin's handlers live in a static cache keyed by MethodInfo.
            FunctionReference.RemoveInvokersForAssembly(GetType().Assembly);

            // Open menus of this plugin hold it (and its option delegates) in a static dictionary.
            Modules.Menu.MenuManager.RemoveMenusForAssembly(this, GetType().Assembly);

            _disposed = true;
        }
    }
}
