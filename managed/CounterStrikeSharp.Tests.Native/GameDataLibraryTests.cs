using System;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using Xunit;

namespace NativeTestsPlugin;

// A gamedata entry's "library" decides which binary its signature is scanned in. Plugins build
// memory functions from the signature text alone, so the text has to lead back to the library.
public class GameDataLibraryTests
{
    private const string Key = "__NativeTests_EngineLibraryEntry";
    private const string Text = "DE AD BE EF 13 37 ? ? 42";

    [Fact]
    public void ServerEntry_ResolvesToServerBinary()
    {
        Assert.Equal(Addresses.ServerPath, GameData.GetSignatureLibraryPath("Host_Say"));
        Assert.Null(GameData.NonServerLibraryPathForSignature(GameData.GetSignature("Host_Say")));
    }

    [Fact]
    public void EngineEntry_ResolvesToEngineBinary_ByKeyAndByText()
    {
        var methods = GameData.GameDataProvider.Methods;
        methods[Key] = new LoadedGameData
        {
            Signatures = new Signatures { Library = "engine", Linux = Text, Windows = Text }
        };
        GameData.InvalidateLibraryLookup();

        try
        {
            Assert.Equal(Addresses.EnginePath, GameData.GetSignatureLibraryPath(Key));
            Assert.Equal(Addresses.EnginePath, GameData.NonServerLibraryPathForSignature(Text));
        }
        finally
        {
            methods.Remove(Key);
            GameData.InvalidateLibraryLookup();
        }

        Assert.Null(GameData.NonServerLibraryPathForSignature(Text));
    }

    [Fact]
    public void UnknownLibrary_Throws()
    {
        var methods = GameData.GameDataProvider.Methods;
        methods[Key] = new LoadedGameData { Signatures = new Signatures { Library = "nosuchlib", Linux = Text, Windows = Text } };
        try
        {
            Assert.Throws<InvalidOperationException>(() => GameData.GetSignatureLibraryPath(Key));
        }
        finally
        {
            methods.Remove(Key);
            GameData.InvalidateLibraryLookup();
        }
    }
}
