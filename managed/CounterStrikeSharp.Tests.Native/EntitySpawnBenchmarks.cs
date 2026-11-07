using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using Xunit;

namespace NativeTestsPlugin;

/// <summary>
/// The "create 1024 entities" scenario other frameworks publish numbers for, split into stages so the
/// framework's share can be told apart from the engine's: each framework stage has a "raw" twin that
/// calls the same server function through a bare function pointer, with no CSSharp layer in between.
/// Run with <c>css_itest EntitySpawnBenchmarks</c>; results land in benchmark-results.md next to the plugin.
/// </summary>
[Trait("Category", "Benchmark")]
public class EntitySpawnBenchmarks
{
    private const int Count = 1024;
    private const int Rounds = 6; // round 0 is the cold round (first use: signature scan, JIT) and is reported apart
    // Same entity as swiftly-solution/profiler's CSSharp scenarios.
    private const string DesignerName = "point_worldtext";

    // Resolved inside the raw test, not in a static initializer, so they cannot warm the
    // framework test's cold round.
    private static IntPtr RawCreateAddress;
    private static IntPtr RawSpawnAddress;

    private static readonly byte[] DesignerNameUtf8 = System.Text.Encoding.UTF8.GetBytes(DesignerName + "\0");

    [Fact]
    public async Task Benchmark_Entity_Framework()
    {
        var create = new List<double>();
        var spawn = new List<double>();
        var teleport = new List<double>();
        var write = new List<double>();
        var remove = new List<double>();
        var entities = new CPointWorldText[Count];
        var position = new Vector3(0, 0, 64);

        for (var round = 0; round < Rounds; round++)
        {
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < Count; i++)
                entities[i] = Utilities.CreateEntityByName<CPointWorldText>(DesignerName)!;
            var tCreate = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            for (var i = 0; i < Count; i++)
                entities[i].DispatchSpawn();
            var tSpawn = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            for (var i = 0; i < Count; i++)
                entities[i].Teleport(position, null, null);
            var tTeleport = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            for (var i = 0; i < Count; i++)
            {
                entities[i].Health = 50;
                Utilities.SetStateChanged(entities[i], "CBaseEntity", "m_iHealth");
            }
            var tWrite = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            for (var i = 0; i < Count; i++)
                entities[i].Remove();
            var tRemove = sw.Elapsed.TotalMilliseconds;

            // Removal is deferred to the end of the frame; let it run before the next round.
            await Server.NextFrameAsync(() => { });
            await Server.NextFrameAsync(() => { });

            if (round == 0)
            {
                Console.WriteLine($"[EntitySpawnBenchmarks] cold round x{Count}: create {tCreate:F3} ms, spawn {tSpawn:F3} ms, " +
                                  $"teleport {tTeleport:F3} ms, write {tWrite:F3} ms, remove {tRemove:F3} ms");
                ScriptContextBenchmarks.RecordMs($"cold CreateEntityByName<T> x{Count} (first use)", "Entity Spawn", tCreate, Count);
                continue;
            }
            create.Add(tCreate);
            spawn.Add(tSpawn);
            teleport.Add(tTeleport);
            write.Add(tWrite);
            remove.Add(tRemove);
        }

        Report("CreateEntityByName<T>", create);
        Report("DispatchSpawn()", spawn);
        Report("Teleport(Vector3)", teleport);
        Report("Health write + SetStateChanged", write);
        Report("Remove()", remove);
        Report("Total create+spawn", create.Zip(spawn, (a, b) => a + b).ToList());
    }

    [Fact]
    public async Task Benchmark_Entity_Raw()
    {
        RawCreateAddress = NativeAPI.FindSignature(Addresses.ServerPath, GameData.GetSignature("UTIL_CreateEntityByName"));
        RawSpawnAddress = NativeAPI.FindSignature(Addresses.ServerPath, GameData.GetSignature("CBaseEntity_DispatchSpawn"));
        Assert.True(RawCreateAddress != IntPtr.Zero && RawSpawnAddress != IntPtr.Zero, "signatures did not resolve");

        var create = new List<double>();
        var spawn = new List<double>();
        var handles = new IntPtr[Count];

        for (var round = 0; round < Rounds; round++)
        {
            var tCreate = RawCreateAll(handles);
            var tSpawn = RawSpawnAll(handles);

            foreach (var h in handles)
                new CBaseEntity(h).Remove();
            await Server.NextFrameAsync(() => { });
            await Server.NextFrameAsync(() => { });

            if (round == 0) continue;
            create.Add(tCreate);
            spawn.Add(tSpawn);
        }

        Report("raw UTIL_CreateEntityByName", create);
        Report("raw DispatchSpawn", spawn);
        Report("raw Total create+spawn", create.Zip(spawn, (a, b) => a + b).ToList());
    }

    private static unsafe double RawCreateAll(IntPtr[] handles)
    {
        var create = (delegate* unmanaged<byte*, int, IntPtr>)RawCreateAddress;
        var sw = Stopwatch.StartNew();
        fixed (byte* name = DesignerNameUtf8)
        {
            for (var i = 0; i < handles.Length; i++)
                handles[i] = create(name, -1);
        }
        return sw.Elapsed.TotalMilliseconds;
    }

    private static unsafe double RawSpawnAll(IntPtr[] handles)
    {
        var spawn = (delegate* unmanaged<IntPtr, IntPtr, void>)RawSpawnAddress;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < handles.Length; i++)
            spawn(handles[i], IntPtr.Zero);
        return sw.Elapsed.TotalMilliseconds;
    }

    private static void Report(string name, List<double> msPerRound)
    {
        msPerRound.Sort();
        var median = msPerRound[msPerRound.Count / 2];
        Console.WriteLine($"[EntitySpawnBenchmarks] {name,-34} x{Count}: median {median,8:F3} ms  " +
                          $"(min {msPerRound[0]:F3}, max {msPerRound[^1]:F3})  {median * 1e6 / Count,9:F0} ns/entity");
        ScriptContextBenchmarks.RecordMs($"{name} x{Count} (median of {msPerRound.Count})", "Entity Spawn", median, Count);
    }
}
