using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using Moq;
using Xunit;

namespace NativeTestsPlugin;

public class TimerTests
{
    [Fact]
    public async Task CreateTimer_ExecutesCallback()
    {
        await WaitOneFrame();

        var callCount = 0;
        var callback = FunctionReference.Create(() => { callCount++; });

        var timerPtr = NativeAPI.CreateTimer(0.1f, callback, 0);

        try
        {
            Assert.NotEqual(IntPtr.Zero, timerPtr);

            // Wait for the timer to fire (0.1 seconds + some buffer)
            await WaitForSeconds(0.2f);
            await WaitOneFrame();

            Assert.True(callCount >= 1, $"Timer callback should have been called at least once, got {callCount}");
        }
        finally
        {
            NativeAPI.KillTimer(timerPtr);
        }
    }

    [Fact]
    public async Task CreateTimer_RepeatsWithFlag()
    {
        await WaitOneFrame();

        var callCount = 0;
        var callback = FunctionReference.Create(() => { callCount++; });

        var timerPtr = NativeAPI.CreateTimer(0.05f, callback, (int)TimerFlags.REPEAT);

        try
        {
            Assert.NotEqual(IntPtr.Zero, timerPtr);

            await WaitForSeconds(0.250f);
            await WaitOneFrame();

            Assert.True(callCount >= 2, $"Repeating timer should have been called multiple times, got {callCount}");
        }
        finally
        {
            NativeAPI.KillTimer(timerPtr);
        }
    }

    [Fact]
    public async Task CreateTimer_TickBased()
    {
        var startTick = Server.TickCount;
        var tickCounts = new List<int>();
        int timesTicked = 0;

        // Run every 4 ticks
        var timer = new Timer(4 * Server.TickInterval, () =>
        {
            timesTicked++;
            tickCounts.Add(Server.TickCount);
        }, TimerFlags.REPEAT);

        await Server.RunOnTickAsync(startTick + 16, () => { });
        timer.Kill();

        Assert.Equal(4, timesTicked);
        for (int i = 0; i < tickCounts.Count; i++)
        {
            var expectedTick = startTick + (i + 1) * 4;
            Assert.Equal(expectedTick, tickCounts[i]);
        }
    }

    [Fact]
    public async Task KillTimer_StopsExecution()
    {
        await WaitOneFrame();

        var callCount = 0;
        var callback = FunctionReference.Create(() => { callCount++; });

        var timerPtr = NativeAPI.CreateTimer(0.05f, callback, (int)TimerFlags.REPEAT);

        // Wait for at least one call
        await WaitForSeconds(0.10f);
        await WaitOneFrame();

        var countBeforeKill = callCount;

        // Kill the timer
        NativeAPI.KillTimer(timerPtr);

        // Wait a bit more
        await WaitForSeconds(0.15f);
        await WaitOneFrame();

        var countAfterKill = callCount;

        Assert.True(countAfterKill <= countBeforeKill + 1,
            $"Timer should stop after kill. Before: {countBeforeKill}, After: {countAfterKill}");
    }

    [Fact]
    public async Task Timer_HighLevelApi_Works()
    {
        await WaitOneFrame();

        var callCount = 0;
        var timer = new Timer(0.1f, () => { callCount++; });

        try
        {
            // Wait for the timer to fire
            await WaitForSeconds(0.3f);
            await WaitOneFrame();

            Assert.True(callCount >= 1, $"Timer callback should have been called, got {callCount}");
        }
        finally
        {
            timer.Kill();
        }
    }

    // Kills issued from inside a timer callback: KillTimer only flags, and RunFrame deletes the
    // flagged timers after it has walked both lists (TimerSystem::SweepKilledTimers).

