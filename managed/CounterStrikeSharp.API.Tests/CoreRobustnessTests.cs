using System.Reflection;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Diagnostics;
using CounterStrikeSharp.API.Core.Hosting;
using CounterStrikeSharp.API.Core.Plugin.Host;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CounterStrikeSharp.API.Tests;

// Pure-logic coverage for core fixes that need no running server: gamedata merge order and
// per-file failure isolation, plugin discovery order, admin merge, diagnostics reset, and the
// inotify error classification.
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "css-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Write(string relative, string contents)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}

public class GameDataProviderTests
{
    private static GameDataProvider Load(TempDir dir)
    {
        var config = new Mock<IScriptHostConfiguration>();
        config.SetupGet(c => c.GameDataPath).Returns(dir.Path);
        var provider = new GameDataProvider(config.Object, NullLogger<GameDataProvider>.Instance);
        provider.Load();
        return provider;
    }

    private const string Sig = "{\"signatures\":{\"library\":\"server\",\"linux\":\"{0}\",\"windows\":\"{0}\"}}";

    private static string Entry(string key, string sig) => $"\"{key}\":" + Sig.Replace("{0}", sig);

    [Fact]
    public void MalformedFileIsSkippedAndTheRestStillLoad()
    {
        using var dir = new TempDir();
        dir.Write("gamedata.json", "{" + Entry("Core", "AA") + "}");
        dir.Write("aaa-broken.json", "{ this is not json");
        dir.Write("zzz-plugin.json", "{" + Entry("Plugin", "BB") + "}");

        var provider = Load(dir);

        Assert.Contains("Core", provider.Methods.Keys);
        Assert.Contains("Plugin", provider.Methods.Keys);
        Assert.DoesNotContain("aaa-broken.json", provider.LoadedFiles);
    }

    [Fact]
    public void CoreFileLoadsFirstSoPluginFilesOverrideIt()
    {
        using var dir = new TempDir();
        // "a" sorts before "gamedata", so only the explicit core-first rule puts gamedata.json first.
        dir.Write("a-plugin.json", "{" + Entry("Shared", "PLUGIN") + "}");
        dir.Write("gamedata.json", "{" + Entry("Shared", "CORE") + "}");

        var provider = Load(dir);

        Assert.Equal("gamedata.json", provider.LoadedFiles[0]);
        Assert.Equal("PLUGIN", provider.Methods["Shared"].Signatures!.Linux);
    }

    [Fact]
    public void LaterFileByNameWinsBetweenPluginFiles()
    {
        using var dir = new TempDir();
        dir.Write("gamedata.json", "{}");
        dir.Write("b.json", "{" + Entry("Dup", "B") + "}");
        dir.Write("a.json", "{" + Entry("Dup", "A") + "}");

        var provider = Load(dir);

        Assert.Equal(new[] { "gamedata.json", "a.json", "b.json" }, provider.LoadedFiles);
        Assert.Equal("B", provider.Methods["Dup"].Signatures!.Linux);
    }
}

public class PluginDiscoveryTests
{
    [Fact]
    public void PluginsAreReturnedSortedByName()
    {
        using var dir = new TempDir();
        foreach (var name in new[] { "Zeta", "alpha", "Mid" })
            dir.Write(System.IO.Path.Combine("plugins", name, name + ".dll"), "");
        dir.Write(System.IO.Path.Combine("plugins", "disabled", "Off", "Off.dll"), "");

        var config = new Mock<IScriptHostConfiguration>();
        config.SetupGet(c => c.PluginPath).Returns(System.IO.Path.Combine(dir.Path, "plugins"));
        var manager = new PluginManager(config.Object, null!, NullLogger<PluginManager>.Instance, null!, null!);

        var method = typeof(PluginManager).GetMethod("GetPluginsAssemblyPaths", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var paths = (string[])method.Invoke(manager, null)!;

        Assert.Equal(new[] { "alpha", "Mid", "Zeta" }, paths.Select(System.IO.Path.GetFileNameWithoutExtension));
    }
}

[Collection("Logging collection")]
public class AdminMergeTests
{
    [Fact]
    public void DuplicateIdentityMergesNewDomainsGroupsAndOverrides()
    {
        using var dir = new TempDir();
        var path = dir.Write("admins.json", """
        {
          "first":  { "identity": "76561197960265901", "flags": ["@css/ban"], "immunity": 10 },
          "second": { "identity": "76561197960265901", "flags": ["@custom/vip"], "groups": ["#css/merge-test"],
                      "command_overrides": { "merge_cmd": true }, "immunity": 20 }
        }
        """);

        AdminManager.LoadAdminData(path);

        var steamId = (SteamID)76561197960265901;
        var data = AdminManager.GetPlayerAdminData(steamId);
        Assert.NotNull(data);
        Assert.True(AdminManager.PlayerHasPermissions(steamId, "@css/ban"));
        Assert.True(AdminManager.PlayerHasPermissions(steamId, "@custom/vip"));
        Assert.Contains("#css/merge-test", data!.Groups);
        Assert.True(data.CommandOverrides["merge_cmd"]);
        Assert.Equal(20u, data.Immunity);
    }

