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
        CreateManager().SetLanguage(steamId, CultureInfo.GetCultureInfo("pl"));

        Assert.Equal("pl", CreateManager().GetLanguage(steamId).Name);
    }

    [Fact]
    public void CorruptStoreIsIgnored()
    {
        File.WriteAllText(Path.Join(_configsPath, "player_languages.json"), "{ not json");

        var manager = CreateManager();
        var steamId = new SteamID(76561197960265729UL);
        manager.SetLanguage(steamId, CultureInfo.GetCultureInfo("de"));

        Assert.Equal("de", CreateManager().GetLanguage(steamId).Name);
    }
}
