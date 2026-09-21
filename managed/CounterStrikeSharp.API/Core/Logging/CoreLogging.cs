using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace CounterStrikeSharp.API.Core.Logging;

public static class CoreLogging
{
    /// <summary>
    /// Width the "(cssharp:Foo)" / "(plugin:Foo)" tag is padded to in console output so
    /// messages start in the same column across framework and plugin lines. 26 fits the
    /// longest framework source ("(cssharp:GameDataProvider)"); longer plugin names simply
    /// overflow rather than being truncated.
    /// </summary>
    public const int SourceTagWidth = 26;

    /// <summary>Cyan — the framework's own "(cssharp:...)" tag.</summary>
    public const string TagColorCore = "\x1b[36m";

    /// <summary>Magenta — a plugin's "(plugin:...)" tag.</summary>
    public const string TagColorPlugin = "\x1b[35m";

    /// <summary>
    /// Renders a console source tag: pads to <see cref="SourceTagWidth"/> and *then* wraps
    /// the result in ANSI color.
    ///
    /// Both halves have to happen here rather than in the output template. Serilog's themed
    /// console sink emits its own style-set + reset around every token, so a raw \x1b[36m
    /// written into the template is cancelled by the sink's reset before the property is
    /// even printed (the tag came out theme-grey). And padding via the template's alignment
    /// ({Tag,-26}) has to run on the uncolored text, otherwise the escape bytes count toward
    /// the width and the column goes ragged. Escapes are always emitted — panels like
    /// pterodactyl read the pipe and render them; a plain terminal shows them as color too.
    /// </summary>
    public static string FormatSourceTag(string tag, string color) =>
        color + tag.PadRight(SourceTagWidth) + "\x1b[0m";

    /// <summary>
    /// Level token as it appears in console + file output: three upper-case chars
    /// (VRB/DBG/INF/WRN/ERR/FTL). Fixed width, unlike the u4/u5 forms which produced
    /// ragged, half-truncated words ("INFOR", "WARNI", "EROR").
    /// </summary>
    public const string LevelToken = "{Level:u3}";

    /// <summary>
    /// Shared console theme. Serilog's built-in themes leave Information uncoloured and
    /// Debug/Verbose the same grey as ordinary text, which makes a warning easy to miss in
    /// a wall of boot output. Written as raw ANSI (not <see cref="SystemConsoleTheme"/>) so
    /// the colors survive docker / screen / pterodactyl pipes, where the Windows console
    /// API path emits nothing.
    /// </summary>
    public static readonly AnsiConsoleTheme ConsoleTheme = new(
        new Dictionary<ConsoleThemeStyle, string>
        {
            [ConsoleThemeStyle.Text] = "\x1b[0m",
            [ConsoleThemeStyle.SecondaryText] = "\x1b[90m",
            [ConsoleThemeStyle.TertiaryText] = "\x1b[90m",
            [ConsoleThemeStyle.Invalid] = "\x1b[33m",
            [ConsoleThemeStyle.Null] = "\x1b[94m",
            [ConsoleThemeStyle.Name] = "\x1b[37m",
            [ConsoleThemeStyle.String] = "\x1b[96m",
            [ConsoleThemeStyle.Number] = "\x1b[95m",
            [ConsoleThemeStyle.Boolean] = "\x1b[94m",
            [ConsoleThemeStyle.Scalar] = "\x1b[96m",
            [ConsoleThemeStyle.LevelVerbose] = "\x1b[90m",
            [ConsoleThemeStyle.LevelDebug] = "\x1b[90m",
            [ConsoleThemeStyle.LevelInformation] = "\x1b[32m",
            [ConsoleThemeStyle.LevelWarning] = "\x1b[33m",
            [ConsoleThemeStyle.LevelError] = "\x1b[91m",
            [ConsoleThemeStyle.LevelFatal] = "\x1b[97;41m",
        });

    public static ILoggerFactory Factory { get; private set; } = null!;
    private static Logger? SerilogLogger { get; set; }

    /// <summary>
    /// The single writer of logs/log-all.txt — the aggregate of every plugin's output.
    /// Plugin loggers forward into it with WriteTo.Logger instead of each opening their
    /// own sink on the same path, because the two obvious alternatives are both broken:
    ///
    ///   * one FileSink per plugin with shared: false — the second plugin's sink hits a
    ///     sharing violation on the already-open file and CreateLogger() throws, taking
    ///     the plugin load with it;
    ///   * one FileSink per plugin with shared: true — Serilog's shared mode guards the
    ///     file with a *named* Mutex, which on Linux is a PAL SharedMemory object. Closing
    ///     that handle runs SharedMemoryProcessDataHeader::Close -> `delete m_data`, and
    ///     under .NET 10 libcoreclr imports plain operator new/delete from the global
    ///     scope, where libtier0.so provides them: tier0's _ZdlPv tail-calls
    ///     g_pMemAlloc->Free(), so a glibc-allocated PAL object is handed to Valve's
    ///     allocator and the server takes a SIGSEGV inside libtier0.so. (.NET 8's
    ///     libcoreclr kept those operators internal, which is why this only started with
    ///     the .NET 10 build.) Rolling is daily, so the close clustered just after
    ///     midnight — one mutex per plugin per rollover.
    ///
    /// Stays <see cref="Logger.None"/> until AddCoreLogging has run, so a plugin logger
    /// built before then forwards into a sink that drops instead of throwing.
    /// </summary>
    public static Serilog.ILogger PluginAggregateLogger { get; private set; } = Logger.None;

    // Live minimum-level control. Defaults to Information so the demoted boot/init
    // lines stay hidden; CoreConfig.Load() drives it from the "LogVerbosity" setting
    // and css_core_reload re-applies it without a restart.
    private static readonly LoggingLevelSwitch LevelSwitch = new(LogEventLevel.Information);

