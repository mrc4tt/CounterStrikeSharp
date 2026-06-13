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
using System.IO;
using System.Linq;
using System.Threading;
using CounterStrikeSharp.API;
using Microsoft.Extensions.Logging;

namespace CounterStrikeSharp.API.Core
{
    /// <summary>
    /// Watches CounterStrikeSharp's own native + managed binaries for on-disk changes
    /// (a framework update applied while the server is live) and, instead of letting the
    /// running match crash, keeps the server on the old binary and announces / schedules a
    /// clean restart at a safe point.
    ///
    /// What this does NOT do: live-reload the new binary into the running process. CS2 has
    /// no in-process binary reload — the CLR cannot be unloaded and the native .so is already
    /// mmap'd. The new version only takes effect after the process exits and an external
    /// supervisor relaunches it. The watcher's whole job is (a) keep the current match alive
    /// on the OLD binary instead of crashing, and (b) optionally trigger that exit at a moment
    /// that doesn't interrupt players (opt-in, supervisor-dependent — see UpdateWatcherAutoRestart).
    ///
    /// For this to be crash-free the update MUST be applied atomically (write to a temp path +
    /// rename), so the live process keeps its old inode mapped. See tools/css-update.sh. A
    /// non-atomic in-place overwrite of the native .so can still SIGSEGV the live process.
    /// </summary>
    internal sealed class UpdateWatcher : IDisposable
    {
        // Only these subdirectories under the CounterStrikeSharp root hold binaries whose
        // on-disk change means "the framework was updated".
        private static readonly string[] WatchedSubdirectories = { "bin", "api" };

        private readonly ILogger _logger;
        private readonly FileSystemWatcher[] _watchers;
        private readonly Timer _debounce;
        private readonly object _lock = new();

        private volatile bool _pending;
        private volatile bool _restarting;
        private string? _firstChangedFile;

