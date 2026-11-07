using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using CounterStrikeSharp.API.Core;

namespace NativeTestsPlugin;

/// <summary>
/// Which methods the runtime JIT-compiles during a window, and how long each took. Answers "what
/// is the cold cost of this scenario" with method names instead of a single number.
///   css_jit start           start recording (runtime JIT events, in-process EventListener)
///   css_jit stop [N]        stop, print the N slowest compiles (default 40) plus totals per assembly
/// Output lines start with JITPROBE. Times are wall time from MethodJittingStarted to MethodLoad on
/// the compiling thread, so they include the JIT itself and anything it ran (class constructors).
/// </summary>
public static class JitProbe
{
    private static Listener? _listener;

    public static void Register(BasePlugin plugin)
    {
        plugin.AddCommand("css_jit", "JIT probe: start | stop [N]", (_, info) =>
        {
            var verb = info.ArgCount > 1 ? info.GetArg(1) : "";
            if (verb == "start")
            {
                _listener?.Dispose();
                _listener = new Listener();
                Console.WriteLine("JITPROBE started");
            }
            else if (verb == "stop")
            {
                var top = info.ArgCount > 2 && int.TryParse(info.GetArg(2), out var n) ? n : 40;
                var listener = _listener;
                _listener = null;
                if (listener == null) { Console.WriteLine("JITPROBE not running"); return; }
                listener.Dispose();
                listener.Print(top);
            }
            else Console.WriteLine("JITPROBE usage: css_jit start | stop [N]");
        });
    }

    private sealed class Listener : EventListener
    {
        private const EventKeywords JitKeyword = (EventKeywords)0x10;
        private const int MethodLoadVerbose = 143;
        private const int MethodJittingStarted = 145;

        private readonly ConcurrentDictionary<(ulong Id, ulong Thread), DateTime> _started = new();
        private readonly ConcurrentQueue<(string Name, double Ms, int Tier)> _done = new();
        private volatile bool _enabled;

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
            {
                EnableEvents(source, EventLevel.Verbose, JitKeyword);
                _enabled = true;
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (!_enabled || e.Payload == null) return;
            if (e.EventId == MethodJittingStarted)
            {
                _started[(Convert.ToUInt64(e.Payload[0]), (ulong)e.OSThreadId)] = e.TimeStamp;
            }
            else if (e.EventId == MethodLoadVerbose)
            {
                var id = Convert.ToUInt64(e.Payload[0]);
                if (!_started.TryRemove((id, (ulong)e.OSThreadId), out var start)) return;
                var ns = Get(e, "MethodNamespace");
                var name = Get(e, "MethodName");
                // MethodFlags bit 0x1 = dynamic; OptimizationTier is payload "OptimizationTier" on newer runtimes.
                var tier = e.PayloadNames?.Contains("OptimizationTier") == true ? Convert.ToInt32(e.Payload[e.PayloadNames.IndexOf("OptimizationTier")]) : -1;
                _done.Enqueue(($"{ns}::{name}", (e.TimeStamp - start).TotalMilliseconds, tier));
            }
        }

        private static string Get(EventWrittenEventArgs e, string field)
        {
            var i = e.PayloadNames?.IndexOf(field) ?? -1;
            return i >= 0 ? e.Payload![i]?.ToString() ?? "" : "";
        }

        public void Print(int top)
        {
            var all = _done.ToArray();
            Console.WriteLine(FormattableString.Invariant(
                $"JITPROBE total methods={all.Length} ms={all.Sum(x => x.Ms):F2}"));
            foreach (var group in all.GroupBy(x => Owner(x.Name)).OrderByDescending(g => g.Sum(x => x.Ms)).Take(12))
                Console.WriteLine(FormattableString.Invariant(
                    $"JITPROBE by-owner {group.Key} methods={group.Count()} ms={group.Sum(x => x.Ms):F2}"));
            foreach (var (name, ms, tier) in all.OrderByDescending(x => x.Ms).Take(top))
                Console.WriteLine(FormattableString.Invariant($"JITPROBE {ms,8:F3} ms tier={tier} {name}"));
        }

        // Namespace prefix up to the second dot: "CounterStrikeSharp.API", "System.Linq", "Profiler"...
        private static string Owner(string name)
        {
            var ns = name.Split("::")[0];
            var parts = ns.Split('.');
            return parts.Length >= 2 ? parts[0] + "." + parts[1] : parts[0];
        }
    }
}
