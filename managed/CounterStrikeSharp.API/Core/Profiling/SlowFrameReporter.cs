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
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace CounterStrikeSharp.API.Core.Profiling
{
    /// <summary>
    /// Writes slow-frame reports to a log file and (optionally) a Discord webhook.
    /// All IO runs off the game thread: <see cref="Report"/> formats from the
    /// immutable snapshot and hands the rest to the thread pool, so logging a
    /// spike never causes another one.
    /// </summary>
    public static class SlowFrameReporter
    {
        // One shared HttpClient for the process (the documented correct pattern;
        // a per-call HttpClient leaks sockets).
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

        private static bool _enabled;
        private static string _logPath = "";
        private static string _webhookUrl = "";
        private static int _topN = 5;
        private static long _discordCooldownMs;
        private static long _lastDiscordMs = long.MinValue / 2;

        /// <summary>
        /// Configure from core config. <paramref name="webhookUrl"/> should already
        /// be resolved (env override applied) by the caller; empty = file only.
        /// </summary>
        public static void Configure(bool enabled, string rootPath, string logFile, string webhookUrl, int topN,
            int discordCooldownSeconds)
        {
            _enabled = enabled;
            _webhookUrl = webhookUrl ?? "";
            _topN = topN < 1 ? 1 : topN;
            _discordCooldownMs = (long)discordCooldownSeconds * 1000;

            // logFile may be relative (resolve against the addons root) or absolute.
            _logPath = Path.IsPathRooted(logFile) ? logFile : Path.Join(rootPath, logFile);
        }

        public static void Report(SlowFrameSnapshot snap)
        {
            if (!_enabled || snap == null) return;

            // Format on the game thread (cheap, string building only), then offload
            // all IO. Capture a webhook decision now so the cooldown is honoured
            // deterministically.
            long now = Environment.TickCount64;
            bool sendDiscord = !string.IsNullOrEmpty(_webhookUrl) && (now - _lastDiscordMs) >= _discordCooldownMs;
            if (sendDiscord) _lastDiscordMs = now;

            string text = FormatText(snap);
            string logPath = _logPath;
            string webhook = sendDiscord ? _webhookUrl : "";

            _ = Task.Run(async () =>
            {
                await WriteFileAsync(logPath, text).ConfigureAwait(false);
                if (webhook.Length > 0)
                    await PostDiscordAsync(webhook, text).ConfigureAwait(false);
            });
        }

        private static string FormatText(SlowFrameSnapshot snap)
        {
            var sb = new StringBuilder(256);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            sb.Append("[").Append(stamp).Append("] Slow server frame: ")
                .Append(snap.WorstFrameMs.ToString("F1", CultureInfo.InvariantCulture))
                .Append(" ms (budget ").Append(snap.BudgetMs.ToString("F1", CultureInfo.InvariantCulture))
                .Append(" ms)\n");

            sb.Append("  CPU (worst frame): ");
            AppendCpu(sb, snap);
            sb.Append('\n');

            sb.Append("  GC this second: gen0 +").Append(snap.Gen0)
                .Append(", gen1 +").Append(snap.Gen1)
                .Append(", gen2 +").Append(snap.Gen2);
            if (snap.Gen2 > 0)
                sb.Append("  <- gen2 collection likely caused the pause");
            sb.Append('\n');

            sb.Append("  Alloc (this second): ");
            AppendAlloc(sb, snap);
            sb.Append('\n');

            sb.Append("  Verdict: ").Append(Verdict(snap)).Append('\n');
            return sb.ToString();
        }

        private static void AppendCpu(StringBuilder sb, SlowFrameSnapshot snap)
        {
            if (snap.WorstFrameByCpu.Count == 0) { sb.Append("(none attributed)"); return; }
            int n = Math.Min(_topN, snap.WorstFrameByCpu.Count);
            for (int i = 0; i < n; i++)
            {
                var c = snap.WorstFrameByCpu[i];
                if (i > 0) sb.Append(" | ");
                sb.Append(c.Plugin).Append(' ').Append(c.Ms.ToString("F1", CultureInfo.InvariantCulture)).Append("ms");
            }
        }

        private static void AppendAlloc(StringBuilder sb, SlowFrameSnapshot snap)
        {
            if (snap.WindowByAlloc.Count == 0) { sb.Append("(none)"); return; }
            int n = Math.Min(_topN, snap.WindowByAlloc.Count);
            for (int i = 0; i < n; i++)
            {
                var c = snap.WindowByAlloc[i];
                if (i > 0) sb.Append(" | ");
                sb.Append(c.Plugin).Append(' ').Append(FormatBytes(c.Bytes)).Append("/s");
            }
        }

        private static string Verdict(SlowFrameSnapshot snap)
        {
            string? topCpu = snap.WorstFrameByCpu.Count > 0 ? snap.WorstFrameByCpu[0].Plugin : null;
            string? topAlloc = snap.WindowByAlloc.Count > 0 ? snap.WindowByAlloc[0].Plugin : null;

            // If a gen2 collection happened and the top CPU consumer is small, the
            // pause was GC; blame the heaviest allocator.
            bool cpuDominant = topCpu != null && snap.WorstFrameByCpu[0].Ms >= snap.BudgetMs * 0.5;

            if (cpuDominant && topCpu == topAlloc)
                return topCpu + " — hot handler AND heaviest allocator (fix its per-tick work).";
            if (cpuDominant)
                return topCpu + " — dominant CPU in the slow frame.";
            if (snap.Gen2 > 0 && topAlloc != null)
                return "GC pause — heaviest allocator is " + topAlloc + " (reduce its allocations).";
            if (topCpu != null)
                return topCpu + " — most likely, but cost was spread out.";
            return "Unattributed (cost outside instrumented plugin handlers, or engine/GC).";
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture) + " MB";
        }

        private static async Task WriteFileAsync(string path, string text)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                await File.AppendAllTextAsync(path, text).ConfigureAwait(false);
            }
            catch
            {
                // Logging about a logging failure on the hot path helps nobody; swallow.
            }
        }

        private static async Task PostDiscordAsync(string webhook, string text)
        {
            try
            {
                // Discord embed description cap is 4096; trim defensively.
                if (text.Length > 3900) text = text.Substring(0, 3900) + "\n…(truncated)";

                var payload = new
                {
                    username = "CSSharp Frame Watchdog",
                    embeds = new[]
                    {
                        new
                        {
                            title = "⚠ Slow server frame",
                            description = "```\n" + text + "```",
                            color = 15158332 // red
                        }
                    }
                };

                string json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await _http.PostAsync(webhook, content).ConfigureAwait(false);
                // 429 / 4xx are non-fatal; nothing to do but drop this report.
            }
            catch
            {
                // Network blocked / webhook deleted: file log still has the data.
            }
        }
    }
}
