//
// Created by Michal Přikryl on 10.10.2025.
// Copyright (c) 2025 slynxcz. All rights reserved.
//

using System.Buffers;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using RayTraceAPI;
using CoreTrace = CounterStrikeSharp.API.Modules.Utils.Trace;
using CoreTraceOptions = CounterStrikeSharp.API.Modules.Utils.TraceOptions;
using CoreTraceResult = CounterStrikeSharp.API.Modules.Utils.TraceResult;
using TraceOptions = RayTraceAPI.TraceOptions;
using TraceResult = RayTraceAPI.TraceResult;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace RayTraceImpl;

// The native RayTrace Metamod plugin is no longer required: CounterStrikeSharp ships the same
// CNavPhysicsInterface traces in core (CounterStrikeSharp.API.Modules.Utils.Trace). This plugin
// only keeps the "raytrace:craytraceinterface" capability alive for existing consumers and
// forwards every call to the core API.

// ReSharper disable once InconsistentNaming
// ReSharper disable once UnusedType.Global
public class RayTraceImpl : BasePlugin
{
    public override string ModuleName => "RayTraceImpl";
    public override string ModuleVersion => "v1.1.0";
    public override string ModuleAuthor => "Slynx";

    internal static PluginCapability<CRayTraceInterface> RayTraceInterface { get; } =
        new("raytrace:craytraceinterface");

    public override void Load(bool hotReload)
    {
        Capabilities.RegisterPluginCapability(RayTraceInterface, () => new CRayTrace());
        Prints.ServerLog("[RayTraceImpl] Using CounterStrikeSharp core Trace API.", ConsoleColor.Green);
    }
}

public class CRayTrace : CRayTraceInterface
{
    private static bool _warnedTraceShapeEx;

    public bool TraceShape(Vector start, QAngle angles, CEntityInstance? ignore, TraceOptions options, out TraceResult result)
    {
        var trace = CoreTrace.TraceShape(start, angles, ToBaseEntity(ignore), ToCoreOptions(options));
        result = ToApiResult(trace);
        DrawBeamIfRequested(options, start, trace);
        return true;
    }

    public bool TraceEndShape(Vector start, Vector end, CEntityInstance? ignore, TraceOptions options, out TraceResult result)
    {
        var trace = CoreTrace.TraceEndShape(start, end, ToBaseEntity(ignore), ToCoreOptions(options));
        result = ToApiResult(trace);
        DrawBeamIfRequested(options, start, trace);
        return true;
    }

    public bool TraceHullShape(Vector start, Vector end, Vector mins, Vector maxs, CEntityInstance? ignore, TraceOptions options, out TraceResult result)
    {
        var trace = CoreTrace.TraceHullShape(start, end, mins, maxs, ToBaseEntity(ignore), ToCoreOptions(options));
        result = ToApiResult(trace);
        DrawBeamIfRequested(options, start, trace);
        return true;
    }

    // The core API builds its own CTraceFilter / Ray_t and has no entry point that accepts raw
    // native ones, so this overload cannot be forwarded.
    public bool TraceShapeEx(Vector start, Vector end, nint filter, nint ray, out TraceResult result)
    {
        result = new TraceResult { Fraction = 1.0f };

        if (!_warnedTraceShapeEx)
        {
            _warnedTraceShapeEx = true;
            Prints.ServerLog("[RayTraceImpl] TraceShapeEx is not supported by the core Trace API; returning false.", ConsoleColor.Yellow);
        }

        return false;
    }

    private static CBaseEntity? ToBaseEntity(CEntityInstance? entity)
        => entity is null || entity.Handle == nint.Zero ? null : new CBaseEntity(entity.Handle);

    // InteractsAs stays 0, as in the native plugin's CTraceFilterEx.
    private static CoreTraceOptions ToCoreOptions(TraceOptions options) => new()
    {
        InteractsAs = 0,
        InteractsWith = (Contents)options.InteractsWith,
        InteractsExclude = (Contents)options.InteractsExclude,
    };

    private static TraceResult ToApiResult(CoreTraceResult trace)
    {
        var endPos = trace.EndPos;
        var normal = trace.Normal;

        return new TraceResult
        {
            EndPosX = endPos.X,
            EndPosY = endPos.Y,
            EndPosZ = endPos.Z,
            HitEntity = trace.HitEntity().Handle,
            Fraction = trace.Fraction,
            AllSolid = trace.IsAllSolid ? 1 : 0,
            NormalX = normal.X,
            NormalY = normal.Y,
            NormalZ = normal.Z,
        };
    }

    private static void DrawBeamIfRequested(TraceOptions options, Vector start, CoreTraceResult trace)
    {
        if (options.DrawBeam == 0)
            return;

        var beam = Utilities.CreateEntityByName<CBeam>("env_beam");
        if (beam is null)
            return;

        beam.Render = System.Drawing.Color.Red;
        beam.Width = 1.5f;
        beam.RenderMode = RenderMode_t.kRenderNormal;
        beam.RenderFX = RenderFx_t.kRenderFxNone;

        beam.Teleport(start, QAngle.Zero, Vector.Zero);

        var endPos = trace.EndPos;
        beam.EndPos.X = endPos.X;
        beam.EndPos.Y = endPos.Y;
        beam.EndPos.Z = endPos.Z;
        beam.DispatchSpawn();
    }
}

public static class Prints
{
    public static void ServerLog(string msg, ConsoleColor color = ConsoleColor.White)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(msg);
        Console.ResetColor();
    }
}