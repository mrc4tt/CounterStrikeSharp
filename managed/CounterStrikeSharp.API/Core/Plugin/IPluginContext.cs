namespace CounterStrikeSharp.API.Core.Plugin;

public interface IPluginContext
{
    PluginState State { get; }
    IPlugin Plugin { get; }
    int PluginId { get; }

    string FilePath { get; }
    void Load(bool hotReload);
    void Unload(bool hotReload);

    /// <summary>
    /// Pre-validates the on-disk DLL, then disposes the previous AssemblyLoadContext and
    /// loads the current bytes from disk. If pre-validation fails, the existing plugin
    /// stays loaded and this method returns false.
    /// </summary>
    bool Reload(bool hotReload);
}