    /// <summary>
    /// Sets the framework's minimum log level from a config string. Accepts Serilog
    /// and spdlog spellings (verbose/trace, debug, information/info, warning/warn,
    /// error, fatal/critical). Unknown values fall back to Information.
    /// </summary>
    public static void SetVerbosity(string? level) => LevelSwitch.MinimumLevel = ParseVerbosity(level);

    /// <summary>
    /// Maps a config verbosity string to a Serilog level. Accepts Serilog and spdlog
    /// spellings (verbose/trace, debug, information/info, warning/warn, error,
    /// fatal/critical); null/blank/unknown fall back to Information. Pure — no side effects.
    /// </summary>
    public static LogEventLevel ParseVerbosity(string? level) =>
        (level?.Trim().ToLowerInvariant()) switch
        {
            "verbose" or "trace" => LogEventLevel.Verbose,
            "debug" => LogEventLevel.Debug,
            "information" or "info" => LogEventLevel.Information,
            "warning" or "warn" => LogEventLevel.Warning,
            "error" => LogEventLevel.Error,
            "fatal" or "critical" => LogEventLevel.Fatal,
            _ => LogEventLevel.Information,
        };

    public static void AddCoreLogging(this ILoggingBuilder builder, string contentRoot)
    {
        if (SerilogLogger == null)
        {
            SerilogLogger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(LevelSwitch)
                .Enrich.FromLogContext()
                .Enrich.With<SourceContextEnricher>()
                // ANSI theme (raw \x1b[..m escapes) so colors survive docker/screen/
                // pterodactyl pipes — unlike SystemConsoleTheme which uses the Windows
                // console API and produces no color when redirected. SourceTag arrives
                // already colored cyan and already padded (see FormatSourceTag) so the
                // framework's own lines stand out from game-engine output and messages
                // line up in one column. Only the console sink carries escapes; the file
                // sinks below stay plain text and use the raw SourceContext instead.
                // The console sink is wrapped in Async for the same reason the file sinks
                // are, but against a different failure: Console.Out is an unbuffered
                // write(2) to stdout, and on a real server stdout is a PIPE -- docker
                // logs, pterodactyl's daemon, screen, systemd-journald. When the reader
                // falls behind, the 64 KiB pipe buffer fills and the write BLOCKS the
                // caller. Every core log line is written from the game thread, so a
                // stalled log consumer stalls the tick: the server spike-lags because
                // something outside it stopped reading its console. Async moves the write
                // to a background thread, which turns that stall into queue depth.
                //
                // blockWhenFull: false is the point of the exercise -- when the queue
                // fills (10k events) Serilog DROPS the event rather than applying
                // backpressure to the game thread. Losing console lines during a log
                // storm is strictly better than dropping ticks; the file sinks below are
                // the durable record, and fatal_reporter.cpp writes crash breadcrumbs
                // with a direct write(2) to stderr that does not go through Serilog.
                .WriteTo.Async(a => a.Console(
                    theme: ConsoleTheme,
                    outputTemplate:
                    "{Timestamp:HH:mm:ss.fff} [" + LevelToken + "] {SourceTag:l} {Message:lj}{NewLine}{Exception}"),
                    bufferSize: 10000, blockWhenFull: false)
                // File sinks run through Async so file rolls + Serilog's retention scan
                // (PathRoller regex over the log dir) happen on a background thread instead
                // of stalling the game tick — a synchronous roll was measured at ~469ms on
                // the game thread. One Async wrapper = one shared background queue/thread.
                .WriteTo.Async(a =>
                {
                    a.File(Path.Join(new[] { contentRoot, "logs", $"log-cssharp.txt" }),
                        rollingInterval: RollingInterval.Day,
                        outputTemplate:
                        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [" + LevelToken +
                        "] (cssharp:{SourceContext}) {Message:lj}{NewLine}{Exception}");
                    // Errors-only sink: isolates crashes/load failures (incl. plugin blame
                    // reports) into one file that can be handed to a plugin author without
                    // wading through the full info-level log.
                    a.File(Path.Join(new[] { contentRoot, "logs", $"log-errors.txt" }),
                        rollingInterval: RollingInterval.Day,
                        restrictedToMinimumLevel: LogEventLevel.Error,
                        outputTemplate:
                        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [" + LevelToken +
                        "] (cssharp:{SourceContext}) {Message:lj}{NewLine}{Exception}");
                })
                .CreateLogger();

            // Single owner of log-all.txt (see PluginAggregateLogger). Its own Async
            // wrapper, so a plugin logging does not wait on this file's roll either.
            // No level floor here: each plugin logger applies its own before forwarding.
            var aggregate = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Async(a => a.File(
                        Path.Join(new[] { contentRoot, "logs", "log-all.txt" }),
                        rollingInterval: RollingInterval.Day,
                        outputTemplate:
                        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [" + LevelToken +
                        "] plugin:{PluginName} {Message:lj}{NewLine}{Exception}"))
                .CreateLogger();
            PluginAggregateLogger = aggregate;

            // Every sink is now behind an Async wrapper, so anything still sitting in a
            // queue when the process ends is lost unless the logger is disposed. Nothing
            // in the tree disposed it before (the sinks were synchronous, so there was
            // nothing to lose); ProcessExit fires on a clean `quit` and drains both
            // queues. A hard crash still bypasses this -- that is what the direct
            // write(2) breadcrumb in fatal_reporter.cpp is for.
            var logger = SerilogLogger;
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                logger.Dispose();
                aggregate.Dispose();
            };

            Factory =
                LoggerFactory.Create(builder => { builder.AddSerilog(SerilogLogger); });
        }

        builder.AddSerilog(SerilogLogger);
    }
}