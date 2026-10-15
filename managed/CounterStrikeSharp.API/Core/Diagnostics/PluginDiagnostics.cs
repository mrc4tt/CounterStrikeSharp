using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;

namespace CounterStrikeSharp.API.Core.Diagnostics;

/// <summary>
/// Fork-only: lightweight per-plugin runtime diagnostics. Tracks how many
/// exceptions each plugin has thrown from its callbacks/handlers, and throttles
/// repeated identical crash reports so a handler that throws every tick cannot
/// flood the log / fill the disk. Keyed by the plugin's assembly simple name.
/// </summary>
public static class PluginDiagnostics
{
    // assembly name -> total runtime errors observed
    private static readonly ConcurrentDictionary<string, int> _errorCounts = new();

    // throttle key (assembly|handler|exceptionType) -> times seen
    private static readonly ConcurrentDictionary<string, int> _reportCounts = new();

    // After this many identical reports, only emit one summary line per interval.
    private const int FullReportThreshold = 1;
    private const int SuppressionInterval = 250;

    /// <summary>
    /// Records one runtime error for the owning plugin and decides whether the
    /// full blame report should be logged this time. Returns the decision plus
    /// the running totals so the caller can annotate the log line.
    /// </summary>
    public static ThrottleDecision RecordError(string assemblyName, string throttleKey)
    {
        var total = _errorCounts.AddOrUpdate(assemblyName ?? "unknown", 1, (_, c) => c + 1);
        var seen = _reportCounts.AddOrUpdate(throttleKey ?? "unknown", 1, (_, c) => c + 1);

        // Always show the first few; after that only every SuppressionInterval-th.
        bool logFull = seen <= FullReportThreshold;
        bool logSuppressionNotice = !logFull && (seen % SuppressionInterval == 0);

        return new ThrottleDecision(logFull, logSuppressionNotice, seen, total);
    }

    /// <summary>
    /// Forgets a plugin's counts and throttle state. Called when the plugin (re)loads: otherwise
    /// after a fix + reload the same error never got a full report again (it was throttled from
    /// the previous run) and css_plugins kept showing the old total.
    /// </summary>
    public static void Reset(string assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName)) return;
        _errorCounts.TryRemove(assemblyName, out _);
        var prefix = assemblyName + "|";
        foreach (var key in _reportCounts.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                _reportCounts.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Simple name of an assembly for error and diagnostic paths. Never throws.
    /// <para>
    /// GetName() parses the assembly's metadata and throws when that is unreadable, e.g. a plugin
    /// DLL overwritten on disk while loaded (CLDB_E_INDEX_NOTFOUND 0x80131124, surfacing as
    /// "0 is an invalid culture identifier"). Called from the catch block of
    /// FunctionReference.Dispatch, that second exception escaped the catch and terminated the
    /// server. Falls back to FullName, then the file name from Location, then "unknown".
    /// </para>
    /// </summary>
    internal static string SafeAssemblyName(Assembly? asm)
    {
        if (asm == null) return "unknown";

        try
        {
            return asm.GetName().Name ?? "unknown";
        }
        catch
        {
            // fall through
        }

        try
        {
            var fullName = asm.FullName;
            if (!string.IsNullOrEmpty(fullName))
            {
                var comma = fullName.IndexOf(',');
                return comma > 0 ? fullName[..comma] : fullName;
            }
        }
        catch
        {
            // fall through
        }

        try
        {
            var location = asm.Location;
            if (!string.IsNullOrEmpty(location)) return Path.GetFileNameWithoutExtension(location);
        }
        catch
        {
            // fall through
        }

        return "unknown";
    }

    /// <summary>Total runtime errors recorded for a plugin assembly (0 if none).</summary>
    public static int GetErrorCount(string assemblyName)
        => assemblyName != null && _errorCounts.TryGetValue(assemblyName, out var c) ? c : 0;

    public readonly struct ThrottleDecision
    {
        public ThrottleDecision(bool logFull, bool logSuppressionNotice, int timesSeen, int pluginTotal)
        {
            LogFull = logFull;
            LogSuppressionNotice = logSuppressionNotice;
            TimesSeen = timesSeen;
            PluginTotal = pluginTotal;
        }

        /// <summary>Emit the full blame report this time.</summary>
        public bool LogFull { get; }

        /// <summary>Emit a short "still happening" notice (report itself suppressed).</summary>
        public bool LogSuppressionNotice { get; }

        /// <summary>How many times this exact error has been seen.</summary>
        public int TimesSeen { get; }

        /// <summary>Total errors for the owning plugin across all handlers.</summary>
        public int PluginTotal { get; }
    }
}
