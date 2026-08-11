using System.Linq;
using Serilog.Core;
using Serilog.Events;

namespace CounterStrikeSharp.API.Core.Logging;

public class SourceContextEnricher : ILogEventEnricher
{
    /// <summary>
    /// Name of the pre-rendered, console-only "(cssharp:Foo)" tag — already padded and
    /// ANSI-colored by <see cref="CoreLogging.FormatSourceTag"/>. It exists as its own
    /// property because padding <c>SourceContext</c> directly would put the spaces inside
    /// the parentheses, and because the themed console sink cancels any color the output
    /// template tries to apply. File sinks use the plain <c>SourceContext</c>.
    /// </summary>
    public const string TagPropertyName = "SourceTag";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var name = "Core";

        if (logEvent.Properties.TryGetValue("SourceContext", out var property))
        {
            var scalarValue = property as ScalarValue;
            var value = scalarValue?.Value as string;

            if (value?.StartsWith("CounterStrikeSharp") ?? false)
            {
                var lastElement = value.Split(".").LastOrDefault();
                if (!string.IsNullOrWhiteSpace(lastElement))
                {
                    logEvent.AddOrUpdateProperty(new LogEventProperty("SourceContext", new ScalarValue(lastElement)));
                    name = lastElement;
                }
                else
                {
                    name = value;
                }
            }
            else if (!string.IsNullOrWhiteSpace(value))
            {
                name = value;
            }
        }

        logEvent.AddOrUpdateProperty(new LogEventProperty(TagPropertyName,
            new ScalarValue(CoreLogging.FormatSourceTag("(cssharp:" + name + ")", CoreLogging.TagColorCore))));
    }
}