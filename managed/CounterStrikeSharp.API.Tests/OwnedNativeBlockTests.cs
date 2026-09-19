using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Modules.Utils;

namespace CounterStrikeSharp.API.Tests;

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
}
