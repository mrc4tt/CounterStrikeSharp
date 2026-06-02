using System;
using System.Collections.Generic;
using System.Reflection;

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
    private static readonly List<Action<Assembly>> _purgers = new();
    private static readonly HashSet<Type> _purgerTypes = new();
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
            foreach (var purge in _purgers)
            {
                purge(assembly);
            }
        }
    }

    private static void EnsurePurger(Type capabilityType, Action<Assembly> purger)
    {
        lock (_lock)
        {
            if (_purgerTypes.Add(capabilityType))
            {
                _purgers.Add(purger);
            }
        }
    }

    private static void PurgeProviders<TDelegate>(Dictionary<string, List<TDelegate>> providers, Assembly assembly)
        where TDelegate : Delegate
    {
        foreach (var list in providers.Values)
        {
            list.RemoveAll(d => d.Method.DeclaringType?.Assembly == assembly);
        }
    }
}
