using System;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.UserMessages;

namespace NativeTestsPlugin;

/// <summary>
/// Console commands for measuring leaks on a live server. Output lines start with LEAKPROBE so a
/// script can grep them out of the console log.
///   css_leak_stats              RSS, managed heap and the AssemblyLoadContexts still holding this plugin
///   css_leak_usermsg N [batch]  create, fill and dispose N user messages, batch per frame
///   css_leak_frames [reset]     frame interval/work percentiles and GC pauses (see FrameProbe)
/// </summary>
public static class LeakProbe
{
    // AssemblyLoadContext.All drops a context as soon as its unload starts, so a context that is
    // unloaded but still rooted (the leak) is invisible there. Every load records a weak reference
    // to its own context in AppDomain data, which outlives the plugin and roots nothing of it.
    private const string ContextsKey = "NativeTestsPlugin.LeakProbe.Contexts";

    private static System.Collections.Generic.List<WeakReference> LoadedContexts()
    {
        if (AppDomain.CurrentDomain.GetData(ContextsKey) is not System.Collections.Generic.List<WeakReference> list)
        {
            list = new System.Collections.Generic.List<WeakReference>();
            AppDomain.CurrentDomain.SetData(ContextsKey, list);
        }
        return list;
    }

    public static void Register(BasePlugin plugin)
    {
        LoadedContexts().Add(new WeakReference(AssemblyLoadContext.GetLoadContext(typeof(LeakProbe).Assembly)));
        FrameProbe.Register(plugin);

        plugin.AddCommand("css_leak_stats", "Print memory and load-context stats.", (_, _) => { var t = Stats(); });
        plugin.AddCommand("css_leak_usermsg", "Stress user message create/delete.", (caller, info) =>
        {
            var total = info.ArgCount > 1 && int.TryParse(info.GetArg(1), out var n) ? n : 100_000;
            var batch = info.ArgCount > 2 && int.TryParse(info.GetArg(2), out var b) ? b : 1000;
            _ = UserMessageStress(total, batch);
        });
    }

    private static long RssKb()
    {
        foreach (var line in File.ReadLines("/proc/self/status"))
            if (line.StartsWith("VmRSS:"))
                return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
        return -1;
    }

    private static async Task Stats()
    {
        // A collectible context needs several GCs (and finalizers in between) before it disappears.
        for (var i = 0; i < 10; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await TestUtils.WaitOneFrame();
        }

        var loaded = LoadedContexts();
        var contexts = loaded.Count(w => w.IsAlive);
        Server.PrintToConsole(
            $"LEAKPROBE stats rss_kb={RssKb()} managed_kb={GC.GetTotalMemory(true) / 1024} " +
            $"plugin_loads={loaded.Count} plugin_alcs_alive={contexts}");

        // The forced collections above are this command's own slow frames; keep them out of the
        // next frame window.
        FrameProbe.Reset();
    }

    private static async Task UserMessageStress(int total, int batch)
    {
        var text = new string('x', 256);
        var before = RssKb();
        for (var done = 0; done < total; done += batch)
        {
            for (var i = 0; i < batch; i++)
            {
                using var message = UserMessage.FromPartialName("HintText");
                message.SetString("message", text);
            }

            // Dispose defers the native delete to the next frame.
            await TestUtils.WaitOneFrame();
        }

        await TestUtils.WaitFrames(2);
        Server.PrintToConsole($"LEAKPROBE usermsg n={total} rss_before_kb={before} rss_after_kb={RssKb()} delta_kb={RssKb() - before}");
    }
}
