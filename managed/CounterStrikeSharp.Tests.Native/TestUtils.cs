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

    // The smoke server has no human players. bot_join_after_player defaults to 1, which keeps the
    // bot quota from filling (and kicks added bots) until a human joins, so tests found no pawn.
    public const string BotSetupCommand = "bot_join_after_player 0; bot_quota_mode normal; bot_quota 5";

    /// <summary>
    /// Returns a bot with a live pawn, filling the bot quota first if there is none. Bots join and spawn
    /// over several frames, so a single WaitOneFrame finds none. Never bot_kick here: the kick lands after
    /// a bot_add from the same frame and removes the bot that was just added.
    /// </summary>
    public static async Task<CCSPlayerController> EnsureAliveBot(int maxFrames = 512)
    {
        CCSPlayerController? AliveBot() =>
            Utilities.GetPlayers().LastOrDefault(p => p.IsBot && p.PawnIsAlive && p.PlayerPawn.Value != null);

        if (AliveBot() is { } bot) return bot;

        Server.ExecuteCommand(BotSetupCommand);
        if (!await WaitUntil(() => AliveBot() != null, maxFrames))
        {
            throw new Exception($"No alive bot after '{BotSetupCommand}' within {maxFrames} frames.");
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