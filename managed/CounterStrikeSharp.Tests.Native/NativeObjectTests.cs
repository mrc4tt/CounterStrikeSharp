using System.Collections.Concurrent;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Xunit;

namespace NativeTestsPlugin;

public class NativeObjectsTests
{
    [Fact]
    public async Task EnsureNativeHandle_IsFreed_Vector3()
    {
        // LiveCount is process-wide and other plugins' vectors come and go while this runs, so follow
        // this test's own buffer instead of comparing counts.
        var released = new ConcurrentDictionary<IntPtr, bool>();
        Action<IntPtr> onReleased = pointer => released[pointer] = true;
        var handle = IntPtr.Zero;

        OwnedNativeBlock.Released += onReleased;

        try
        {
            await Server.NextFrameAsync(() =>
            {
                var vector = new Vector(0, 0, 500);
                Assert.Equal(IntPtr.Zero, vector.RawHandle);

                handle = vector.Handle;
                Assert.Equal(500, NativeAPI.VectorGetZ(handle));
                Assert.True(OwnedNativeBlock.LiveCount >= 1);
            });

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // Dead blocks wait out a grace period before they are released, see OwnedNativeBlock.
            Assert.False(released.ContainsKey(handle));

            await Task.Delay(1100);
            OwnedNativeBlock.FlushPending();
            Assert.True(released.ContainsKey(handle));
        }
        finally
        {
            OwnedNativeBlock.Released -= onReleased;
        }
    }
}
