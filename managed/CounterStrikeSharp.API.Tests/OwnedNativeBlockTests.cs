using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Modules.Utils;

namespace CounterStrikeSharp.API.Tests;

// OwnedNativeBlock.LiveCount is process-wide: run alone so other classes' Vector/QAngle use cannot skew it.
[CollectionDefinition(nameof(OwnedNativeBlockTests), DisableParallelization = true)]
public class OwnedNativeBlockCollection
{
}

[Collection(nameof(OwnedNativeBlockTests))]
public class OwnedNativeBlockTests
{
    private static float ReadFloat(IntPtr pointer, int index)
    {
        return BitConverter.Int32BitsToSingle(Marshal.ReadInt32(pointer, index * sizeof(float)));
    }

    [Fact]
    public void Handle_IsSeededFromManagedValues()
    {
        var vector = new Vector(1, 2, 3);
        var handle = vector.Handle;

        Assert.NotEqual(IntPtr.Zero, handle);
        Assert.Equal(1, ReadFloat(handle, 0));
        Assert.Equal(2, ReadFloat(handle, 1));
        Assert.Equal(3, ReadFloat(handle, 2));

        GC.KeepAlive(vector);
    }

    [Fact]
    public void Handle_IsStableAndBacksTheAccessors()
    {
        var quaternion = new Quaternion(1, 2, 3, 4);
        var handle = quaternion.Handle;

        quaternion.W = 9;

        Assert.Equal(handle, quaternion.Handle);
        Assert.Equal(9, ReadFloat(handle, 3));

        GC.KeepAlive(quaternion);
    }

    [Fact]
    public void ConcurrentFirstUse_PublishesOneHandle()
    {
        for (var i = 0; i < 200; i++)
        {
            var angle = new QAngle(i, 0, 0);
            var handles = new IntPtr[8];

            Parallel.For(0, handles.Length, n => handles[n] = angle.Handle);

            Assert.All(handles, h => Assert.Equal(handles[0], h));
            Assert.Equal(i, ReadFloat(handles[0], 0));

            GC.KeepAlive(angle);
        }
    }

    [Fact]
    public void CollectedWrappers_ReleaseWithoutCorruptingTheHeap()
    {
        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < 50_000; i++)
            {
                _ = new Vector(i, i, i).Handle;
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        var survivor = new Vector(7, 8, 9);
        Assert.Equal(9, ReadFloat(survivor.Handle, 2));

        GC.KeepAlive(survivor);
    }

    // Upstream issue 1486: a plugin allocating Vector/QAngle every tick outran NativeHandleTracker's
    // sweep, and its list of dead entries (each still holding its HGlobal) grew until OOM. Here each buffer
    // is released by its own block's finalizer, so live buffers must track the allocation rate over the
    // grace period, not the total ever allocated. Steady ~200k/s (100x the issue's rate) for 3 s, no
    // forced GC during the run.
    [Fact]
    public void SteadyAllocation_DoesNotAccumulate()
    {
        const int perBatch = 2_000;
        var total = 0L;
        var peakLastSecond = 0L;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        while (clock.Elapsed < TimeSpan.FromSeconds(3))
        {
            for (var i = 0; i < perBatch; i++)
            {
                _ = new Vector(i, i, i).Handle;
                _ = new QAngle(i, i, i).Handle;
            }
            total += perBatch * 2;

            if (clock.Elapsed > TimeSpan.FromSeconds(2))
                peakLastSecond = Math.Max(peakLastSecond, OwnedNativeBlock.LiveCount);

            Thread.Sleep(10);
        }

        // Bounded by roughly what was allocated in the last ~2 s (1 s grace + GC/finalizer lag),
        // never the whole run.
        Assert.True(peakLastSecond < total * 3 / 4, $"live {peakLastSecond} of {total} allocated");

        // And everything dead is released one grace period after its finalizer ran. The grace clock starts
        // at finalization, so blocks finalized only by this collection need a full second of their own.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Thread.Sleep(1100);
        OwnedNativeBlock.FlushPending();
        Assert.True(OwnedNativeBlock.LiveCount < 10_000, $"still live after drain: {OwnedNativeBlock.LiveCount} of {total}");
    }
}
