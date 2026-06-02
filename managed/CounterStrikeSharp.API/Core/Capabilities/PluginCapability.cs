using System.Collections.Generic;

namespace CounterStrikeSharp.API.Core.Capabilities;

public sealed class PluginCapability<T>
{
    public string Name { get; }
    internal static readonly Dictionary<string, List<Func<T>>> Providers = new();

    public PluginCapability(string name)
    {
        Name = name;
    }

    public T? Get()
    {
        // TryGetValue: the key is absent when no provider was ever registered,
        // and the list can be empty after a providing plugin unloaded.
        if (Providers.TryGetValue(Name, out var list))
        {
            foreach (var provider in list)
            {
                return provider();
            }
        }

        return default;
    }
}