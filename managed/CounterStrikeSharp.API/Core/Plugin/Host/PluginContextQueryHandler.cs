using System.Linq;

namespace CounterStrikeSharp.API.Core.Plugin.Host;

public class PluginContextQueryHandler : IPluginContextQueryHandler
{
    private readonly IPluginManager _pluginManager;

    public PluginContextQueryHandler(IPluginManager pluginManager)
    {
        _pluginManager = pluginManager;
    }

    // NOTE: x.Plugin can be null for a context whose Load() threw before it
    // built the plugin instance (e.g. a dependency failed to resolve at runtime).
    // Such a "zombie" context can linger in the manager list; dereferencing
    // x.Plugin.* here without a guard throws NRE and silently kills the whole
    // css_plugins command, so every predicate must null-check x.Plugin first.
    public IPluginContext? FindPluginByType(Type moduleClass)
    {
        return _pluginManager.GetLoadedPlugins().FirstOrDefault(x => x.Plugin?.GetType() == moduleClass);
    }

    public IPluginContext? FindPluginById(int id)
    {
        return _pluginManager.GetLoadedPlugins().FirstOrDefault(x => x.PluginId == id);
    }

    public IPluginContext? FindPluginByModuleName(string name)
    {
        return _pluginManager.GetLoadedPlugins().FirstOrDefault(x => x.Plugin?.ModuleName == name);
    }

    public IPluginContext? FindPluginByModulePath(string path)
    {
        return _pluginManager.GetLoadedPlugins().FirstOrDefault(x => x.Plugin?.ModulePath == path);
    }

    public IPluginContext? FindPluginByIdOrName(string query)
    {
        return _pluginManager.GetLoadedPlugins().FirstOrDefault(x => x.PluginId.ToString() == query || x.Plugin?.ModuleName == query);
    }
}