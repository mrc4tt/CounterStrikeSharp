using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using CounterStrikeSharp.API.Core.Plugin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace CounterStrikeSharp.API.Core.Diagnostics;

/// <summary>
/// The <c>css_memstats</c> server command: counters that tell whether memory growth on a live server
/// is timers, callbacks, something else managed, an unloaded plugin that is still referenced, or
/// native/engine. Every line starts with MEMSTATS and is key=value, so a script can grep and diff
/// two runs. Plain <c>css_memstats</c> only reads counters; <c>css_memstats gc</c> collects first.
/// </summary>
internal static class MemStats
{
    // One entry per plugin load. AssemblyLoadContext.All drops a context when its unload starts, so
    // a context that is unloaded but still referenced (the leak) is only visible through a weak
    // reference taken while it was loaded. Game thread only.
    private static readonly List<(string Name, WeakReference Context)> LoadContexts = new();

    internal static void TrackLoadContext(Assembly pluginAssembly)
    {
        var context = AssemblyLoadContext.GetLoadContext(pluginAssembly);
        if (context == null || context == AssemblyLoadContext.Default) return;

        LoadContexts.RemoveAll(entry => !entry.Context.IsAlive);
        foreach (var entry in LoadContexts)
            if (ReferenceEquals(entry.Context.Target, context)) return;

        LoadContexts.Add((PluginDiagnostics.SafeAssemblyName(pluginAssembly), new WeakReference(context)));
    }