    [Fact]
    public void InvalidIdentityIsSkippedWithoutDroppingOthers()
    {
        using var dir = new TempDir();
        var path = dir.Write("admins.json", """
        {
          "typo":  { "identity": "not-a-steamid", "flags": ["@css/root"] },
          "valid": { "identity": "76561197960265902", "flags": ["@css/kick"] }
        }
        """);

        AdminManager.LoadAdminData(path);

        Assert.True(AdminManager.PlayerHasPermissions((SteamID)76561197960265902, "@css/kick"));
    }
}

public class PluginDiagnosticsTests
{
    [Fact]
    public void ResetClearsCountAndThrottleForThatPluginOnly()
    {
        var plugin = "DiagTest" + Guid.NewGuid().ToString("N");
        var other = "DiagOther" + Guid.NewGuid().ToString("N");

        Assert.True(PluginDiagnostics.RecordError(plugin, plugin + "|Handler|Ex").LogFull);
        Assert.False(PluginDiagnostics.RecordError(plugin, plugin + "|Handler|Ex").LogFull);
        PluginDiagnostics.RecordError(other, other + "|Handler|Ex");

        PluginDiagnostics.Reset(plugin);

        Assert.Equal(0, PluginDiagnostics.GetErrorCount(plugin));
        Assert.Equal(1, PluginDiagnostics.GetErrorCount(other));
        // Throttle state is gone too: the next occurrence gets a full report again.
        Assert.True(PluginDiagnostics.RecordError(plugin, plugin + "|Handler|Ex").LogFull);
    }
}

public class InotifyDiagnosticsTests
{
    [Theory]
    [InlineData("The configured user limit (1024) on the number of inotify instances has been reached, or the per-process limit on the number of open file descriptors has been reached.", true, false)]
    [InlineData("The configured user limit (8192) on the number of inotify watches has been reached, or the operating system failed to allocate a required resource.", true, true)]
    [InlineData("The process cannot access the file because it is being used by another process.", false, false)]
    public void ClassifiesRuntimeMessages(string message, bool isLimit, bool isWatches)
    {
        var ex = new IOException(message);
        Assert.Equal(isLimit, InotifyDiagnostics.IsInotifyLimitError(ex));
        Assert.Equal(isWatches, InotifyDiagnostics.IsWatchLimitError(ex));
    }

    [Fact]
    public void NonIOExceptionIsNotAnInotifyError()
    {
        Assert.False(InotifyDiagnostics.IsInotifyLimitError(new InvalidOperationException("inotify")));
    }
}

public class LoadFailureReportTests
{
    private const string MissingFullName = "CSSharpUtils, Version=1.0.9.0, Culture=neutral, PublicKeyToken=null";

    private static Exception MissingDependency() =>
        new System.Reflection.TargetInvocationException(
            new FileNotFoundException($"Could not load file or assembly '{MissingFullName}'. The system cannot find the file specified.",
                MissingFullName));

    private static string Report(TempDir dir, string? depsJson)
    {
        Core.Plugin.PluginContext.CssRootPath = dir.Path;
        var pluginPath = dir.Write("plugins/RapidFireFix/RapidFireFix.dll", "");
        if (depsJson != null) dir.Write("plugins/RapidFireFix/RapidFireFix.deps.json", depsJson);
        return Core.Plugin.PluginContext.BuildLoadFailureReportFromPath(MissingDependency(), pluginPath);
    }

    [Fact]
    public void SummaryUnwrapsReflectionAndNamesTheDependency()
    {
        Assert.Equal("missing dependency CSSharpUtils 1.0.9.0", Core.Plugin.PluginContext.Summarize(MissingDependency()));
    }

    [Fact]
    public void DependencyShippedByThePluginMeansIncompleteUpload()
    {
        using var dir = new TempDir();
        var report = Report(dir, """
        { "targets": { ".NETCoreApp,Version=v8.0": {
            "RapidFireFix/1.0.0": { "runtime": { "RapidFireFix.dll": {} } },
            "CSSharpUtils/1.0.9": { "runtime": { "lib/net8.0/CSSharpUtils.dll": {} } } } } }
        """);

        Assert.Contains("Install problem", report);
        Assert.Contains("own release includes CSSharpUtils.dll 1.0.9", report);
        Assert.Contains("Put CSSharpUtils.dll next to RapidFireFix.dll", report);
        Assert.Contains("File:      plugins/RapidFireFix/RapidFireFix.dll", report);
        Assert.DoesNotContain("Remove this DLL", report);
    }

    [Fact]
    public void DependencyNotShippedMeansSharedFolder()
    {
        using var dir = new TempDir();
        var report = Report(dir, """
        { "targets": { ".NETCoreApp,Version=v8.0": {
            "RapidFireFix/1.0.0": { "runtime": { "RapidFireFix.dll": {} } } } } }
        """);

        Assert.Contains("provided by the server", report);
        Assert.Contains("shared/CSSharpUtils/CSSharpUtils.dll", report);
    }

    [Fact]
    public void NoDepsJsonSaysUnknownAndListsCopiesFound()
    {
        using var dir = new TempDir();
        File.Copy(typeof(FactAttribute).Assembly.Location,
            dir.Write("plugins/OtherPlugin/CSSharpUtils.dll", ""), overwrite: true);

        var report = Report(dir, null);

        Assert.Contains("Expects:   Unknown", report);
        Assert.Contains("Found:     CSSharpUtils.dll", report);
        Assert.Contains("plugins/OtherPlugin/", report);
    }

    [Fact]
    public void PluginCodeFailureBlamesThePluginAndBannerIsAscii()
    {
        using var dir = new TempDir();
        Core.Plugin.PluginContext.CssRootPath = dir.Path;
        Exception thrown;
        try { throw new InvalidOperationException("boom\nsecond line"); }
        catch (Exception e) { thrown = e; }

        var report = Core.Plugin.PluginContext.BuildLoadFailureReportFromPath(thrown, Path.Combine(dir.Path, "plugins/X/X.dll"));

        Assert.Contains("Error:     InvalidOperationException: boom", report);
        Assert.DoesNotContain("second line", report);
        Assert.Contains("the failure is in the plugin's own code", report);
        Assert.All(report, c => Assert.True(c < 128, $"non-ASCII char U+{(int)c:X4} in banner"));
    }
}
