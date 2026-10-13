using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Core;

namespace CounterStrikeSharp.API.Tests;

// The primitive fast paths in ScriptContext.Push / GetResult must produce exactly the bytes and values
// the general Marshal.StructureToPtr / PtrToStructure path did, since natives read them as raw slots.
public class ScriptContextPrimitiveTests
{
    private enum IntEnum { A = -3, B = 7 }
    private enum ByteEnum : byte { A = 200 }
    private enum ULongEnum : ulong { A = ulong.MaxValue - 1 }
    private enum ShortEnum : short { A = -2 }

    public static IEnumerable<object[]> Values()
    {
        yield return new object[] { (IntPtr)0x1234_5678_9ABC };
        yield return new object[] { -42 };
        yield return new object[] { 0xFFFF_FFF0u };
        yield return new object[] { 3.5f };
        yield return new object[] { -1.25 };
        yield return new object[] { true };
        yield return new object[] { false };
        yield return new object[] { long.MinValue + 5 };
        yield return new object[] { ulong.MaxValue - 9 };
        yield return new object[] { (short)-7 };
        yield return new object[] { (ushort)65000 };
        yield return new object[] { (byte)250 };
        yield return new object[] { (sbyte)-100 };
        yield return new object[] { IntEnum.A };
        yield return new object[] { IntEnum.B };
        yield return new object[] { ByteEnum.A };
        yield return new object[] { ULongEnum.A };
        yield return new object[] { ShortEnum.A };
    }

    private static unsafe byte[] ReferenceSlot(object value)
    {
        // The pre-fast-path behaviour: enums converted to their underlying type, then StructureToPtr
        // into a zeroed 8-byte slot.
        if (value.GetType().IsEnum)
            value = Convert.ChangeType(value, value.GetType().GetEnumUnderlyingType());
        var slot = new byte[8];
        fixed (byte* p = slot)
            Marshal.StructureToPtr(value, (IntPtr)p, false);
        return slot;
    }

    [Theory]
    [MemberData(nameof(Values))]
    public unsafe void PushWritesTheSameBytesAsStructureToPtr(object value)
    {
        var context = new ScriptContext();
        context.Push(value);

        Assert.Equal(1, context.m_extContext.numArguments);
        var actual = new byte[8];
        fixed (fxScriptContext* cxt = &context.m_extContext)
            new ReadOnlySpan<byte>(cxt->functionData, 8).CopyTo(actual);

        Assert.Equal(ReferenceSlot(value), actual);
    }

    [Theory]
    [MemberData(nameof(Values))]
    public unsafe void GetResultReadsTheSameValueAsPtrToStructure(object value)
    {
        if (value.GetType().IsEnum) return; // enums take the unchanged Enum.ToObject path

        var slot = ReferenceSlot(value);
        var context = new ScriptContext();
        fixed (byte* p = slot)
        {
            var expected = Marshal.PtrToStructure((IntPtr)p, value.GetType());
            var actual = context.GetResult(value.GetType(), p);
            Assert.Equal(expected, actual);
        }
    }
}
