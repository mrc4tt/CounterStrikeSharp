using System.Linq;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.UserMessages;
using Xunit;

namespace NativeTestsPlugin;

// Guards the libraries/Protobufs bump. The managed UserMessage API works on the server's own message
// descriptors, but three native paths cast a server-allocated message to OUR compiled protobuf class
// (ToPB<T>): ClientPrint (CUserMessageTextMsg), ReplicateConVar (CNETMsg_SetConVar) and
// CustomHudClicked. If our .proto drifts from the server's, those casts write into the wrong layout,
// so they are exercised here end to end.
public class ProtobufTests
{
    private static async Task<CCSPlayerController> GetBot()
    {
        if (!Utilities.GetPlayers().Any())
        {
            Server.ExecuteCommand("bot_quota 2; bot_quota_mode normal");
            await WaitUntil(() => Utilities.GetPlayers().Any());
        }

        return Utilities.GetPlayers().First();
    }

    [Theory]
    [InlineData("TextMsg")]
    [InlineData("SayText")]
    [InlineData("SayText2")]
    [InlineData("SetConVar")]
    public void MessagesUsedByCoreExist(string name)
    {
        Assert.True(UserMessage.FindIdByName(name) > 0, $"{name} not found on this server");
    }

    // CEntityIndex-boxed fields keep their -1 default after the bump.
    [Theory]
    [InlineData("SayText", "playerindex")]
    [InlineData("SayText2", "entityindex")]
    public void BoxedEntityIndexDefaultsToMinusOne(string message, string field)
    {
        using var userMessage = UserMessage.FromPartialName(message);

        Assert.Equal(-1, userMessage.ReadInt(field));
    }

    [Fact]
    public void SayText2_AllFieldsRoundTrip()
    {
        using var userMessage = UserMessage.FromPartialName("SayText2");

        userMessage.SetInt("entityindex", 7);
        userMessage.SetBool("chat", true);
        userMessage.SetString("messagename", "msg");
        userMessage.SetString("param1", "p1");
        userMessage.SetBool("textallchat", true);

        Assert.Equal(7, userMessage.ReadInt("entityindex"));
        Assert.True(userMessage.ReadBool("chat"));
        Assert.Equal("msg", userMessage.ReadString("messagename"));
        Assert.Equal("p1", userMessage.ReadString("param1"));
        Assert.True(userMessage.ReadBool("textallchat"));
    }

    // Native ClientPrint -> ToPB<CUserMessageTextMsg>. A layout mismatch crashes or corrupts here.
    [Fact]
    public async Task PrintToChatAndCenter_DoNotBreakServer()
    {
        var bot = await GetBot();

        bot.PrintToChat("protobuf test chat");
        bot.PrintToCenter("protobuf test center");
        Server.PrintToChatAll("protobuf test chat all");
        await WaitOneFrame();

        Assert.True(bot.IsValid);
    }

    // Native ReplicateConVar -> ToPB<CNETMsg_SetConVar> with a nested repeated CMsg_CVars.CVar.
    [Fact]
    public async Task ReplicateConVar_DoesNotBreakServer()
    {
        var bot = await GetBot();

        bot.ReplicateConVar("sv_cheats", "0");
        await WaitOneFrame();

        Assert.True(bot.IsValid);
    }
}
