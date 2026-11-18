using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using CounterStrikeSharp.API.Core.Plugin;
using Xunit;

namespace CounterStrikeSharp.API.Tests;

public class PluginNativeLibraryFallbackTests
{
    [Fact]
    public void Candidates_BareNameOnLinux_TriesPluginFolderThenRuntimesFolder()
    {
        var candidates = PluginNativeLibraryFallback.Candidates("/p", "e_sqlite3", windows: false, rid: "linux-x64").ToList();

        Assert.Equal(new[]
        {
            "/p/libe_sqlite3.so", "/p/e_sqlite3.so", "/p/libe_sqlite3", "/p/e_sqlite3",
            "/p/runtimes/linux-x64/native/libe_sqlite3.so", "/p/runtimes/linux-x64/native/e_sqlite3.so",
            "/p/runtimes/linux-x64/native/libe_sqlite3", "/p/runtimes/linux-x64/native/e_sqlite3",
        }, candidates);
    }

    [Fact]
    public void Candidates_FileNameOnLinux_IsKeptAsGiven()
    {
        var candidates = PluginNativeLibraryFallback.Candidates("/p", "libfoo.so.1", windows: false, rid: "linux-x64").ToList();

        Assert.Equal(new[] { "/p/libfoo.so.1", "/p/runtimes/linux-x64/native/libfoo.so.1" }, candidates);
    }

    [Fact]
    public void Candidates_Windows_AddsDllExtension()
    {
        var candidates = PluginNativeLibraryFallback.Candidates("/p", "e_sqlite3", windows: true, rid: "win-x64").ToList();

        Assert.Equal(new[]
        {
            "/p/e_sqlite3.dll", "/p/e_sqlite3",
            "/p/runtimes/win-x64/native/e_sqlite3.dll", "/p/runtimes/win-x64/native/e_sqlite3",
        }, candidates);
    }

    [Theory]
    [InlineData("/usr/lib/libz.so")]
    [InlineData("../evil")]
    [InlineData("sub\\evil")]
    [InlineData("")]
    public void Candidates_PathsAreNotProbed(string name)
    {
        Assert.Empty(PluginNativeLibraryFallback.Candidates("/p", name, windows: false, rid: "linux-x64"));
    }

    [Fact]
    public void NativeUnderRuntimesFolder_ResolvesThroughThePluginContext()
    {
        const string systemLib = "/lib/x86_64-linux-gnu/libz.so.1";
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            !File.Exists(systemLib))
            return;

        var dir = Path.Combine(Path.GetTempPath(), "css-native-fallback-" + Guid.NewGuid().ToString("N"));
        var nativeDir = Path.Combine(dir, "runtimes", "linux-x64", "native");
        Directory.CreateDirectory(nativeDir);
        // A RID-agnostic publish without deps.json: the native exists only under runtimes/<rid>/native/.
        File.Copy(systemLib, Path.Combine(nativeDir, "libcssfallbacktest.so"));

        var context = new AssemblyLoadContext("css-native-fallback-test", isCollectible: true);
        try
        {
            var assembly = context.LoadFromAssemblyPath(typeof(PluginNativeLibraryFallbackTests).Assembly.Location);

            Assert.False(NativeLibrary.TryLoad("cssfallbacktest", assembly, null, out _));

            PluginNativeLibraryFallback.Attach(context, dir);
            PluginNativeLibraryFallback.Attach(context, dir); // second attach is a no-op

            Assert.True(NativeLibrary.TryLoad("cssfallbacktest", assembly, null, out var handle));
            Assert.NotEqual(IntPtr.Zero, handle);
            Assert.True(NativeLibrary.TryGetExport(handle, "zlibVersion", out _));

            // Mapped from a shadow copy in temp, not from the plugin folder, so a deploy can replace it.
            var mapped = File.ReadAllLines("/proc/self/maps").Where(l => l.Contains("libcssfallbacktest.so")).ToList();
            Assert.NotEmpty(mapped);
            Assert.DoesNotContain(mapped, l => l.Contains(dir));
            Assert.Contains(mapped, l => l.Contains("css-native-"));
        }
        finally
        {
            context.Unload();
            try { Directory.Delete(dir, true); }
            catch (IOException) { }
        }
    }

    // The runtime never unloads a library a resolving handler returned. With a new shadow path per
    // context every plugin reload left one more copy of the library mapped.
    [Fact]
    public void ReloadingThePlugin_MapsTheNativeOnce()
    {
        const string systemLib = "/lib/x86_64-linux-gnu/libz.so.1";
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            !File.Exists(systemLib))
            return;

        var dir = Path.Combine(Path.GetTempPath(), "css-native-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.Copy(systemLib, Path.Combine(dir, "libcssreloadtest.so"));

        try
        {
            for (var i = 0; i < 4; i++)
            {
                var context = new AssemblyLoadContext("css-native-reload-test", isCollectible: true);
                var assembly = context.LoadFromAssemblyPath(typeof(PluginNativeLibraryFallbackTests).Assembly.Location);
                PluginNativeLibraryFallback.Attach(context, dir);
                Assert.True(NativeLibrary.TryLoad("cssreloadtest", assembly, null, out _));
                context.Unload();
            }

            var mapped = File.ReadAllLines("/proc/self/maps").Where(l => l.Contains("libcssreloadtest.so"))
                .Select(l => l.Substring(l.IndexOf('/'))).Distinct().ToList();
            Assert.Single(mapped);
        }
        finally
        {
            try { Directory.Delete(dir, true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void ShadowCopy_SamePathForAnUnchangedFile_NewPathAfterAnUpdate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "css-native-fallback-" + Guid.NewGuid().ToString("N"));
        var shadow = Path.Combine(dir, "shadow");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "libx.so");

        try
        {
            File.WriteAllText(file, "one");
            var first = PluginNativeLibraryFallback.ShadowCopy(file, shadow);
            Assert.Equal(first, PluginNativeLibraryFallback.ShadowCopy(file, shadow));
            Assert.Equal("one", File.ReadAllText(first));

            File.WriteAllText(file, "three");
            var updated = PluginNativeLibraryFallback.ShadowCopy(file, shadow);
            Assert.NotEqual(first, updated);
            Assert.Equal("three", File.ReadAllText(updated));
            Assert.Equal("one", File.ReadAllText(first));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
