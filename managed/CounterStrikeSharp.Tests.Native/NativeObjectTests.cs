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
        var liveBefore = OwnedNativeBlock.LiveCount;

        await Server.NextFrameAsync(() =>
        {
            var vector = new Vector(0, 0, 500);
            Assert.Equal(IntPtr.Zero, vector.RawHandle);
            Assert.Equal(500, NativeAPI.VectorGetZ(vector.Handle));
            Assert.Equal(liveBefore + 1, OwnedNativeBlock.LiveCount);
        });

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Dead blocks wait out a grace period before they are released, see OwnedNativeBlock.
        await Task.Delay(1100);
        OwnedNativeBlock.FlushPending();
        Assert.Equal(liveBefore, OwnedNativeBlock.LiveCount);
    }
}