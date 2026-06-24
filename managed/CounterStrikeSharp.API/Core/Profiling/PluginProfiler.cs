/*
 *  This file is part of CounterStrikeSharp.
 *  CounterStrikeSharp is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  CounterStrikeSharp is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with CounterStrikeSharp.  If not, see <https://www.gnu.org/licenses/>. *
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CounterStrikeSharp.API.Core.Profiling
{
    /// <summary>
    /// Attributes per-tick CPU time and allocations to the plugin that ran, so a
    /// slow server frame can be blamed on a specific plugin instead of "a plugin
    /// or GC". All mutation happens on the game thread (listener dispatch + the
    /// per-tick boundary call), so no locking is required; the only cross-thread
    /// handoff is an immutable snapshot passed to <see cref="SlowFrameReporter"/>.
    ///
    /// Detection model: per frame we sum each plugin's time; we keep the single
    /// WORST frame seen in a rolling 1-second window plus the window's per-plugin
    /// totals and GC collection counts. Once per second, if the worst frame blew
    /// past the budget, we emit a report naming the dominant plugin(s).
    /// </summary>
    public static class PluginProfiler
    {
        /// <summary>
        /// Master switch. When false, <see cref="Begin"/>/<see cref="End"/> are a
        /// single branch and add no measurable cost to the hot path.
        /// </summary>
        public static bool Enabled;

        private static double _budgetMs;
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        // ---- game-thread-only state ----
        private static readonly Dictionary<string, FrameAcc> _frame = new();   // current frame
        private static readonly Dictionary<string, double> _windowMs = new();  // rolling 1s
        private static readonly Dictionary<string, long> _windowBytes = new();
        private static Dictionary<string, FrameAcc> _worstSnapshot = new();    // worst frame this window
        private static double _worstFrameMs;
        private static long _windowStartMs;
        private static int _gc0, _gc1, _gc2;
        private static bool _initialized;

        private struct FrameAcc
        {
            public long Ticks;
            public long Bytes;
        }

        /// <summary>Timestamp + allocation baseline captured around a plugin call.</summary>
        public readonly struct Sample
        {
            public readonly long Ts;
            public readonly long Bytes;
            public Sample(long ts, long bytes)
            {
                Ts = ts;
                Bytes = bytes;
            }
        }

        public static void Configure(bool enabled, double budgetMs)
        {
            Enabled = enabled;
            _budgetMs = budgetMs;
        }

        /// <summary>Call immediately before invoking a plugin handler.</summary>
        public static Sample Begin()
        {
            if (!Enabled) return default;
            return new Sample(Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());
        }

        /// <summary>Call immediately after the plugin handler returns.</summary>
        public static void End(string plugin, in Sample start)
        {
            // start.Ts == 0 means Begin() ran while disabled (default Sample); skip.
            if (!Enabled || start.Ts == 0) return;

            long dt = Stopwatch.GetTimestamp() - start.Ts;
            long db = GC.GetAllocatedBytesForCurrentThread() - start.Bytes;

            ref var acc = ref CollectionsMarshal.GetValueRefOrAddDefault(_frame, plugin ?? "unknown", out _);
            acc.Ticks += dt;
            if (db > 0) acc.Bytes += db; // GC mid-call can make the delta negative; ignore those
        }

        /// <summary>
        /// Called once per game frame (from Server.OnTick). Finalizes the frame,
        /// tracks the worst frame in the current second, and emits a report when
        /// the second closes if that worst frame exceeded the budget.
        /// </summary>
        public static void OnFrameBoundary()
        {
            if (!Enabled) return;

            long now = Environment.TickCount64;
            if (!_initialized)
            {
                _initialized = true;
                _windowStartMs = now;
                SampleGcBaseline();
            }

            // Finalize the frame that just ran.
            double frameMs = 0;
            foreach (var kv in _frame)
            {
                double ms = kv.Value.Ticks * TicksToMs;
                frameMs += ms;

                _windowMs.TryGetValue(kv.Key, out var wms);
                _windowMs[kv.Key] = wms + ms;
                _windowBytes.TryGetValue(kv.Key, out var wby);
                _windowBytes[kv.Key] = wby + kv.Value.Bytes;
            }

            if (frameMs > _worstFrameMs)
            {
                _worstFrameMs = frameMs;
                _worstSnapshot = new Dictionary<string, FrameAcc>(_frame);
            }

            _frame.Clear();

            // Close the 1-second window.
            if (now - _windowStartMs >= 1000)
            {
                if (_worstFrameMs > _budgetMs)
                {
                    int g0 = GC.CollectionCount(0) - _gc0;
                    int g1 = GC.CollectionCount(1) - _gc1;
                    int g2 = GC.CollectionCount(2) - _gc2;
                    SlowFrameReporter.Report(BuildSnapshot(g0, g1, g2));
                }

                _windowMs.Clear();
                _windowBytes.Clear();
                _worstSnapshot = new Dictionary<string, FrameAcc>();
                _worstFrameMs = 0;
                _windowStartMs = now;
                SampleGcBaseline();
            }
        }

        private static void SampleGcBaseline()
        {
            _gc0 = GC.CollectionCount(0);
            _gc1 = GC.CollectionCount(1);
            _gc2 = GC.CollectionCount(2);
        }

        private static SlowFrameSnapshot BuildSnapshot(int g0, int g1, int g2)
        {
            // Worst single frame: per-plugin ms, sorted descending.
            var worst = new List<PluginCost>(_worstSnapshot.Count);
            foreach (var kv in _worstSnapshot)
                worst.Add(new PluginCost(kv.Key, kv.Value.Ticks * TicksToMs, kv.Value.Bytes));
            worst.Sort((a, b) => b.Ms.CompareTo(a.Ms));

            // Window allocation totals, sorted descending (likely GC culprit).
            var alloc = new List<PluginCost>(_windowBytes.Count);
            foreach (var kv in _windowBytes)
            {
                _windowMs.TryGetValue(kv.Key, out var ms);
                alloc.Add(new PluginCost(kv.Key, ms, kv.Value));
            }
            alloc.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));

            return new SlowFrameSnapshot(_worstFrameMs, _budgetMs, worst, alloc, g0, g1, g2);
        }
    }

    /// <summary>Per-plugin cost line in a report.</summary>
    public readonly struct PluginCost
    {
        public readonly string Plugin;
        public readonly double Ms;
        public readonly long Bytes;
        public PluginCost(string plugin, double ms, long bytes)
        {
            Plugin = plugin;
            Ms = ms;
            Bytes = bytes;
        }
    }

    /// <summary>Immutable per-second snapshot handed off to the reporter (cross-thread safe).</summary>
    public sealed class SlowFrameSnapshot
    {
        public double WorstFrameMs { get; }
        public double BudgetMs { get; }
        public IReadOnlyList<PluginCost> WorstFrameByCpu { get; }
        public IReadOnlyList<PluginCost> WindowByAlloc { get; }
        public int Gen0 { get; }
        public int Gen1 { get; }
        public int Gen2 { get; }

        public SlowFrameSnapshot(double worstFrameMs, double budgetMs, IReadOnlyList<PluginCost> worstFrameByCpu,
            IReadOnlyList<PluginCost> windowByAlloc, int gen0, int gen1, int gen2)
        {
            WorstFrameMs = worstFrameMs;
            BudgetMs = budgetMs;
            WorstFrameByCpu = worstFrameByCpu;
            WindowByAlloc = windowByAlloc;
            Gen0 = gen0;
            Gen1 = gen1;
            Gen2 = gen2;
        }
    }
}
