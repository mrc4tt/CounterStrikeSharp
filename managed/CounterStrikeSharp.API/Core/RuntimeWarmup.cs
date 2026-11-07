using System;
using System.Diagnostics;
using System.Linq;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace CounterStrikeSharp.API.Core;

/// <summary>
/// Runs the API's hot paths a few dozen times once the server is ticking, so a plugin's first pass
/// through them (its first round, its first command, the profiler's one-shot rows) runs optimized
/// code instead of paying for JIT in the middle of a frame.
/// </summary>
/// <remarks>
/// Two first-use costs, measured with NativeTestsPlugin's css_jit probe on a plugin's first schema
/// write/read of 1024 entities:
/// <list type="bullet">
/// <item>Compilation: runtime library code (UTF-8 transcoding, number formatting, span helpers) is
/// JIT-compiled at first use on CPUs whose vector width differs from the precompiled code's
/// (AVX-512 servers), and every API method is compiled the first time it is called.</item>
/// <item>Tier-0 code: a method is first compiled without optimization and only recompiled
/// optimized after ~30 calls and a quiet period, so a first loop over 1024 entities runs entirely
/// on unoptimized code.</item>
/// </list>
/// Calling each path more than 30 times here triggers both at load, and tiered compilation then
/// replaces them with optimized code on its background thread before gameplay.
/// <para>
/// Only harmless work: strings and colours are written to a private scratch buffer, entity access
/// reads the world entity and the game rules, Teleport is called without arguments (it throws
/// before reaching the engine), schema lookups are pure, and the one engine call is a
/// state-change notification for the world's (unchanged) health field.
/// Runs on the game thread (entity access and schema lookups call natives that must run there).
/// </para>
/// </remarks>
internal static class RuntimeWarmup
{
    private const int Calls = 64; // above the tiered-compilation call-count threshold (30)

    public static void Run(ILogger logger)
    {
        var sw = Stopwatch.StartNew();
        var scratch = Marshal.AllocHGlobal(1024);
        try
        {
            Strings(scratch);
            Colors(scratch);
            Vectors();
            Entities();
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Runtime warmup stopped early");
        }
        finally
        {
            Marshal.FreeHGlobal(scratch);
        }

        logger.LogInformation("Runtime warmup done in {Elapsed:F1} ms", sw.Elapsed.TotalMilliseconds);
    }

    private static void Strings(IntPtr scratch)
    {
        for (var i = 0; i < Calls; i++)
        {
            // ASCII and non-ASCII, with interpolated integers and floats: the transcoding and
            // formatting paths a schema string read/write and typical plugin text go through.
            Schema.SetStringBytesAt(scratch, 0, $"Warmup #{i}{(long)i * 7919}", 512);
            _ = Schema.GetStringAt(scratch, 0);
            Schema.SetStringBytesAt(scratch, 0, $"Wärmüp ✓ {i * 0.5f} {i * 0.25:F2}", 512);
            _ = Schema.GetStringAt(scratch, 0);
            Schema.GetFixedArrayAt<byte>(scratch, 0, 8).Clear();
        }
    }

    private static void Colors(IntPtr scratch)
    {
        for (var i = 0; i < Calls; i++)
        {
            Schema.SetColorAt(scratch, 600, Color.FromArgb(255, i, 0, 0));
            var c = Schema.GetColorAt(scratch, 600);
            _ = Color.FromArgb(255, Math.Clamp(c.R * 2, 0, 255), c.G, c.B);
        }
    }

    private static void Vectors()
    {
        for (var i = 0; i < Calls; i++)
        {
            var v = new Modules.Utils.Vector(0, i * 10, 0);
            _ = (Vector3)v;
            var a = new QAngle(0, i, 0);
            _ = (Vector3)a;
        }
    }

    private static void Entities()
    {
        // The world entity always exists while a map is loaded; only read from it.
        var world = Utilities.GetEntityFromIndex<CBaseEntity>(0);
        if (world == null) return;

        for (var i = 0; i < Calls; i++)
        {
            _ = world.Handle;
            _ = world.IsValid;
            var asEntity = world.As<CBaseEntity>();
            _ = asEntity.LastNetworkChange;
            _ = asEntity.IsSteadyState;
            // Pure lookup (offset, chain offset, networked) for the notify path; does not notify.
            _ = Schema.TryGetStateChangedTarget("CBaseEntity", "m_iHealth", out _, out _);
        }

        // Teleport's managed side (validity, argument handling) without moving anything: with no
        // argument it throws before the engine call. 64 caught exceptions, once, at load.
        for (var i = 0; i < Calls; i++)
        {
            try { world.Teleport((Vector3?)null, null, null); } catch (ArgumentException) { }
            try { world.Teleport((Modules.Utils.Vector?)null, null, null); } catch (ArgumentException) { }
        }

        // The usual game rules idiom: entity list walk by designer name, then a pointer field.
        for (var i = 0; i < Calls; i++)
            _ = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;

        // The notify path itself (SetStateChanged -> engine). Marks the world's unchanged health
        // field as changed: at worst one redundant delta of an unchanged value, and at load there
        // are normally no clients to send it to.
        for (var i = 0; i < Calls; i++)
            Utilities.SetStateChanged(world, "CBaseEntity", "m_iHealth");
    }
}
