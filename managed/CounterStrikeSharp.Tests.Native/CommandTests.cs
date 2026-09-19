using System;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using Moq;
using Xunit;

namespace NativeTestsPlugin;

public class CommandTests
{
    private readonly Mock<Action> _mock;
    private readonly FunctionReference _methodCallback;

    public CommandTests()
    {
        _mock = new Mock<Action>();
        _methodCallback = FunctionReference.Create(() => { _mock.Object.Invoke(); });
    }

    [Fact]
    public async Task AddCommandHandler()
    {
        NativeAPI.AddCommand("css_test_native", "description", true, (int)ConCommandFlags.FCVAR_LINKED_CONCOMMAND, _methodCallback);
        NativeAPI.IssueServerCommand("css_test_native");
        await WaitUntil(() => _mock.Invocations.Count >= 1);
        _mock.Verify(s => s(), Times.Once);

        NativeAPI.RemoveCommand("css_test_native", _methodCallback);
        NativeAPI.IssueServerCommand("css_test_native");
        await WaitFrames(4);
        _mock.Verify(s => s(), Times.Once);
        NativeAPI.RemoveCommand("css_test_native", _methodCallback);
    }

    [Fact]
    public async Task CanTriggerCommandsWithPublicChatTrigger()
    {
        var mock = new Mock<Action<int, IntPtr>>();
        var methodCallback = FunctionReference.Create((int playerSlot, IntPtr commandInfo) =>
        {
            var cmdInfo = new CommandInfo(commandInfo, null);
            Assert.Equal("css_test_public_chat", cmdInfo.GetArg(0));
            Assert.Equal(4, cmdInfo.ArgCount);
            Assert.Equal("1", cmdInfo.GetArg(1));
            Assert.Equal("2", cmdInfo.GetArg(2));
            Assert.Equal("3", cmdInfo.GetArg(3));
            mock.Object.Invoke(playerSlot, commandInfo);
        });

        NativeAPI.AddCommand("css_test_public_chat", "description", true, (int)ConCommandFlags.FCVAR_LINKED_CONCOMMAND, methodCallback);
        NativeAPI.IssueServerCommand("css_test_public_chat 1 2 3");
        await WaitUntil(() => mock.Invocations.Count >= 1);
        mock.Verify(s => s(It.IsAny<int>(), It.IsAny<IntPtr>()), Times.Once);
    }

    [Fact]
    public async Task CanTriggerCommandsWithSilentChatTrigger()
    {
        NativeAPI.AddCommand("css_test_silent_chat", "description", true, (int)ConCommandFlags.FCVAR_LINKED_CONCOMMAND, _methodCallback);
        // Issued commands run from the command buffer, which may not be processed by the next frame.
        NativeAPI.IssueServerCommand("css_test_silent_chat");
        await WaitUntil(() => _mock.Invocations.Count >= 1);
        _mock.Verify(s => s(), Times.Once);

        NativeAPI.IssueServerCommand("say \"!test_silent_chat\"");
        await WaitUntil(() => _mock.Invocations.Count >= 2);
        _mock.Verify(s => s(), Times.Exactly(2));
        NativeAPI.RemoveCommand("css_test_silent_chat", _methodCallback);
    }

    [Fact]
    public async Task IssueServerCommand()
    {
        bool called = false;
        NativeAPI.AddCommandListener("say", FunctionReference.Create(() => { called = true; }), true);

        NativeAPI.IssueServerCommand("say Hello, world!");
        await WaitUntil(() => called);

        Assert.True(called, "The 'say' command handler was not called.");
    }
}