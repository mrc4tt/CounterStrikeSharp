using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace CounterStrikeSharp.API;

public static class Marshaling
{
    public static ColorMarshaler ColorMarshaler = new();
}

public interface ICustomMarshal<T>
{
    T NativeToManaged(IntPtr pointer);

    void ManagedToNative(IntPtr pointer, T managedObj);
}

public class ColorMarshaler : ICustomMarshal<Color>
{
    // Source 2's Color is four bytes r, g, b, a. Read as a little-endian 32-bit value that is
    // R | G << 8 | B << 16 | A << 24, which is also how a by-value Color travels in a register.
    public static uint Pack(Color color) => (uint)((color.A << 24) | (color.B << 16) | (color.G << 8) | color.R);

    public static Color Unpack(uint packed) =>
        Color.FromArgb((byte)(packed >> 24), (byte)packed, (byte)(packed >> 8), (byte)(packed >> 16));

    public Color NativeToManaged(IntPtr pointer)
    {
        return Unpack((uint)Marshal.ReadInt32(pointer));
    }

    public void ManagedToNative(IntPtr pointer, Color managedObj)
    {
        Marshal.WriteInt32(pointer, (int)Pack(managedObj));
    }
}

internal interface IMarshalToNative
{
    // Returns the object format that will be passed to the native API when marshalled by the script context.
    IEnumerable<object> GetNativeObject();
}
