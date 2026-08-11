using CounterStrikeSharp.API.Core.Plugin;
using Serilog.Core;
using Serilog.Events;

namespace CounterStrikeSharp.API.Core.Logging;

public class PluginNameEnricher : ILogEventEnricher
{
    public const string PropertyName = "PluginName";

    /// <summary>
    /// Pre-rendered "(plugin:Foo)" tag. Separate from <see cref="PropertyName"/> so the
    /// console template can pad the whole parenthesised group to the same column the
    /// framework's "(cssharp:Foo)" tag uses — see <c>CoreLogging.SourceTagWidth</c>.
    /// </summary>
    public const string TagPropertyName = "PluginTag";

    public PluginNameEnricher(PluginContext pluginContext)
    {
        Context = pluginContext;
    }

    public PluginContext Context { get; }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var property = propertyFactory.CreateProperty(PropertyName, Context.Plugin.ModuleName);
        logEvent.AddPropertyIfAbsent(property);

        logEvent.AddPropertyIfAbsent(new LogEventProperty(TagPropertyName, new ScalarValue(
            CoreLogging.FormatSourceTag("(plugin:" + Context.Plugin.ModuleName + ")", CoreLogging.TagColorPlugin))));
    }
}