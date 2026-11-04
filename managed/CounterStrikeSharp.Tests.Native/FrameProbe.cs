using System;
using System.Diagnostics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace NativeTestsPlugin;

/// <summary>
/// Frame-time half of the leak probe. A leak that grows a per-frame list (callbacks, hooks,
/// timers left behind by reloads) shows up as frame work growing with uptime long before frames
/// go over budget, so this records two things per frame:
///   interval  OnTick to OnTick: what players feel; above the tick interval = a late frame
///   work      OnServerPreEntityThink to OnTick: entity think plus the rest of GameFrame,
///             including every plugin's OnTick/timers registered before this one
/// plus the GC count and pause time over the window.
///   css_leak_frames          print the window since the last reset, then reset
///   css_leak_frames reset    reset only (after a map change, reload or churn)
/// Samples go into preallocated arrays, so recording a frame allocates nothing.
/// </summary>
public static class FrameProbe
{
    // 30 minutes at 64 tick; frames past this are counted but not kept for the percentiles.
    private const int Capacity = 64 * 60 * 30;

    private static readonly float[] Intervals = new float[Capacity];
    private static readonly float[] Work = new float[Capacity];
    private static int _intervalCount, _workCount, _frames;
    private static long _lastTick, _thinkStart, _windowStart;
    private static int _gc0, _gc1, _gc2;
    private static TimeSpan _gcPause;

    private static string Inv(FormattableString s) => FormattableString.Invariant(s);

    private static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    public static void Register(BasePlugin plugin)
    {
        Reset();
        plugin.RegisterListener<Listeners.OnServerPreEntityThink>(() => _thinkStart = Stopwatch.GetTimestamp());
        plugin.RegisterListener<Listeners.OnTick>(OnTick);
        plugin.AddCommand("css_leak_frames", "Print and reset frame-time stats.", (_, info) =>
        {
            if (info.ArgCount > 1 && info.GetArg(1) == "reset") Reset();
            else Print();
        });
    }

    private static void OnTick()
    {
        var now = Stopwatch.GetTimestamp();
        _frames++;
        if (_lastTick != 0 && _intervalCount < Capacity) Intervals[_intervalCount++] = (float)Ms(_lastTick, now);
        if (_thinkStart != 0 && _workCount < Capacity) Work[_workCount++] = (float)Ms(_thinkStart, now);
        _lastTick = now;
        _thinkStart = 0;
    }

    public static void Reset()
    {
        _intervalCount = _workCount = _frames = 0;
        _lastTick = _thinkStart = 0;
        _windowStart = Stopwatch.GetTimestamp();
        _gc0 = GC.CollectionCount(0);
        _gc1 = GC.CollectionCount(1);
        _gc2 = GC.CollectionCount(2);
        _gcPause = GC.GetTotalPauseDuration();
    }

    private static float Percentile(float[] sorted, int n, double p) => n == 0 ? 0 : sorted[Math.Min(n - 1, (int)(n * p))];

    private static string Describe(string name, float[] values, int n)
    {
        var sorted = new float[n];
        Array.Copy(values, sorted, n);
        Array.Sort(sorted);
        double sum = 0;
        for (var i = 0; i < n; i++) sum += sorted[i];
        return Inv($"{name}_mean={(n == 0 ? 0 : sum / n):F3} {name}_p50={Percentile(sorted, n, 0.50):F3} ") +
               Inv($"{name}_p99={Percentile(sorted, n, 0.99):F3} {name}_max={(n == 0 ? 0 : sorted[n - 1]):F3}");
    }

    private static void Print()
    {
        // A frame counts as late when it arrives more than half a tick after it was due.
        var lateMs = Server.TickInterval * 1000 * 1.5;
        var late = 0;
        for (var i = 0; i < _intervalCount; i++)
            if (Intervals[i] > lateMs) late++;

        Server.PrintToConsole(
            Inv($"LEAKPROBE frames window_s={Ms(_windowStart, Stopwatch.GetTimestamp()) / 1000:F0} frames={_frames} late={late} ") +
            $"{Describe("interval", Intervals, _intervalCount)} {Describe("work", Work, _workCount)} " +
            $"gc0={GC.CollectionCount(0) - _gc0} gc1={GC.CollectionCount(1) - _gc1} gc2={GC.CollectionCount(2) - _gc2} " +
            Inv($"gc_pause_ms={(GC.GetTotalPauseDuration() - _gcPause).TotalMilliseconds:F1}"));
        Reset();
    }
}
