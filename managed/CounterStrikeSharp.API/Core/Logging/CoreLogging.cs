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
                .WriteTo.Console(
                    theme: ConsoleTheme,
                    outputTemplate:
                    "{Timestamp:HH:mm:ss.fff} [" + LevelToken + "] {SourceTag:l} {Message:lj}{NewLine}{Exception}")
                // File sinks run through Async so file rolls + Serilog's retention scan
                // (PathRoller regex over the log dir) happen on a background thread instead
                // of stalling the game tick — a synchronous roll was measured at ~469ms on
                // the game thread. One Async wrapper = one shared background queue/thread.
                .WriteTo.Async(a =>
                {
                    a.File(Path.Join(new[] { contentRoot, "logs", $"log-cssharp.txt" }),
                        rollingInterval: RollingInterval.Day, shared: true,
                        outputTemplate:
                        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [" + LevelToken +
                        "] (cssharp:{SourceContext}) {Message:lj}{NewLine}{Exception}");
                    // Errors-only sink: isolates crashes/load failures (incl. plugin blame
                    // reports) into one file that can be handed to a plugin author without
                    // wading through the full info-level log.
                    a.File(Path.Join(new[] { contentRoot, "logs", $"log-errors.txt" }),
                        rollingInterval: RollingInterval.Day, shared: true,
                        restrictedToMinimumLevel: LogEventLevel.Error,
                        outputTemplate:
                        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [" + LevelToken +
                        "] (cssharp:{SourceContext}) {Message:lj}{NewLine}{Exception}");
                })
                .CreateLogger();

            Factory =
                LoggerFactory.Create(builder => { builder.AddSerilog(SerilogLogger); });
        }

        builder.AddSerilog(SerilogLogger);
    }
}