using System.Linq;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using Moq;
using Xunit;

namespace NativeTestsPlugin;

public class EntityIOTests
{
    [Fact]
    public void GetDesignerName_ReturnsCorrectName()
    {
        var world = Utilities.FindAllEntitiesByDesignerName<CWorld>("worldent").FirstOrDefault();

        Assert.NotNull(world);
        Assert.Equal("worldent", world.DesignerName);
    }

    [Fact]
    public void GetEntityFromIndex_ReturnsValidPointer()
    {
        var world = Utilities.GetEntityFromIndex<CWorld>(0);
        Assert.NotNull(world);

        Assert.NotNull(world);
        Assert.Equal("worldent", world.DesignerName);
    }

    [Fact]
    public void GetEntityFromHandle_Works()
    {
        var world = Utilities.FindAllEntitiesByDesignerName<CWorld>("worldent").FirstOrDefault();

        Assert.NotNull(world);

        var worldFromHandle = world.EntityHandle.Get()!.As<CWorld>();

        Assert.Equal(world.Handle, worldFromHandle.Handle);
    }

    [Fact]
    public async Task AcceptInput_DoesNotThrow()
    {
        var entity = Utilities.CreateEntityByName<CBaseModelEntity>("prop_dynamic");

        Assert.NotNull(entity);

        var mock = new Mock<Action>();
        var callback = FunctionReference.Create(mock.Object);
        var isHooked = false;

        try
        {
            NativeAPI.HookEntityOutput("prop_dynamic", "OnUser1", callback, HookMode.Pre);
            isHooked = true;
            NativeAPI.AcceptInput(entity.Handle, "FireUser1", IntPtr.Zero, IntPtr.Zero, "", 0);
            await WaitOneFrame();

            Assert.Single(mock.Invocations);

            // Test unhook
            NativeAPI.UnhookEntityOutput("prop_dynamic", "OnUser1", callback, HookMode.Pre);
            isHooked = false;
            NativeAPI.AcceptInput(entity.Handle, "FireUser1", IntPtr.Zero, IntPtr.Zero, "", 0);

            await WaitOneFrame();
            Assert.Single(mock.Invocations);
        }
        finally
        {
            if (isHooked)
            {
                NativeAPI.UnhookEntityOutput("prop_dynamic", "OnUser1", callback, HookMode.Pre);
            }

            FunctionReference.Remove(callback.Identifier);
            entity.Remove();
        }
    }

    [Fact]
    public async Task AddEntityIOEvent_WithLegacyOutputId_FiresOutput()
    {
        var entity = Utilities.CreateEntityByName<CBaseModelEntity>("prop_dynamic");

        Assert.NotNull(entity);

        var mock = new Mock<Action>();
        var callback = FunctionReference.Create(mock.Object);
        var isHooked = false;

        try
        {
            NativeAPI.HookEntityOutput("prop_dynamic", "OnUser2", callback, HookMode.Pre);
            isHooked = true;
            entity.AddEntityIOEvent("FireUser2", outputId: 12345);
            await WaitOneFrame();

            Assert.Single(mock.Invocations);
        }
        finally
        {
            if (isHooked)
            {
                NativeAPI.UnhookEntityOutput("prop_dynamic", "OnUser2", callback, HookMode.Pre);
            }

            FunctionReference.Remove(callback.Identifier);
            entity.Remove();
        }
    }

    [Fact]
    public void EntityIOOutput_Description_MatchesTheOwningField()
    {
        var relay = Utilities.CreateEntityByName<CLogicRelay>("logic_relay");
        Assert.NotNull(relay);

        try
        {
            relay.DispatchSpawn();

            var description = relay.OnTrigger.Description;
            Assert.Equal("OnTrigger", description.Name);
            Assert.Equal((uint)Schema.GetSchemaOffset("CLogicRelay", "m_OnTrigger"), description.OutputOffset);
        }
        finally
        {
            relay.Remove();
        }
    }

    [Fact]
    public void EntityIOOutput_Connections_WalkWithoutReadingGarbage()
    {
        // Map-authored I/O: every connection must read back as a target input and the chain must end.
        var walked = 0;
        foreach (var relay in Utilities.FindAllEntitiesByDesignerName<CLogicRelay>("logic_relay"))
        {
            var hops = 0;
            for (var connection = relay.OnTrigger.Connections; connection != null; connection = connection.Next)
            {
                Assert.True(++hops < 1000, "connection list does not terminate");
                Assert.False(string.IsNullOrEmpty(connection.TargetInput));
                walked++;
            }
        }

        Console.WriteLine($"[EntityIOTests] walked {walked} logic_relay OnTrigger connection(s)");
    }
}
