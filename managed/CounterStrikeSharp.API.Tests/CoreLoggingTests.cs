using CounterStrikeSharp.API.Core.Logging;
using Serilog.Events;

namespace CounterStrikeSharp.API.Tests;

public class CoreLoggingTests
{
    [Theory]
    [InlineData("verbose", LogEventLevel.Verbose)]
    [InlineData("trace", LogEventLevel.Verbose)]
    [InlineData("debug", LogEventLevel.Debug)]
    [InlineData("information", LogEventLevel.Information)]
    [InlineData("info", LogEventLevel.Information)]
    [InlineData("warning", LogEventLevel.Warning)]
    [InlineData("warn", LogEventLevel.Warning)]
    [InlineData("error", LogEventLevel.Error)]
    [InlineData("fatal", LogEventLevel.Fatal)]
    [InlineData("critical", LogEventLevel.Fatal)]
    public void ParseVerbosity_MapsKnownSpellings(string input, LogEventLevel expected)
    {
        Assert.Equal(expected, CoreLogging.ParseVerbosity(input));
    }

    [Theory]
    [InlineData("DEBUG")]
    [InlineData("Warning")]
    [InlineData("  info  ")]
    public void ParseVerbosity_IsCaseInsensitiveAndTrims(string input)
    {
        // All of these should resolve to a non-default level, proving normalization works.
        var expected = input.Trim().ToLowerInvariant() switch
        {
            "debug" => LogEventLevel.Debug,
            "warning" => LogEventLevel.Warning,
            "info" => LogEventLevel.Information,
            _ => LogEventLevel.Information,
        };
        Assert.Equal(expected, CoreLogging.ParseVerbosity(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    [InlineData("loud")]
    public void ParseVerbosity_FallsBackToInformation(string? input)
    {
        Assert.Equal(LogEventLevel.Information, CoreLogging.ParseVerbosity(input));
    }
}
