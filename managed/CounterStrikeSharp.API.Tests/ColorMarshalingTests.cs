using System.Drawing;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Modules.Memory;

namespace CounterStrikeSharp.API.Tests;

public class ColorMarshalingTests
{
    [Fact]
    public void PackMatchesSourceTwoByteLayout()
    {
        // Source 2 Color is { r, g, b, a } in memory.
        var packed = ColorMarshaler.Pack(Color.FromArgb(0x44, 0x11, 0x22, 0x33));

        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44 }, BitConverter.GetBytes(packed));
    }

    [Theory]
    [InlineData(255, 255, 0, 0)]
    [InlineData(128, 1, 2, 3)]
    [InlineData(0, 255, 255, 255)]
    public void PackUnpackRoundTrips(int a, int r, int g, int b)
    {
        var color = Color.FromArgb(a, r, g, b);

        Assert.Equal(color.ToArgb(), ColorMarshaler.Unpack(ColorMarshaler.Pack(color)).ToArgb());
    }

    [Fact]
    public void MarshalerAgreesWithPackedValue()
    {
        var color = Color.FromArgb(10, 20, 30, 40);
        var buffer = Marshal.AllocHGlobal(4);
        try
        {
            Marshaling.ColorMarshaler.ManagedToNative(buffer, color);

            Assert.Equal(ColorMarshaler.Pack(color), (uint)Marshal.ReadInt32(buffer));
            Assert.Equal(color.ToArgb(), Marshaling.ColorMarshaler.NativeToManaged(buffer).ToArgb());
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void ColorIsAValidNativeDataType()
    {
        Assert.Equal(DataType.DATA_TYPE_UINT, typeof(Color).ToValidDataType());
        Assert.Equal(DataType.DATA_TYPE_UINT, typeof(Color?).ToValidDataType());
    }
}
