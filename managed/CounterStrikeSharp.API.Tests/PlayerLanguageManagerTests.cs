using System.Globalization;
using CounterStrikeSharp.API.Core.Hosting;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CounterStrikeSharp.API.Tests;

public class PlayerLanguageManagerTests : IDisposable
{
    private readonly string _configsPath = Path.Join(Path.GetTempPath(), "css-lang-" + Guid.NewGuid().ToString("N"));

    public PlayerLanguageManagerTests()
    {
        Directory.CreateDirectory(_configsPath);
    }

    public void Dispose()
    {
        Directory.Delete(_configsPath, true);
    }

    private PlayerLanguageManager CreateManager()
    {
        var config = new Mock<IScriptHostConfiguration>();
        config.SetupGet(x => x.ConfigsPath).Returns(_configsPath);
        return new PlayerLanguageManager(config.Object, NullLogger<PlayerLanguageManager>.Instance);
    }

    [Fact]
    public void LanguageSurvivesRestart()
    {
        var steamId = new SteamID(76561197960265729UL);
        var first = CreateManager();
        first.SetLanguage(steamId, CultureInfo.GetCultureInfo("pl"));
        first.FlushSaves();

        Assert.Equal("pl", CreateManager().GetLanguage(steamId).Name);
    }

    [Fact]
    public void CorruptStoreIsIgnored()
    {
        File.WriteAllText(Path.Join(_configsPath, "player_languages.json"), "{ not json");

        var manager = CreateManager();
        var steamId = new SteamID(76561197960265729UL);
        manager.SetLanguage(steamId, CultureInfo.GetCultureInfo("de"));
        manager.FlushSaves();

        Assert.Equal("de", CreateManager().GetLanguage(steamId).Name);
    }

    [Fact]
    public void ResetForgetsChoiceAcrossRestart()
    {
        var steamId = new SteamID(76561197960265729UL);
        var manager = CreateManager();
        manager.SetLanguage(steamId, CultureInfo.GetCultureInfo("da"));
        Assert.Equal(PlayerLanguageSource.Manual, manager.GetLanguageSource(steamId));

        Assert.True(manager.ResetLanguage(steamId));
        Assert.False(manager.ResetLanguage(steamId));
        manager.FlushSaves();

        var restarted = CreateManager();
        Assert.Equal(PlayerLanguageSource.Server, restarted.GetLanguageSource(steamId));
        Assert.Equal(restarted.GetDefaultLanguage(), restarted.GetLanguage(steamId));
    }

    [Fact]
    public void LatestChoiceWinsWhenSavesQueueUp()
    {
        var steamId = new SteamID(76561197960265729UL);
        var manager = CreateManager();
        foreach (var language in new[] { "de", "pl", "sv", "da" })
            manager.SetLanguage(steamId, CultureInfo.GetCultureInfo(language));
        manager.FlushSaves();

        Assert.Equal("da", CreateManager().GetLanguage(steamId).Name);
    }

    [Fact]
    public void GuessWithoutGeoIpDoesNothing()
    {
        var steamId = new SteamID(76561197960265729UL);
        var manager = CreateManager();

        Assert.Null(manager.GuessLanguage(steamId, "80.1.2.3:27005"));
        Assert.Equal(PlayerLanguageSource.Server, manager.GetLanguageSource(steamId));
    }
}
