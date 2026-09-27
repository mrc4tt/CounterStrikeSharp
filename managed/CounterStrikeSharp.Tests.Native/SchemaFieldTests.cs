using System.Runtime.CompilerServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using Xunit;

namespace NativeTestsPlugin;

public class SchemaFieldTests : IDisposable
{
    private static readonly SchemaField<int> Health = new("CBaseEntity", "m_iHealth");

    private readonly CBaseModelEntity entity;

    public SchemaFieldTests()
    {
        this.entity = Utilities.CreateEntityByName<CBaseModelEntity>("prop_dynamic")!;
    }

    [Fact]
    public void Offset_MatchesSchemaGetSchemaOffset()
    {
        Assert.Equal(Schema.GetSchemaOffset("CBaseEntity", "m_iHealth"), Health.Offset);
        Assert.True(Health.IsNetworked, "m_iHealth should be a networked field");
    }

    [Fact]
    public void Get_MatchesGeneratedProperty()
    {
        this.entity.Health = 42;

        Assert.Equal(42, Health.Get(this.entity));
        Assert.Equal(this.entity.Health, Health.Get(this.entity.Handle));
    }

    [Fact]
    public void Set_IsVisibleThroughGeneratedProperty()
    {
        // Networked field on an entity: exercises the write + SchemaSetStateChanged path.
        Health.Set(this.entity, 77);
        Assert.Equal(77, this.entity.Health);

        Health.Set(this.entity.Handle, 78);
        Assert.Equal(78, this.entity.Health);
    }

    [Fact]
    public void GetRef_AliasesGeneratedProperty()
    {
        this.entity.Health = 10;

        Health.GetRef(this.entity) += 5;
        Health.SetStateChanged(this.entity);

        Assert.Equal(15, this.entity.Health);
        Assert.True(Unsafe.AreSame(ref Health.GetRef(this.entity), ref this.entity.Health));
    }

    public void Dispose()
    {
        if (this.entity.IsValid) this.entity.Remove();
    }
}
