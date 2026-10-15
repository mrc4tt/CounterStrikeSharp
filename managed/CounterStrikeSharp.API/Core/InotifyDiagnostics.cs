using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace CounterStrikeSharp.API.Core
{
    /// <summary>
    /// Keeps plugins clear of the Linux inotify instance limit, and explains it when they hit it.
    /// <para>
    /// Every <see cref="FileSystemWatcher"/> on Linux is one inotify instance, and
    /// <c>fs.inotify.max_user_instances</c> (often 128 or 1024) counts per <i>user</i>, across every
    /// process that user runs. Hosts running many CS2 servers under one account exhausted it, and
    /// any plugin that then created a watcher failed to load with "The configured user limit
    /// (1024) on the number of inotify instances has been reached" (an admin plugin, in the live
    /// incident). Core itself no longer creates watchers unless hot reload is on.
    /// </para>
    /// </summary>
    internal static class InotifyDiagnostics
    {
        private const string PollingEnvVar = "DOTNET_USE_POLLING_FILE_WATCHER";
        private const string LimitPath = "/proc/sys/fs/inotify/max_user_instances";

        /// <summary>
        /// Switches <c>Microsoft.Extensions.FileProviders.PhysicalFileProvider</c> to polling for this
        /// process, so a plugin using <c>AddJsonFile(..., reloadOnChange: true)</c> or
        /// <c>IOptionsMonitor</c> watches its config without an inotify instance. Reload still works,
        /// it just lands within ~4s instead of immediately. Raw <c>new FileSystemWatcher</c> calls are
        /// unaffected; nothing short of patching the runtime changes those. Must run before any
        /// plugin loads (the provider reads the variable when it is constructed). An operator value
        /// (e.g. <c>0</c>) is left alone.
        /// </summary>
        public static void ApplyPollingFileWatcherDefault()
        {
            if (!OperatingSystem.IsLinux()) return;
            if (Environment.GetEnvironmentVariable(PollingEnvVar) != null) return;
            Environment.SetEnvironmentVariable(PollingEnvVar, "1");
        }

        /// <summary>
        /// True when <paramref name="ex"/> is the runtime's inotify-exhaustion error, for either
        /// limit (instances or watches; see <see cref="IsWatchLimitError"/>).
        /// </summary>
        public static bool IsInotifyLimitError(Exception ex)
        {
            return ex is IOException && (ex.Message ?? string.Empty).Contains("inotify", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True for the max_user_watches variant rather than max_user_instances.</summary>
        public static bool IsWatchLimitError(Exception ex)
        {
            return IsInotifyLimitError(ex) && ex.Message.Contains("inotify watches", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Logs a warning at startup when this user's inotify instances are at 80% of the limit or
        /// more, naming the processes holding the most. Runs once; the /proc scan is tens of ms.
        /// </summary>
        public static void WarnIfNearLimit(ILogger logger)
        {
            try
            {
                if (!TryGetUsage(out var used, out var limit, out var top)) return;
                // long: hosts that mean "unlimited" set the sysctl to int.MaxValue, and limit*4 wraps.
                if ((long)used * 5 < (long)limit * 4) return;

                logger.LogWarning(
                    "inotify instances for this user (processes visible here): {Used}/{Limit}. Plugins that create a file watcher will fail to load once it is full. " +
                    "Top holders: {Top}. Raise fs.inotify.max_user_instances, or run fewer servers per user",
                    used, limit, string.Join(", ", top.Select(t => $"{t.Name}[{t.Pid}]={t.Count}")));
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "inotify usage check failed");
            }
        }

        /// <summary>One-line usage summary for a load-failure hint, or null when unavailable.</summary>
        public static string? DescribeUsage()
        {
            try
            {
                if (!TryGetUsage(out var used, out var limit, out var top)) return null;
                return $"Processes of this user visible here hold {used}/{limit}; top holders: "
                       + string.Join(", ", top.Select(t => $"{t.Name}[{t.Pid}]={t.Count}")) + ".";
            }
            catch
            {
                return null;
            }
        }

        private static readonly object CacheLock = new();
        private static DateTime _cachedAt = DateTime.MinValue;
        private static (int Used, int Limit, List<(string Name, int Pid, int Count)> Top)? _cached;

        // The scan readlinks every fd of every visible process, so a burst of load failures (the
        // limit being full typically fails several plugins in a row) reuses one result.
        private static bool TryGetUsage(out int used, out int limit, out List<(string Name, int Pid, int Count)> top)
        {
            lock (CacheLock)
            {
                if (_cached == null || DateTime.UtcNow - _cachedAt > TimeSpan.FromSeconds(30))
                {
                    _cached = Scan();
                    _cachedAt = DateTime.UtcNow;
                }

                used = _cached?.Used ?? 0;
                limit = _cached?.Limit ?? 0;
                top = _cached?.Top ?? new List<(string, int, int)>();
                return _cached != null;
            }
        }

        // Counts inotify fds of every /proc/<pid> owned by our real UID — what the per-user limit
        // counts. The UID filter matters when running as root, which can read every process's fds.
        // Known blind spot: in a container (Pterodactyl/Docker) other containers sharing the host
        // UID are in another PID namespace and invisible, so this undercounts there.
        private static (int Used, int Limit, List<(string Name, int Pid, int Count)> Top)? Scan()
        {
            if (!OperatingSystem.IsLinux() || !File.Exists(LimitPath)) return null;
            if (!int.TryParse(File.ReadAllText(LimitPath).Trim(), out var limit) || limit <= 0) return null;

            var self = RealUid("/proc/self");
            if (self == null) return null;

            int used = 0;
            var perProcess = new List<(string Name, int Pid, int Count)>();
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
                if (RealUid(dir) != self) continue;

                int count = 0;
                try
                {
                    foreach (var fd in Directory.EnumerateFiles(Path.Combine(dir, "fd")))
                    {
                        try
                        {
                            if (new FileInfo(fd).LinkTarget == "anon_inode:inotify") count++;
                        }
                        catch
                        {
                            // fd closed between enumerate and readlink
                        }
                    }
                }
                catch
                {
                    continue; // other user's process, or it exited
                }

                if (count == 0) continue;
                used += count;

                string name;
                try
                {
                    name = File.ReadAllText(Path.Combine(dir, "comm")).Trim();
                }
                catch
                {
                    name = "?";
                }

                perProcess.Add((name, pid, count));
            }

            return (used, limit, perProcess.OrderByDescending(p => p.Count).Take(5).ToList());
        }

        // First field of the "Uid:" line in /proc/<pid>/status (real UID), or null if unreadable.
        private static string? RealUid(string procDir)
        {
            try
            {
                foreach (var line in File.ReadLines(Path.Combine(procDir, "status")))
                {
                    if (line.StartsWith("Uid:", StringComparison.Ordinal))
                        return line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1];
                }
            }
            catch
            {
                // process exited
            }

            return null;
        }
    }
}
