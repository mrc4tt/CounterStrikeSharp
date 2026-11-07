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
            if (scenario == "spawngrow")
            {
                var task = SpawnGrow(info.ArgCount > 2 && int.TryParse(info.GetArg(2), out var gr) ? gr : 20,
                    info.ArgCount > 3 && int.TryParse(info.GetArg(3), out var gw) ? gw : 2,
                    info.ArgCount > 4 && info.GetArg(4) == "framework",
                    info.ArgCount > 5 ? info.GetArg(5) : "point_worldtext");
                return;
            }
            if (scenario == "spawnab") { var task = SpawnAb(info.ArgCount > 2 && int.TryParse(info.GetArg(2), out var r) ? r : 20, info.ArgCount > 3 && info.GetArg(3) == "swap"); return; }
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

    // Spawn A/B: each round creates 1024 entities, then spawns them interleaved, even indices through
    // CBaseEntity.DispatchSpawn() and odd ones through a bare function pointer to the same server
    // function, timing each call. Interleaving cancels order and cache effects between the two;
    // "swap" flips which half goes through the framework, to catch a cost tied to the position.
    private static async System.Threading.Tasks.Task SpawnAb(int rounds, bool swap)
    {
        var spawnAddress = CounterStrikeSharp.API.Core.NativeAPI.FindSignature(
            CounterStrikeSharp.API.Modules.Memory.Addresses.ServerPath,
            CounterStrikeSharp.API.Core.GameData.GetSignature("CBaseEntity_DispatchSpawn"));
        double framework = 0, raw = 0;
        var ticksToUs = 1_000_000.0 / Stopwatch.Frequency;
        for (var round = 0; round < rounds; round++)
        {
            var entities = new CPointWorldText[Count];
            for (var i = 0; i < Count; i++)
                entities[i] = Utilities.CreateEntityByName<CPointWorldText>("point_worldtext")!;
            double f = 0, r = 0;
            for (var i = 0; i < Count; i++)
            {
                var useFramework = ((i & 1) == 0) != swap;
                var t0 = Stopwatch.GetTimestamp();
                if (useFramework) entities[i].DispatchSpawn();
                else RawSpawn(spawnAddress, entities[i].RawHandle);
                var dt = (Stopwatch.GetTimestamp() - t0) * ticksToUs;
                if (useFramework) f += dt; else r += dt;
            }
            foreach (var e in entities)
                if (e.IsValid) e.Remove();
            await Server.NextFrameAsync(() => { });
            await Server.NextFrameAsync(() => { });
            if (round == 0) continue; // cold
            framework += f; raw += r;
        }
        var n = (rounds - 1) * (Count / 2);
        Console.WriteLine(FormattableString.Invariant(
            $"PERFLOOP spawnab{(swap ? " swap" : "")} rounds={rounds - 1} framework={framework * 1000 / n:F0} ns/entity raw={raw * 1000 / n:F0} ns/entity"));
    }

    // Leak hunt: per round, create 1024 point_worldtext and spawn them in creation order (raw
    // function pointers, or the framework API with "framework"), time even and odd creation
    // positions apart, then remove them and wait `waitFrames` frames. Prints one line per round
    // (designer name optional, default point_worldtext) with the entity-slot picture: how many of this round's slots were used by an earlier round,
    // how many entities are active before create and after the wait.
    private static async System.Threading.Tasks.Task SpawnGrow(int rounds, int waitFrames, bool framework, string designerName)
    {
        var createAddress = CounterStrikeSharp.API.Core.NativeAPI.FindSignature(
            CounterStrikeSharp.API.Modules.Memory.Addresses.ServerPath,
            CounterStrikeSharp.API.Core.GameData.GetSignature("UTIL_CreateEntityByName"));
        var spawnAddress = CounterStrikeSharp.API.Core.NativeAPI.FindSignature(
            CounterStrikeSharp.API.Modules.Memory.Addresses.ServerPath,
            CounterStrikeSharp.API.Core.GameData.GetSignature("CBaseEntity_DispatchSpawn"));
        var name = System.Text.Encoding.UTF8.GetBytes(designerName + "\0");
        var seen = new System.Collections.Generic.HashSet<uint>();
        var ticksToUs = 1_000_000.0 / Stopwatch.Frequency;

        for (var round = 0; round < rounds; round++)
        {
            var activeBefore = CountActive();
            var pointers = new IntPtr[Count];
            var cs = Stopwatch.StartNew();
            for (var i = 0; i < Count; i++)
                pointers[i] = framework
                    ? Utilities.CreateEntityByName<CBaseEntity>(designerName)!.RawHandle
                    : RawCreate(createAddress, name);
            var createMs = cs.Elapsed.TotalMilliseconds;

            double even = 0, odd = 0;
            for (var i = 0; i < Count; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                if (framework) new CBaseEntity(pointers[i]).DispatchSpawn();
                else RawSpawn(spawnAddress, pointers[i]);
                var dt = (Stopwatch.GetTimestamp() - t0) * ticksToUs;
                if ((i & 1) == 0) even += dt; else odd += dt;
            }

            int reused = 0, minIndex = int.MaxValue, maxIndex = 0;
            foreach (var pointer in pointers)
            {
                var entity = new CBaseEntity(pointer);
                var index = entity.Index;
                if (!seen.Add(index)) reused++;
                minIndex = Math.Min(minIndex, (int)index);
                maxIndex = Math.Max(maxIndex, (int)index);
                if (entity.IsValid) entity.Remove();
            }

            for (var f = 0; f < waitFrames; f++)
                await Server.NextFrameAsync(() => { });

            Console.WriteLine(FormattableString.Invariant(
                $"PERFLOOP spawngrow {designerName} round={round} create={createMs:F2}ms spawnEven={even * 2 / Count:F1}us spawnOdd={odd * 2 / Count:F1}us reused={reused} index={minIndex}-{maxIndex} activeBefore={activeBefore} activeAfterWait={CountActive()}"));
        }
    }

    private static int CountActive()
    {
        var n = 0;
        foreach (var _ in Utilities.GetAllEntities()) n++;
        return n;
    }

    private static unsafe IntPtr RawCreate(IntPtr address, byte[] name)
    {
        fixed (byte* p = name)
            return ((delegate* unmanaged<byte*, int, IntPtr>)address)(p, -1);
    }

    private static unsafe void RawSpawn(IntPtr address, IntPtr entity)
    {
        ((delegate* unmanaged<IntPtr, IntPtr, void>)address)(entity, IntPtr.Zero);
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