        private UpdateWatcher(ILogger logger, FileSystemWatcher[] watchers)
        {
            _logger = logger;
            _watchers = watchers;
            _debounce = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// Creates and starts the watcher, or returns <c>null</c> if disabled / nothing to watch.
        /// </summary>
        public static UpdateWatcher? Start(ILogger logger, string rootPath)
        {
            if (!CoreConfig.UpdateWatcherEnabled)
            {
                return null;
            }

            var dirs = WatchedSubdirectories
                .Select(sub => Path.Combine(rootPath, sub))
                .Where(Directory.Exists)
                .ToArray();

            if (dirs.Length == 0)
            {
                logger.LogWarning("UpdateWatcher enabled but no binary directories found under {Root}; disabling.", rootPath);
                return null;
            }

            var watcher = new UpdateWatcher(logger, new FileSystemWatcher[dirs.Length]);

            for (int i = 0; i < dirs.Length; i++)
            {
                var fsw = new FileSystemWatcher(dirs[i])
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                fsw.Changed += watcher.OnFileChanged;
                fsw.Created += watcher.OnFileChanged;
                fsw.Renamed += watcher.OnFileChanged;
                fsw.EnableRaisingEvents = true;
                watcher._watchers[i] = fsw;
            }

            watcher.RegisterSafePointListeners();

            logger.LogInformation(
                "UpdateWatcher active (watching {Dirs}). AutoRestart={AutoRestart}.",
                string.Join(", ", dirs.Select(Path.GetFileName)), CoreConfig.UpdateWatcherAutoRestart);

            return watcher;
        }

        private static bool IsBinary(string path)
        {
            var ext = Path.GetExtension(path);
            return ext.Equals(".so", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase);
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            // Once an update is staged we no longer care about further churn from the copy.
            if (_pending || !IsBinary(e.FullPath))
            {
                return;
            }

            lock (_lock)
            {
                _firstChangedFile ??= e.FullPath;
            }

            // Debounce: an update rewrites many files; wait until the writes settle before
            // declaring the update complete, so we never act mid-copy.
            var debounceMs = Math.Max(1, CoreConfig.UpdateWatcherDebounceSeconds) * 1000;
            _debounce.Change(debounceMs, Timeout.Infinite);
        }

        private void OnDebounceElapsed(object? state)
        {
            lock (_lock)
            {
                if (_pending)
                {
                    return;
                }

                _pending = true;
            }

            _logger.LogWarning(
                "==================== COUNTERSTRIKESHARP UPDATE DETECTED ====================");
            _logger.LogWarning(
                "On-disk change to a framework binary (e.g. {File}).", _firstChangedFile ?? "<unknown>");
            _logger.LogWarning(
                "The running server is STILL on the OLD binary and will keep running this match.");
            _logger.LogWarning(
                "A full process restart is required to load the new version (CS2 cannot hot-swap it).");

            if (CoreConfig.UpdateWatcherAutoRestart)
            {
                _logger.LogWarning(
                    "AutoRestart is ON: the server will restart via '{Command}' at the next safe point" +
                    " (map change / empty server). Ensure your supervisor relaunches the process.",
                    CoreConfig.UpdateWatcherRestartCommand);

                // The server may already be empty; check on the main thread right away.
                Server.NextFrame(() => MaybeRestartWhenEmpty("server already empty at update time"));
            }
            else
            {
                _logger.LogWarning(
                    "AutoRestart is OFF: restart the server when convenient to apply the update.");
            }

            _logger.LogWarning(
                "===========================================================================");
        }

        private void RegisterSafePointListeners()
        {
            // Map change is a natural break between matches -> restart here if auto-restart is on.
            RegisterListener("OnMapEnd", _ =>
            {
                if (_pending && CoreConfig.UpdateWatcherAutoRestart && CoreConfig.UpdateWatcherRestartOnMapChange)
                {
                    DoRestart("map change");
                }
            });

            // A player leaving may have emptied the server -> restart with zero interruption.
            // Deferred to next frame so the disconnecting player is gone from the count.
            RegisterListener("OnClientDisconnectPost", _ =>
            {
                if (_pending && CoreConfig.UpdateWatcherAutoRestart && CoreConfig.UpdateWatcherRestartWhenEmpty)
                {
                    Server.NextFrame(() => MaybeRestartWhenEmpty("server empty"));
                }
            });
        }

        // Registers a core-owned listener. The FunctionReference is permanent and never removed
        // (the watcher lives for the whole process), and is GC-crash-safe by design.
        private static void RegisterListener(string listenerName, Action<ScriptContext> handler)
        {
            var wrapper = new Func<ScriptContext, HookResult>(ctx =>
            {
                handler(ctx);
                return HookResult.Continue;
            });

            var reference = FunctionReference.Create(wrapper);
            NativeAPI.AddListener(listenerName, (InputArgument)reference);
        }

        private void MaybeRestartWhenEmpty(string reason)
        {
            if (!_pending || _restarting)
            {
                return;
            }

            var humans = Utilities.GetPlayers().Count(p => !p.IsBot);
            if (humans == 0)
            {
                DoRestart(reason);
            }
        }

        private void DoRestart(string reason)
        {
            lock (_lock)
            {
                if (_restarting)
                {
                    return;
                }

                _restarting = true;
            }

            var command = CoreConfig.UpdateWatcherRestartCommand;
            _logger.LogWarning(
                "Applying staged CounterStrikeSharp update: restarting server now ({Reason}) via '{Command}'." +
                " The supervisor must relaunch the process to load the new binary.",
                reason, command);

            Server.ExecuteCommand(command);
        }

        public void Dispose()
        {
            foreach (var fsw in _watchers)
            {
                if (fsw == null)
                {
                    continue;
                }

                fsw.EnableRaisingEvents = false;
                fsw.Dispose();
            }

            _debounce.Dispose();
        }
    }
}
