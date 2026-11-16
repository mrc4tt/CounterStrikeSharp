using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CounterStrikeSharp.API.Core.Plugin;

namespace CounterStrikeSharp.API.Core.Capabilities;

public static class Capabilities
{
    // One non-generic purge hook per generic capability type ever used, each
    // closing over that type's static Providers dictionary. Lets
    // RemoveProvidersForAssembly strip every supplier owned by an unloading
    // plugin from a single entry point — reaching PluginCapability<T>.Providers
    // for every T. Without this, a registered supplier delegate pins the
    // plugin's AssemblyLoadContext forever, so the ALC can never be collected
    // on reload (memory leak).
    private static readonly Dictionary<Type, Action<Assembly>> _purgers = new();
    private static readonly object _lock = new();

    public static void RegisterPluginCapability<T>(PluginCapability<T> capability, Func<T> supplier)
    {
        EnsurePurger(typeof(PluginCapability<T>),
            asm => PurgeProviders(PluginCapability<T>.Providers, asm));

        if (!PluginCapability<T>.Providers.ContainsKey(capability.Name))
        {
            PluginCapability<T>.Providers.Add(capability.Name, new());
        }

        PluginCapability<T>.Providers[capability.Name].Add(supplier);
    }

    public static void RegisterPlayerCapability<T>(PlayerCapability<T> capability,
        Func<CCSPlayerController, T> supplier)
    {
        EnsurePurger(typeof(PlayerCapability<T>),
            asm => PurgeProviders(PlayerCapability<T>.Providers, asm));

        if (!PlayerCapability<T>.Providers.ContainsKey(capability.Name))
        {
            PlayerCapability<T>.Providers.Add(capability.Name, new());
        }

        PlayerCapability<T>.Providers[capability.Name].Add(supplier);
    }

    /// <summary>
    /// Removes every capability supplier owned by the given assembly. Called on
    /// plugin unload so the plugin's AssemblyLoadContext becomes collectable.
    /// </summary>
    internal static void RemoveProvidersForAssembly(Assembly assembly)
    {
        lock (_lock)
        {
            foreach (var purge in _purgers.Values)
            {
                purge(assembly);
            }

            // A capability over one of the plugin's own types (its contract shipped next to the plugin
            // instead of in shared/) leaves a purger for PluginCapability<ThatType> here, and that
            // generic instantiation roots the plugin's load context just like a supplier did.
            foreach (var capabilityType in _purgers.Keys.ToList())
            {
                if (capabilityType.GetGenericArguments().Any(argument => ReferencesPluginType(assembly, argument)))
                {
                    _purgers.Remove(capabilityType);
                }
            }
        }
    }

    private static void EnsurePurger(Type capabilityType, Action<Assembly> purger)
    {
        lock (_lock)
        {
            _purgers.TryAdd(capabilityType, purger);
        }
    }

    private static void PurgeProviders<TDelegate>(Dictionary<string, List<TDelegate>> providers, Assembly assembly)
        where TDelegate : Delegate
    {
        foreach (var list in providers.Values)
        {
            // By load context, not by the plugin's main assembly: a supplier declared in a private
            // dependency of the plugin stayed first in the list, so Get() kept handing out objects
            // from the unloaded plugin after a reload.
            list.RemoveAll(d => PluginAssemblyOwnership.Owns(assembly, d));
        }
    }

    private static bool ReferencesPluginType(Assembly assembly, Type type)
    {
        if (PluginAssemblyOwnership.Owns(assembly, type)) return true;
        if (type.HasElementType) return ReferencesPluginType(assembly, type.GetElementType()!);

        return type.IsGenericType && type.GetGenericArguments().Any(argument => ReferencesPluginType(assembly, argument));
    }
}
