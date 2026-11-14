using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace CounterStrikeSharp.API.Core.Plugin;

/// <summary>
/// Last-chance lookup of a plugin's native libraries ([DllImport("e_sqlite3")] and the like) in the
/// plugin's own folder.
/// <para>
/// McMaster's load context resolves natives from the plugin's deps.json, and without one only from
/// the plugin folder itself. A RID-agnostic publish (dotnet publish without -r) keeps the native under
/// runtimes/&lt;rid&gt;/native/, so a deploy that drops or omits deps.json fails with
/// DllNotFoundException "Unable to load shared library 'e_sqlite3'", probing only the runtime's
/// Microsoft.NETCore.App folder. McMaster's own base-folder fallback only runs for names that already
/// carry an extension ("e_sqlite3.so"), never for a bare "e_sqlite3".
/// </para>
/// <para>
/// Hooked on <see cref="AssemblyLoadContext.ResolvingUnmanagedDll"/>, which the runtime raises only
/// after the context's own lookup and the default probing failed, so a plugin that loads today keeps
/// loading exactly the same file. The plugin folder comes from its path on disk: plugin assemblies are
/// loaded in memory and their Assembly.Location is "".
/// </para>
/// <para>
/// The library is loaded from a copy in a per-context temp folder, never from the plugin folder.
/// </para>
/// </summary>
internal static class PluginNativeLibraryFallback
{
    // Keys are weak, so this never keeps an unloaded plugin's context alive.
    private static readonly ConditionalWeakTable<AssemblyLoadContext, string> Attached = new();

    public static void Attach(AssemblyLoadContext? context, string pluginDirectory)
    {
        if (context == null || context == AssemblyLoadContext.Default) return;

        lock (Attached)
        {
            if (Attached.TryGetValue(context, out _)) return;
            Attached.Add(context, pluginDirectory);
        }

        // The handlers capture only strings: they live on the context and must not root the plugin.
        var shadowDirectory = Path.Join(Path.GetTempPath(), "css-native-" + Path.GetRandomFileName());
        context.ResolvingUnmanagedDll += (_, name) => Resolve(pluginDirectory, name, shadowDirectory);
        context.Unloading += _ => DeleteShadowCopies(shadowDirectory);
    }

    internal static IntPtr Resolve(string pluginDirectory, string libraryName, string shadowDirectory)
    {
        foreach (var candidate in Candidates(pluginDirectory, libraryName, OperatingSystem.IsWindows(), PortableRid()))
        {
            if (!File.Exists(candidate)) continue;

            // Load a copy, as McMaster does for every native it resolves itself (LoadInMemory). The
            // original stays free: Windows locks a loaded .dll, so a deploy could not replace it, and on
            // Linux overwriting a mapped .so in place corrupts the running copy.
            string path;
            try
            {
                path = ShadowCopy(candidate, shadowDirectory);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (NativeLibrary.TryLoad(path, out var handle)) return handle;
        }

        return IntPtr.Zero;
    }

    private static string ShadowCopy(string file, string shadowDirectory)
    {
        Directory.CreateDirectory(shadowDirectory);
        var copy = Path.Join(shadowDirectory, Path.GetFileName(file));
        if (!File.Exists(copy)) File.Copy(file, copy);
        return copy;
    }

    private static void DeleteShadowCopies(string shadowDirectory)
    {
        try
        {
            if (Directory.Exists(shadowDirectory)) Directory.Delete(shadowDirectory, recursive: true);
        }
        catch (Exception)
        {
            // Still loaded (Windows keeps the file locked until the process exits): leave it in temp.
        }
    }

    /// <summary>Files to try, in order: the plugin folder first, then runtimes/&lt;rid&gt;/native/.</summary>
    internal static IEnumerable<string> Candidates(string pluginDirectory, string libraryName, bool windows, string rid)
    {
        if (string.IsNullOrEmpty(libraryName) || Path.IsPathRooted(libraryName) ||
            libraryName.IndexOfAny(new[] { '/', '\\' }) >= 0)
            yield break;

        var fileNames = FileNames(libraryName, windows);
        var directories = new[] { pluginDirectory, Path.Join(pluginDirectory, "runtimes", rid, "native") };

        foreach (var directory in directories)
        {
            foreach (var fileName in fileNames)
            {
                yield return Path.Join(directory, fileName);
            }
        }
    }

    private static List<string> FileNames(string name, bool windows)
    {
        var names = new List<string>();
        if (windows)
        {
            if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) names.Add(name + ".dll");
            names.Add(name);
            return names;
        }

        // Already a file name ("libfoo.so", "libfoo.so.1"): use it as given.
        if (name.EndsWith(".so", StringComparison.Ordinal) || name.Contains(".so.", StringComparison.Ordinal))
        {
            names.Add(name);
            if (!name.StartsWith("lib", StringComparison.Ordinal)) names.Add("lib" + name);
            return names;
        }

        if (!name.StartsWith("lib", StringComparison.Ordinal)) names.Add("lib" + name + ".so");
        names.Add(name + ".so");
        if (!name.StartsWith("lib", StringComparison.Ordinal)) names.Add("lib" + name);
        names.Add(name);
        return names;
    }

    // NuGet's portable RID ("linux-x64", "win-x64"). RuntimeInformation.RuntimeIdentifier can report a
    // distro RID ("debian.13-x64") on a source-built runtime, which no package uses as a folder name.
    private static string PortableRid()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        return $"{os}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
    }
}
