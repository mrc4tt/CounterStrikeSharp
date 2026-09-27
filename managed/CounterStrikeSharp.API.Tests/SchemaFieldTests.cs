using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Modules.Memory;

namespace CounterStrikeSharp.API.Tests;

// Exercises SchemaField<T> read/write paths off-server through the internal pre-resolved
// constructor (no native schema lookup). Resolution and state-changed notification need a
// live server and are covered by NativeTestsPlugin's SchemaFieldTests.
public class SchemaFieldTests : IDisposable
{
    private const int BufferSize = 64;
    private readonly IntPtr _buffer = Marshal.AllocHGlobal(BufferSize);

    public SchemaFieldTests()
    {
        for (var i = 0; i < BufferSize; i++) Marshal.WriteByte(_buffer, i, 0);
    }

    public void Dispose() => Marshal.FreeHGlobal(_buffer);

    private sealed class TestObject : NativeObject
    {
        public TestObject(IntPtr pointer) : base(pointer)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Pair
    {
        public float A;
        public int B;
    }

    private enum TestEnum : int
    {
        Zero = 0,
        Seven = 7,
    }

    [Fact]
    public void Constructor_DoesNotResolve()
    {
        // Would hit NativeAPI (and fail off-server) if the ctor resolved eagerly.
        var field = new SchemaField<int>("CBaseEntity", "m_iHealth");

        Assert.Equal("CBaseEntity", field.ClassName);
        Assert.Equal("m_iHealth", field.FieldName);
        Assert.Equal("CBaseEntity::m_iHealth", field.ToString());
    }

    [Fact]
    public void Get_ReadsAtOffset()
    {
        Marshal.WriteInt32(_buffer, 12, 1234);
        var field = new SchemaField<int>("C", "m_x", 12);

        Assert.Equal(12, field.Offset);
        Assert.Equal(1234, field.Get(_buffer));
        Assert.Equal(1234, field.Get(new TestObject(_buffer)));
    }

    [Fact]
    public void Set_WritesAtOffset_AndDoesNotTouchNeighbours()
    {
        var field = new SchemaField<int>("C", "m_x", 8);

        field.Set(_buffer, -5);
        field.Set(new TestObject(_buffer), 99);

        Assert.Equal(99, Marshal.ReadInt32(_buffer, 8));
        Assert.Equal(0, Marshal.ReadInt32(_buffer, 4));
        Assert.Equal(0, Marshal.ReadInt32(_buffer, 12));
    }

    [Fact]
    public void GetRef_AliasesNativeMemory()
    {
        var field = new SchemaField<float>("C", "m_f", 16);

        ref var value = ref field.GetRef(_buffer);
        value = 2.5f;

        Assert.Equal(2.5f, BitConverter.Int32BitsToSingle(Marshal.ReadInt32(_buffer, 16)));

        field.GetRef(new TestObject(_buffer)) += 1f;
        Assert.Equal(3.5f, field.Get(_buffer));
    }

    [Fact]
    public void BlittableStructsEnumsAndBools_RoundTrip()
    {
        var pair = new SchemaField<Pair>("C", "m_pair", 20);
        var @enum = new SchemaField<TestEnum>("C", "m_enum", 28);
        var flag = new SchemaField<bool>("C", "m_flag", 32);

        pair.Set(_buffer, new Pair { A = 1.25f, B = 42 });
        @enum.Set(_buffer, TestEnum.Seven);
        flag.Set(_buffer, true);

        Assert.Equal(1.25f, pair.Get(_buffer).A);
        Assert.Equal(42, pair.Get(_buffer).B);
        Assert.Equal(7, Marshal.ReadInt32(_buffer, 28));
        Assert.Equal(TestEnum.Seven, @enum.Get(_buffer));
        Assert.Equal(1, Marshal.ReadByte(_buffer, 32));
        Assert.True(flag.Get(_buffer));
    }

    [Fact]
    public void NullHandle_Throws()
    {
        var field = new SchemaField<int>("C", "m_x", 0);

        Assert.Throws<ArgumentNullException>(() => field.Get(IntPtr.Zero));
        Assert.Throws<ArgumentNullException>(() => field.Set(IntPtr.Zero, 1));
        Assert.Throws<ArgumentNullException>(() => field.GetRef(IntPtr.Zero));
        Assert.Throws<ArgumentNullException>(() => field.Get(new TestObject(IntPtr.Zero)));
        Assert.Throws<ArgumentNullException>(() => field.Get((NativeObject)null!));
        Assert.Throws<ArgumentNullException>(() => field.SetStateChanged(new TestObject(IntPtr.Zero)));
    }

    [Fact]
    public void GetRef_ReferenceType_Throws()
    {
        var field = new SchemaField<string>("C", "m_s", 0);

        Assert.Throws<NotSupportedException>(() => field.GetRef(_buffer));
    }

    [Fact]
    public void BlockedField_ThrowsWhileGuidelinesEnabled()
    {
        // CoreConfig.FollowCS2ServerGuidelines defaults to true.
        Assert.True(Core.CoreConfig.FollowCS2ServerGuidelines);
        var field = new SchemaField<int>("CEconItemView", "m_iAccountID", 4, blocked: true);

        var ex = Assert.Throws<Exception>(() => field.Get(_buffer));
        Assert.Contains("FollowCS2ServerGuidelines", ex.Message);
        Assert.Throws<Exception>(() => field.Set(_buffer, 1));
        Assert.Throws<Exception>(() => field.Offset);
        Assert.Equal(0, Marshal.ReadInt32(_buffer, 4));
    }

    [Fact]
    public void NonNetworkedField_SetStateChanged_IsNoOp()
    {
        var field = new SchemaField<int>("C", "m_x", 0, networked: false);

        Assert.False(field.IsNetworked);
        field.SetStateChanged(new TestObject(_buffer)); // would call into native if it tried to notify
    }
}
