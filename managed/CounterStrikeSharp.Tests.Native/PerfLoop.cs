using System;
using System.Diagnostics;
using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace NativeTestsPlugin;

/// <summary>
/// Repeats swiftly-solution/profiler's one-shot entity scenarios (same loop bodies as its
/// CSSharpProfiler) for a few seconds on the game thread, so `perf record` gets enough samples to
/// split a row into framework and engine cost. The profiler runs each body once (~1 ms).
///   css_perfloop write|virtual|read [seconds=5]
/// Creates 1024 point_worldtext once, runs passes until the time is up, removes them, prints
/// PERFLOOP <scenario> passes=N us/pass=X.
/// </summary>
public static class PerfLoop
{
    private const int Count = 1024;

    public static void Register(BasePlugin plugin)
    {
        plugin.AddCommand("css_perfloop", "Repeat a profiler scenario: write|virtual|read [seconds]", (_, info) =>
        {
            var scenario = info.ArgCount > 1 ? info.GetArg(1) : "";
            var seconds = info.ArgCount > 2 && double.TryParse(info.GetArg(2), out var s) ? s : 5;
            Action<CBaseEntity[]>? body = scenario switch
            {
                "write" => Write,
                "virtual" => Virtual,
                "read" => Read,
                _ => null,
            };
            if (body == null) { Console.WriteLine("PERFLOOP usage: css_perfloop write|virtual|read [seconds]"); return; }

            var entities = new CBaseEntity[Count];
            for (var i = 0; i < Count; i++)
                entities[i] = Utilities.CreateEntityByName<CPointWorldText>("point_worldtext")!;
            foreach (var e in entities) e.DispatchSpawn();

            var passes = 0;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                body(entities);
                passes++;
            }
            var us = sw.Elapsed.TotalMicroseconds / passes;

            foreach (var e in entities)
                if (e.IsValid) e.Remove();
            Console.WriteLine(FormattableString.Invariant($"PERFLOOP {scenario} passes={passes} us/pass={us:F1}"));
        });
    }

    // Bodies copied from the profiler's CSSharpProfiler/Entitites.cs (loop shape and API calls).
    private static void Write(CBaseEntity[] entities)
    {
        var index = 0;
        foreach (var entity in entities)
        {
            var textEntity = entity.As<CPointWorldText>();
            textEntity.MessageText = $"Performance Test Entity #{index}{Random.Shared.NextInt64()}";
            Utilities.SetStateChanged(textEntity, "CPointWorldText", "m_messageText");
            textEntity.Color = Color.FromArgb(255, 255, 0, 0);
            Utilities.SetStateChanged(textEntity, "CPointWorldText", "m_Color");
            index++;
        }
    }

    private static void Virtual(CBaseEntity[] entities)
    {
        var index = 0;
        foreach (var entity in entities)
        {
            var textEntity = entity.As<CPointWorldText>();
            textEntity.Teleport(new Vector(0, index * 10, 0), null, null);
            index++;
        }
    }

    private static void Read(CBaseEntity[] entities)
    {
        foreach (var entity in entities)
        {
            var textEntity = entity.As<CPointWorldText>();
            var message = textEntity.MessageText;
            var color = textEntity.Color;
            var enabled = textEntity.Enabled;
            var testColor = Color.FromArgb(255, Math.Clamp(textEntity.Color.R * 2, 0, 255), Math.Clamp(textEntity.Color.G * 2, 0, 255), Math.Clamp(textEntity.Color.B * 2, 0, 255));
            GC.KeepAlive(message);
        }
    }
}
