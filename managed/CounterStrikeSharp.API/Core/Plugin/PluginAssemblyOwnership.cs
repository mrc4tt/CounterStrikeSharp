using System;
using System.Reflection;
using System.Runtime.Loader;

namespace CounterStrikeSharp.API.Core.Plugin;

/// <summary>
/// Decides whether something registered in a static API collection belongs to a plugin that is unloading.
/// </summary>
/// <remarks>
/// Comparing against the plugin's main assembly alone missed everything registered from a private
/// dependency loaded next to it (a plugin split into Plugin.dll + Plugin.Core.dll): that hook, timer
/// or capability supplier stayed registered, kept calling the old instance after a reload and pinned
/// the plugin's AssemblyLoadContext. A plugin owns its whole load context, so that is what is compared.
/// The default context is never treated as owned: the API, the runtime and shared assemblies live there.
/// </remarks>
internal static class PluginAssemblyOwnership
{
    internal static bool Owns(Assembly plugin, Assembly? candidate)
    {
        if (candidate is null) return false;
        if (candidate == plugin) return true;

        var context = AssemblyLoadContext.GetLoadContext(plugin);
        if (context is null || context == AssemblyLoadContext.Default) return false;

        return AssemblyLoadContext.GetLoadContext(candidate) == context;
    }

    internal static bool Owns(Assembly plugin, Type? type) => type is not null && Owns(plugin, type.Assembly);

    /// <summary>
    /// A delegate is owned when its method or the object it is bound to comes from the plugin. The
    /// target matters for a delegate over an API or generic method bound to a plugin object.
    /// </summary>
    internal static bool Owns(Assembly plugin, Delegate? handler)
    {
        if (handler is null) return false;
        if (Owns(plugin, handler.Method.Module.Assembly)) return true;

        return handler.Target is { } target && Owns(plugin, target.GetType().Assembly);
    }
}
