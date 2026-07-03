using System;
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
    public static ILoggerFactory Factory { get; private set; } = null!;
    private static Logger? SerilogLogger { get; set; }

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
                // console API and produces no color when redirected. The (cssharp:...)
                // prefix is wrapped in cyan (\x1b[36m ... \x1b[0m) directly in the template
                // so the framework's own lines stand out from game-engine output. Only the
                // console sink carries escapes; the file sinks below stay plain text.
                .WriteTo.Console(
                    theme: AnsiConsoleTheme.Code,
                    outputTemplate:
                    "{Timestamp:HH:mm:ss.fff} [{Level:u5}] \x1b[36m(cssharp:{SourceContext})\x1b[0m {Message:lj}{NewLine}{Exception}")
                // File sinks run through Async so file rolls + Serilog's retention scan
                // (PathRoller regex over the log dir) happen on a background thread instead
                // of stalling the game tick — a synchronous roll was measured at ~469ms on
                // the game thread. One Async wrapper = one shared background queue/thread.
                .WriteTo.Async(a =>
                {
                    a.File(Path.Join(new[] { contentRoot, "logs", $"log-cssharp.txt" }),
                        rollingInterval: RollingInterval.Day, shared: true,
                        outputTemplate:
                        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u5}] (cssharp:{SourceContext}) {Message:lj}{NewLine}{Exception}");
                    // Errors-only sink: isolates crashes/load failures (incl. plugin blame
                    // reports) into one file that can be handed to a plugin author without
                    // wading through the full info-level log.
                    a.File(Path.Join(new[] { contentRoot, "logs", $"log-errors.txt" }),
                        rollingInterval: RollingInterval.Day, shared: true,
                        restrictedToMinimumLevel: LogEventLevel.Error,
                        outputTemplate:
                        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u5}] (cssharp:{SourceContext}) {Message:lj}{NewLine}{Exception}");
                })
                .CreateLogger();

            Factory =
                LoggerFactory.Create(builder => { builder.AddSerilog(SerilogLogger); });
        }

        builder.AddSerilog(SerilogLogger);
    }
}