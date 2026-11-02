using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Modules.Entities;
using FastGenericNew;
using Microsoft.Extensions.DependencyInjection;

namespace CounterStrikeSharp.API.Modules.Utils;

public readonly record struct CEntityIndex(uint Value)
{
    public override string ToString() => $"Entity Index {Value}";
}

/// <summary>
/// CHandle is a class that represents a 32-bit ID (entindex + serial number) unique to every past and present entity in a game.
/// It is used to refer to entities where pointers and entity indexes are unsafe; mainly across the client/server divide.
/// <a href="https://developer.valvesoftware.com/wiki/CHandle">More info</a>
/// </summary>
/// <typeparam name="T">Type of entity this handle refers to</typeparam>
public class CHandle<T> : IEquatable<CHandle<T>> where T : NativeEntity
{
    private uint _raw;
    private IntPtr? _pointer;

    public uint Raw
    {
        get
        {
            if (_pointer != null)
            {
                return (uint)Marshal.ReadInt32(_pointer.Value);
            }

            return _raw;
        }
        set
        {
            if (_pointer != null)
            {
                Marshal.WriteInt32(_pointer.Value, (int)value);
                return;
            }

            _raw = value;
        }
    }

    public CHandle(uint raw)
    {
        Raw = raw;
    }

    public CHandle(IntPtr raw)
    {
        _pointer = raw;
    }


    /// <summary>
    /// Gets the entity this handle refers to (see <see cref="Get"/>), or points the handle at
    /// another entity. Setting <c>null</c> writes the engine's invalid handle (<c>0xFFFFFFFF</c>).
    /// </summary>
    /// <remarks>
    /// When this handle wraps an entity's schema field, the setter writes game memory directly and
    /// does not mark the field as changed: for a networked field, call
    /// <c>Utilities.SetStateChanged</c> afterwards or clients will not see the new value.
    /// </remarks>
    public T? Value
    {
        get => Get();
        // INVALID_EHANDLE_INDEX, not MaxEntities - 1: IsValid accepts either, but the engine
        // compares against 0xFFFFFFFF, so a 0x7FFF written into game memory reads as a live
        // handle to slot 32767 there.
        set => Raw = value?.EntityHandle.Raw ?? 0xFFFFFFFF;
    }

    /// <summary>
    /// Retrieves the instance of the entity this handle refers to.
    /// </summary>
    public T? Get()
    {
        if (!IsValid)
            return null;

        var entity = EntitySystem.GetEntityByHandle(this);
        if (entity == null)
            return null;

        return FastNew.CreateInstance<T, IntPtr>(entity.Value);
    }

    public override string ToString() => IsValid ? $"Index = {Index}, Serial = {SerialNum}" : "<invalid>";

    /// <summary>
    /// Checks that the handle is valid and points to an entity.
    /// </summary>
    public bool IsValid => Index != (Utilities.MaxEntities - 1);

    public uint Index => (uint)(Raw & (Utilities.MaxEntities - 1));
    public uint SerialNum => Raw >> Utilities.MaxEntityBits;

    public static implicit operator uint(CHandle<T> handle) => handle.Raw;

    /// <summary>
    /// Resolves the handle to its entity, or <c>null</c> when the handle (or the entity) is gone.
    /// Each conversion is an entity lookup plus a new wrapper object.
    /// </summary>
    public static implicit operator T?(CHandle<T>? handle) => handle?.Value;

    public bool Equals(CHandle<T>? other)
    {
        return other != null && Raw == other.Raw;
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as CHandle<T>);
    }

    public override int GetHashCode()
    {
        return (int)Raw;
    }
}

public class CEntityHandle : CHandle<CEntityInstance>
{
    public CEntityHandle(uint raw) : base(raw)
    {
    }

    public CEntityHandle(IntPtr raw) : base(raw)
    {
    }
}

public class PointerTo<T> : NativeObject where T : NativeObject
{
    public PointerTo(IntPtr pointer) : base(pointer)
    {
    }

    public T Value
    {
        get
        {
            unsafe
            {
                return FastNew.CreateInstance<T, IntPtr>(Unsafe.Read<IntPtr>((void*)Handle));
            }
        }
    }
}