    internal static void Print(CommandInfo info, IEnumerable<PluginContext> plugins)
    {
        var collect = info.ArgCount > 1 && info.GetArg(1).Equals("gc", StringComparison.OrdinalIgnoreCase);
        if (collect)
        {
            // A collectible load context needs more than one pass: finalizers have to run in between.
            for (var i = 0; i < 4; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        var perOwner = new Dictionary<string, (int Permanent, int SingleUse)>();
        var references = FunctionReference.GetMapStats(perOwner);
        var liveContexts = new HashSet<AssemblyLoadContext>();

        foreach (var context in plugins)
        {
            if (context.Plugin is not BasePlugin plugin) continue;

            var assembly = plugin.GetType().Assembly;
            if (AssemblyLoadContext.GetLoadContext(assembly) is { } loadContext) liveContexts.Add(loadContext);

            var owner = PluginDiagnostics.SafeAssemblyName(assembly);
            perOwner.Remove(owner, out var owned);
            var counts = plugin.GetRegistrationCounts();
            var hooks = BaseMemoryFunction.CountHooks(assembly).ForAssembly;

            Line(info, "plugin",
                ("id", context.PluginId), ("name", owner),
                // timers: made with AddTimer and not finished. timers_all adds `new Timer(...)`.
                ("timers", counts.Timers), ("timers_all", Timer.CountLiveForAssembly(assembly)),
                ("handlers", counts.Handlers), ("listeners", counts.Listeners),
                ("command_listeners", counts.CommandListeners),
                ("entity_output_hooks", counts.EntityOutputHooks + counts.SingleEntityOutputHooks),
                ("memory_hooks", hooks), ("commands", counts.Commands), ("convars", counts.ConVars),
                ("references", owned.Permanent + owned.SingleUse), ("references_single_use", owned.SingleUse));
        }

        // References whose owner is not a loaded plugin: core's own, or left behind by an unload.
        foreach (var (owner, owned) in perOwner)
        {
            Line(info, "references_other", ("owner", owner), ("references", owned.Permanent + owned.SingleUse),
                ("references_single_use", owned.SingleUse));
        }

        var timers = Timer.LiveCounts;
        var tasks = Server.PendingTaskCounts;
        Line(info, "managed",
            ("timers", timers.Live), ("timers_stop_on_mapchange", timers.StopOnMapChange),
            ("references", references.References), ("references_permanent", references.Permanent),
            ("references_single_use", references.SingleUse), ("references_by_delegate", references.IndexedByDelegate),
            ("rooted_stubs", references.RootedStubs), ("fired_stub_window", references.FiredStubWindow),
            ("removed_owners", references.RemovedOwners), ("dead_callbacks_logged", references.DeadCallbacksLogged),
            ("compiled_invokers", references.CompiledInvokers),
            ("next_frame_pending", tasks.NextFrame), ("next_world_update_pending", tasks.NextWorldUpdate),
            ("memory_hooks", BaseMemoryFunction.CountHooks(null).Total),
            ("owned_native_buffers", OwnedNativeBlock.LiveCount));

        PrintLoadContexts(info, liveContexts, collect);
        PrintNative(info);
        PrintMemory(info, collect);
    }

    private static void PrintLoadContexts(CommandInfo info, HashSet<AssemblyLoadContext> liveContexts, bool collected)
    {
        LoadContexts.RemoveAll(entry => !entry.Context.IsAlive);

        var stale = new Dictionary<string, int>();
        var staleCount = 0;
        foreach (var entry in LoadContexts)
        {
            if (entry.Context.Target is not AssemblyLoadContext context || liveContexts.Contains(context)) continue;

            staleCount++;
            stale.TryGetValue(entry.Name, out var count);
            stale[entry.Name] = count + 1;
        }

        // Without a collection first an unloaded context is alive simply because no GC ran yet.
        Line(info, "contexts", ("alive", LoadContexts.Count), ("loaded", liveContexts.Count),
            ("unloaded_alive", staleCount), ("after_gc", collected ? 1 : 0));
        foreach (var (name, count) in stale)
            Line(info, "context_unloaded_alive", ("name", name), ("count", count));
    }

    private static void PrintNative(CommandInfo info)
    {
        try
        {
            Line(info, "native",
                ("timers_once", NativeAPI.GetNativeStat(0)), ("timers_repeat", NativeAPI.GetNativeStat(1)),
                ("callbacks", NativeAPI.GetNativeStat(2)), ("callback_listeners", NativeAPI.GetNativeStat(3)),
                ("valve_functions", NativeAPI.GetNativeStat(4)), ("game_events", NativeAPI.GetNativeStat(5)));
        }
        catch (NativeException)
        {
            // Core older than this API: the native does not exist there.
            Line(info, "native", ("available", 0));
        }
    }

    private static void PrintMemory(CommandInfo info, bool collected)
    {
        var gc = GC.GetGCMemoryInfo();
        var generations = gc.GenerationInfo.ToArray();
        long Generation(int index) => index < generations.Length ? generations[index].SizeAfterBytes / 1024 : -1;

        Line(info, "gc",
            ("after_gc", collected ? 1 : 0),
            ("total_kb", GC.GetTotalMemory(false) / 1024), ("heap_kb", gc.HeapSizeBytes / 1024),
            ("committed_kb", gc.TotalCommittedBytes / 1024), ("fragmented_kb", gc.FragmentedBytes / 1024),
            // Sizes as of the last collection of each generation.
            ("gen0_kb", Generation(0)), ("gen1_kb", Generation(1)), ("gen2_kb", Generation(2)),
            ("loh_kb", Generation(3)), ("poh_kb", Generation(4)),
            ("collections_gen0", GC.CollectionCount(0)), ("collections_gen1", GC.CollectionCount(1)),
            ("collections_gen2", GC.CollectionCount(2)),
            ("allocated_total_mb", GC.GetTotalAllocatedBytes() / (1024 * 1024)));

        var process = ReadProcessStatus();
        Line(info, "process",
            ("rss_kb", process.Rss), ("rss_anon_kb", process.RssAnon), ("rss_peak_kb", process.RssPeak),
            ("threads", process.Threads), ("uptime_s", UptimeSeconds()));
    }

    private static long UptimeSeconds()
    {
        try
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            return (long)(DateTime.UtcNow - self.StartTime.ToUniversalTime()).TotalSeconds;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static (long Rss, long RssAnon, long RssPeak, long Threads) ReadProcessStatus()
    {
        long rss = -1, anon = -1, peak = -1, threads = -1;
        try
        {
            if (!File.Exists("/proc/self/status"))
            {
                using var self = System.Diagnostics.Process.GetCurrentProcess();
                return (self.WorkingSet64 / 1024, -1, self.PeakWorkingSet64 / 1024, self.Threads.Count);
            }

            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("VmRSS:")) rss = Field(line);
                else if (line.StartsWith("RssAnon:")) anon = Field(line);
                else if (line.StartsWith("VmHWM:")) peak = Field(line);
                else if (line.StartsWith("Threads:")) threads = Field(line);
            }
        }
        catch (Exception)
        {
            // Diagnostics only: unreadable numbers stay -1.
        }

        return (rss, anon, peak, threads);

        static long Field(string line)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : -1;
        }
    }

    private static void Line(CommandInfo info, string section, params (string Key, object Value)[] fields)
    {
        var builder = new System.Text.StringBuilder("MEMSTATS ").Append(section);
        foreach (var (key, value) in fields)
        {
            builder.Append(' ').Append(key).Append('=');
            // Names can contain spaces ("[CS2] Foo"); keep one token per field.
            builder.Append(value is string text
                ? text.Replace(' ', '_')
                : Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        info.ReplyToCommand(builder.ToString());
    }
}
