using System.Threading.Tasks;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Xunit;
using static TestUtils;

namespace NativeTestsPlugin;

public class CHandleTests
{
    private const uint InvalidEHandle = 0xFFFFFFFF;

    [Fact]
    public async Task CHandle_ValueSetter_OnStandaloneHandle()
    {
        var controller = await EnsureAliveBot();

        var handle = new CHandle<CCSPlayerController>(InvalidEHandle);
        Assert.False(handle.IsValid);
        CCSPlayerController? converted = handle;
        Assert.Null(converted);

        handle.Value = controller;
        Assert.Equal(controller.EntityHandle.Raw, handle.Raw);
        converted = handle;
        Assert.NotNull(converted);
        Assert.Equal(controller.Handle, converted.Handle);

        handle.Value = null;
        Assert.Equal(InvalidEHandle, handle.Raw);
        Assert.False(handle.IsValid);

        CHandle<CCSPlayerController>? none = null;
        converted = none;
        Assert.Null(converted);
    }

    [Fact]
    public async Task CHandle_ValueSetter_WritesSchemaField()
    {
        var controller = await EnsureAliveBot();
        var pawn = controller.PlayerPawn.Value!;
        uint original = pawn.EffectEntity.Raw;

        try
        {
            pawn.EffectEntity.Value = controller;
            // A fresh wrapper reads the field back from game memory.
            Assert.Equal(controller.EntityHandle.Raw, pawn.EffectEntity.Raw);
            CBaseEntity? resolved = pawn.EffectEntity;
            Assert.NotNull(resolved);
            Assert.Equal(controller.Handle, resolved.Handle);

            pawn.EffectEntity.Value = null;
            Assert.Equal(InvalidEHandle, pawn.EffectEntity.Raw);
            Assert.Null(pawn.EffectEntity.Value);
        }
        finally
        {
            pawn.EffectEntity.Raw = original;
        }
    }
}
