using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;

namespace CounterStrikeSharp.API.Core.Plugin.Host;

/// <summary>
/// One <see cref="FileSystemWatcher"/> shared by every <see cref="PluginContext"/> instead of one per plugin.
/// On Linux each <see cref="FileSystemWatcher"/> allocates a kernel inotify instance, and the default
/// <c>fs.inotify.max_user_instances</c> on most distros (and inside Pterodactyl/Docker containers) is 128.
/// With many plugins the per-plugin model exhausts that limit and crashes the host with
/// "The configured user limit (128) on the number of inotify instances has been reached".
/// </summary>
public sealed class SharedPluginFileWatcher : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly Dictionary<string, Action> _onDeletedHandlers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly ILogger _logger;
    private bool _disposed;

    public bool IsActive => _watcher != null;

    public SharedPluginFileWatcher(string rootPath, ILogger logger)
    {
        _logger = logger;

        try
        {
            _watcher = new FileSystemWatcher(rootPath)
            {
                Filter = "*.dll",
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            _watcher.Deleted += OnDeleted;
            _watcher.Renamed += OnRenamed;
            _logger.LogDebug("Shared plugin file watcher started at {Root}", rootPath);
        }
        catch (IOException ex) when (ex.Message.Contains("inotify"))
        {
            _logger.LogWarning(ex,
                "Could not create shared plugin file watcher at {Root} — inotify limit reached. " +
                "Hot-reload disabled. Raise the limit: " +
                "sudo sysctl -w fs.inotify.max_user_instances=8192 && " +
                "sudo sysctl -w fs.inotify.max_user_watches=524288 (persist in /etc/sysctl.d/).",
                rootPath);
            _watcher = null;
        }
    }

    public void RegisterDelete(string fullPath, Action handler)
    {
        if (_watcher == null) return;
        lock (_lock) _onDeletedHandlers[fullPath] = handler;
    }

    public void UnregisterDelete(string fullPath)
    {
        if (_watcher == null) return;
        lock (_lock) _onDeletedHandlers.Remove(fullPath);
    }

    private void OnDeleted(object sender, FileSystemEventArgs e) => Dispatch(e.FullPath);

    // Many editors save by writing to a temp file then renaming, which surfaces as Renamed
    // rather than Deleted on the original path. Treat the source side as a delete.
    private void OnRenamed(object sender, RenamedEventArgs e) => Dispatch(e.OldFullPath);

    private void Dispatch(string path)
    {
        Action? handler;
        lock (_lock)
        {
            if (!_onDeletedHandlers.TryGetValue(path, out handler)) return;
        }

        try
        {
            handler();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shared plugin watcher handler threw for {Path}", path);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_watcher != null)
        {
            _watcher.Deleted -= OnDeleted;
            _watcher.Renamed -= OnRenamed;
            _watcher.Dispose();
        }
        lock (_lock) _onDeletedHandlers.Clear();
    }
}
