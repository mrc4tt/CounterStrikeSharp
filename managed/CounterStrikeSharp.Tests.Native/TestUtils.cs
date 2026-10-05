using System;
using System.Linq;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

public static class TestUtils
{
    public static async Task WaitOneFrame()
    {
        await Server.NextFrameAsync(() => { }).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until <paramref name="condition"/> holds, checking once per frame. Server commands issued by a
    /// test (bot_add, ...) run from the command buffer, which is not guaranteed to have been processed by
    /// the next frame, so waiting exactly one frame for their effects is a race.
    /// </summary>
    public static async Task<bool> WaitUntil(Func<bool> condition, int maxFrames = 64)
    {
        for (var i = 0; i < maxFrames; i++)
        {
            await WaitOneFrame();
            if (condition()) return true;
        }

        return condition();
    }

    /// <summary>
    /// Waits a fixed number of frames; used to show that something does NOT happen.
    /// </summary>
    public static async Task WaitFrames(int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            await WaitOneFrame();
        }
    }

    /// <summary>
    /// Issues <paramref name="setupCommand"/> (bot_add, bot_quota ...) and waits until a bot with a live pawn
    /// exists. Bots join and spawn over several frames, so a single WaitOneFrame finds none.
    /// </summary>
    public static async Task<CCSPlayerController> EnsureAliveBot(string setupCommand, int maxFrames = 512)
    {
        Server.ExecuteCommand(setupCommand);
        CCSPlayerController? AliveBot() =>
            Utilities.GetPlayers().LastOrDefault(p => p.IsBot && p.PawnIsAlive && p.PlayerPawn.Value != null);

        if (!await WaitUntil(() => AliveBot() != null, maxFrames))
        {
            throw new Exception($"No alive bot after '{setupCommand}' within {maxFrames} frames.");
        }

        return AliveBot()!;
    }

    public static async Task WaitForSeconds(float seconds)
    {
        var startTick = Server.TickCount;
        var ticksToWait = (int)(seconds / Server.TickInterval);
        var targetTick = startTick + ticksToWait;
        await Server.RunOnTickAsync(targetTick, () => { }).ConfigureAwait(false);
    }
}