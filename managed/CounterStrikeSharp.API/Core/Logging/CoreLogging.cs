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
    public static ILoggerFactory Factory { get; private set; }
    private static Logger? SerilogLogger { get; set; }

    public static void AddCoreLogging(this ILoggingBuilder builder, string contentRoot)
    {
        if (SerilogLogger == null)
        {
            SerilogLogger = new LoggerConfiguration()
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
                    "{Timestamp:HH:mm:ss} [{Level:u4}] \x1b[36m(cssharp:{SourceContext})\x1b[0m {Message:lj}{NewLine}{Exception}")
                .WriteTo.File(Path.Join(new[] { contentRoot, "logs", $"log-cssharp.txt" }),
                    rollingInterval: RollingInterval.Day, shared: true,
                    outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u4}] (cssharp:{SourceContext}) {Message:lj}{NewLine}{Exception}")
                // Errors-only sink: isolates crashes/load failures (incl. plugin blame
                // reports) into one file that can be handed to a plugin author without
                // wading through the full info-level log.
                .WriteTo.File(Path.Join(new[] { contentRoot, "logs", $"log-errors.txt" }),
                    rollingInterval: RollingInterval.Day, shared: true,
                    restrictedToMinimumLevel: LogEventLevel.Error,
                    outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u4}] (cssharp:{SourceContext}) {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            Factory =
                LoggerFactory.Create(builder => { builder.AddSerilog(SerilogLogger); });
        }

        builder.AddSerilog(SerilogLogger);
    }
}