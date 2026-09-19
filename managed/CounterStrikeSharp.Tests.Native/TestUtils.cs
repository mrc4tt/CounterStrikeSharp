using System;
using System.Threading.Tasks;
using CounterStrikeSharp.API;

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

    public static async Task WaitForSeconds(float seconds)
    {
        var startTick = Server.TickCount;
        var ticksToWait = (int)(seconds / Server.TickInterval);
        var targetTick = startTick + ticksToWait;
        await Server.RunOnTickAsync(targetTick, () => { }).ConfigureAwait(false);
    }
}