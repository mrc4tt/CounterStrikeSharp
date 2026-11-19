using CounterStrikeSharp.API.Core.Translations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CounterStrikeSharp.API.Tests;

// GeoIpConfig is process-wide state: run alone and put the defaults back.
[CollectionDefinition(nameof(GeoIpConfigTests), DisableParallelization = true)]
public class GeoIpConfigCollection
{
}

[Collection(nameof(GeoIpConfigTests))]
public class GeoIpConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("geoip-config").FullName;

    public void Dispose()
    {
        GeoIpConfig.SetForTests(new GeoIpConfigData());
        Directory.Delete(_dir, true);
    }

    private void Write(string json) => File.WriteAllText(Path.Join(_dir, GeoIpConfig.FileName), json);

    private sealed class CountingLogger : ILogger
    {
        public int Warnings;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings++;
        }
    }

    [Fact]
    public void MissingFileMeansDefaults()
    {
        GeoIpConfig.SetForTests(new GeoIpConfigData { Enabled = false, MaxAgeDays = 99 });

        GeoIpConfig.Load(_dir, NullLogger.Instance);

        Assert.True(GeoIpConfig.Enabled);
        Assert.True(GeoIpConfig.Notify);
        Assert.Equal("configs/geoip/GeoLite2-Country.mmdb", GeoIpConfig.DatabasePath);
        Assert.Equal(2, GeoIpConfig.DownloadUrls.Count);
        Assert.Equal(7, GeoIpConfig.MaxAgeDays);
        Assert.Null(GeoIpConfig.CountryLanguages);
    }

    [Fact]
    public void FileValuesAreUsedAndMissingKeysKeepTheirDefault()
    {
        Write("""
              {
                  // comments and trailing commas are fine
                  "Enabled": false,
                  "DatabasePath": "/data/country.mmdb",
                  "DownloadUrls": [],
                  "CountryLanguages": { "fr": ["BE"] },
              }
              """);

        GeoIpConfig.Load(_dir, NullLogger.Instance);

        Assert.False(GeoIpConfig.Enabled);
        Assert.True(GeoIpConfig.Notify);
        Assert.Equal("/data/country.mmdb", GeoIpConfig.DatabasePath);
        Assert.Empty(GeoIpConfig.DownloadUrls);
        Assert.Equal(7, GeoIpConfig.MaxAgeDays);
        Assert.Equal(new[] { "BE" }, GeoIpConfig.CountryLanguages!["fr"]);
    }

    [Fact]
    public void NullListsReadAsEmpty()
    {
        Write("""{ "DatabasePath": null, "DownloadUrls": null }""");

        GeoIpConfig.Load(_dir, NullLogger.Instance);

        Assert.Equal("", GeoIpConfig.DatabasePath);
        Assert.Empty(GeoIpConfig.DownloadUrls);
    }

    [Fact]
    public void BrokenFileKeepsTheSettingsInUse()
    {
        Write("""{ "MaxAgeDays": 30 }""");
        GeoIpConfig.Load(_dir, NullLogger.Instance);

        Write("""{ "MaxAgeDays": """);
        var logger = new CountingLogger();
        GeoIpConfig.Load(_dir, logger);

        Assert.Equal(30, GeoIpConfig.MaxAgeDays);
        Assert.Equal(1, logger.Warnings);
    }

    [Fact]
    public void UnknownKeysAreReported()
    {
        // The key name from before the settings moved out of core.json.
        Write("""{ "GeoIpLanguageEnabled": false }""");
        var logger = new CountingLogger();

        GeoIpConfig.Load(_dir, logger);

        Assert.True(GeoIpConfig.Enabled);
        Assert.Equal(1, logger.Warnings);
    }

    [Fact]
    public void ExampleFileMatchesTheDefaults()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Join(dir.FullName, "configs", "addons"))) dir = dir.Parent;
        Assert.NotNull(dir);

        var configs = Path.Join(dir.FullName, "configs", "addons", "counterstrikesharp", "configs");
        File.Copy(Path.Join(configs, "geoip.example.json"), Path.Join(_dir, GeoIpConfig.FileName));
        var logger = new CountingLogger();

        GeoIpConfig.Load(_dir, logger);

        var defaults = new GeoIpConfigData();
        Assert.Equal(0, logger.Warnings);
        Assert.Equal(defaults.Enabled, GeoIpConfig.Enabled);
        Assert.Equal(defaults.Notify, GeoIpConfig.Notify);
        Assert.Equal(defaults.DatabasePath, GeoIpConfig.DatabasePath);
        Assert.Equal(defaults.DownloadUrls, GeoIpConfig.DownloadUrls);
        Assert.Equal(defaults.MaxAgeDays, GeoIpConfig.MaxAgeDays);
        Assert.Empty(GeoIpConfig.CountryLanguages!);
    }
}