    [Fact]
    public async Task OnceOff_KillsOlderOnceOff_FromCallback_KeepsNewerTimer()
    {
        await WaitOneFrame();

        var olderFired = 0;
        var killerFired = 0;
        var newerFired = 0;

        // Creation order matters: the killer sits between its victim and a newer, not-yet-due timer.
        Timer? older = null;
        older = new Timer(0.5f, () => { olderFired++; });
        var killer = new Timer(0.1f, () => { killerFired++; older!.Kill(); });
        var newer = new Timer(0.3f, () => { newerFired++; });

        try
        {
            await WaitForSeconds(0.7f);
            await WaitOneFrame();

            Assert.Equal(1, killerFired);
            Assert.Equal(0, olderFired);
            Assert.Equal(1, newerFired);
        }
        finally
        {
            older.Kill();
            killer.Kill();
            newer.Kill();
        }
    }

    [Fact]
    public async Task OnceOff_KillsOlderOnceOff_FromCallback_WhenKillerIsLast()
    {
        await WaitOneFrame();

        var olderFired = 0;
        var killerFired = 0;

        Timer? older = null;
        older = new Timer(0.5f, () => { olderFired++; });
        var killer = new Timer(0.1f, () => { killerFired++; older!.Kill(); });

        try
        {
            await WaitForSeconds(0.7f);
            await WaitOneFrame();

            Assert.Equal(1, killerFired);
            Assert.Equal(0, olderFired);
        }
        finally
        {
            older.Kill();
            killer.Kill();
        }
    }

    [Fact]
    public async Task Repeat_KillsOlderRepeatAndSelf_FromCallback_KeepsNewerTimer()
    {
        await WaitOneFrame();

        var olderFired = 0;
        var killerFired = 0;
        var newerFired = 0;

        Timer? older = null;
        Timer? killer = null;
        older = new Timer(0.05f, () => { olderFired++; }, TimerFlags.REPEAT);
        killer = new Timer(0.05f, () =>
        {
            killerFired++;
            older!.Kill();
            killer!.Kill();
        }, TimerFlags.REPEAT);
        var newer = new Timer(0.1f, () => { newerFired++; }, TimerFlags.REPEAT);

        try
        {
            await WaitForSeconds(0.5f);
            await WaitOneFrame();

            Assert.Equal(1, killerFired);
            Assert.True(olderFired <= 1, $"Older repeat timer should stop once killed, fired {olderFired} times");
            Assert.True(newerFired >= 3, $"Newer repeat timer must keep running, fired {newerFired} times");
        }
        finally
        {
            older.Kill();
            killer.Kill();
            newer.Kill();
        }
    }

    [Theory]
    [InlineData(0.05f)]
    [InlineData(0.001f)] // shorter than a tick: the case that used to fire twice in one frame
    public async Task Repeat_KillsOlderRepeat_FromCallback_DoesNotDoubleFire(float interval)
    {
        await WaitOneFrame();

        var olderFired = 0;
        var killerTicks = new List<int>();

        Timer? older = null;
        older = new Timer(interval, () => { olderFired++; }, TimerFlags.REPEAT);
        var killer = new Timer(interval, () =>
        {
            killerTicks.Add(Server.TickCount);
            older!.Kill();
        }, TimerFlags.REPEAT);

        try
        {
            await WaitForSeconds(0.5f);
            await WaitOneFrame();

            Assert.True(olderFired <= 1, $"Older repeat timer should stop once killed, fired {olderFired} times");
            Assert.True(killerTicks.Count >= 3, $"Killer should keep repeating, fired {killerTicks.Count} times");
            Assert.Equal(killerTicks.Count, killerTicks.Distinct().Count());
        }
        finally
        {
            older.Kill();
            killer.Kill();
        }
    }

    [Fact]
    public async Task Timer_CreatedInsideCallback_FiresOnLaterFrame()
    {
        await WaitOneFrame();

        var outerTick = -1;
        var innerTick = -1;
        Timer? inner = null;

        var outer = new Timer(0.1f, () =>
        {
            outerTick = Server.TickCount;
            inner = new Timer(0.0f, () => { innerTick = Server.TickCount; });
        });

        try
        {
            await WaitForSeconds(0.3f);
            await WaitOneFrame();

            Assert.NotEqual(-1, outerTick);
            Assert.NotEqual(-1, innerTick);
            Assert.True(innerTick > outerTick, $"Inner timer fired on tick {innerTick}, outer on {outerTick}");
        }
        finally
        {
            outer.Kill();
            inner?.Kill();
        }
    }
}
