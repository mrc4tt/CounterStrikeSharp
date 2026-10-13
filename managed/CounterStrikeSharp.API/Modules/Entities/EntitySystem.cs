using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;

namespace CounterStrikeSharp.API.Modules.Entities;

public static class EntitySystem
{
    // The entity list lives for the whole process. Cached in a plain field instead of a Lazy<T>,
    // because it is read on every entity access.
    private static IntPtr _concreteEntityListPointer;

    private static IntPtr EntityListPointer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var pointer = _concreteEntityListPointer;
            if (pointer == IntPtr.Zero)
                _concreteEntityListPointer = pointer = NativeAPI.GetConcreteEntityListPointer();
            return pointer;
        }
    }

    private const int MaxEntities = 32768;
    private const int MaxEntitiesPerChunk = 512;
    private const int MaxChunks = MaxEntities / MaxEntitiesPerChunk;
    private const int SizeOfEntityIdentity = 0x70;
    private const int HandleOffset = 0x10;
    private const uint InvalidEHandleIndex = 0xFFFFFFFF;

    static unsafe Span<IntPtr> IdentityChunks => new((void*)EntityListPointer, MaxChunks);
    public static IntPtr FirstActiveEntity => Marshal.ReadIntPtr(EntityListPointer, MaxEntitiesPerChunk);

    public static IntPtr? GetEntityByHandle(uint raw)
    {
        return TryGetEntityPointer(raw, out var pointer) ? pointer : null;
    }

    public static IntPtr? GetEntityByHandle<T>(CHandle<T> handle) where T : NativeEntity
    {
        return TryGetEntityPointer(handle.Raw, out var pointer) ? pointer : null;
    }

    /// <summary>
    /// Entity pointer for a raw handle, or IntPtr.Zero when the handle is invalid or the entity is gone.
    /// Allocation-free: every schema read/write, IsValid and virtual call on an entity goes through this.
    /// </summary>
    internal static IntPtr GetEntityPointerByRawHandle(uint raw)
    {
        return TryGetEntityPointer(raw, out var pointer) ? pointer : IntPtr.Zero;
    }

    private static unsafe bool TryGetEntityPointer(uint raw, out IntPtr pointer)
    {
        pointer = IntPtr.Zero;

        uint index = raw & (Utilities.MaxEntities - 1);
        if (index == Utilities.MaxEntities - 1)
            return false;

        IntPtr pChunkToUse = ((IntPtr*)EntityListPointer)[index / MaxEntitiesPerChunk];
        if (pChunkToUse == IntPtr.Zero)
            return false;

        byte* pIdentity = (byte*)pChunkToUse + SizeOfEntityIdentity * (index % MaxEntitiesPerChunk);

        // The serial number must match, or the slot now holds a different entity.
        if (*(uint*)(pIdentity + HandleOffset) != raw)
            return false;

        pointer = *(IntPtr*)pIdentity;
        return true;
    }

    public static IntPtr? GetEntityByIndex(uint index)
    {
        if ((int)index <= -1 || index >= MaxEntities - 1) return null;

        IntPtr pChunkToUse = IdentityChunks[(int)(index / MaxEntitiesPerChunk)];
        if (pChunkToUse == IntPtr.Zero)
            return null;

        IntPtr pIdentityPtr = IntPtr.Add(pChunkToUse, SizeOfEntityIdentity * (int)(index % MaxEntitiesPerChunk));

        if (pIdentityPtr == IntPtr.Zero)
            return null;

        // Read the handle in place instead of wrapping it in a CEntityHandle (no allocation).
        uint foundIndex = (uint)Marshal.ReadInt32(pIdentityPtr + HandleOffset) & (Utilities.MaxEntities - 1);
        if (foundIndex != index)
            return null;

        return Marshal.ReadIntPtr(pIdentityPtr);
    }

    public static uint GetRawHandleFromEntityPointer(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
            return InvalidEHandleIndex;

        // Delegate to the native side which safely calls GetRefEHandle() on the
        // engine entity. The previous managed implementation chased raw pointers
        // through Schema (m_pEntity → identity + 0x10) which crashes with an
        // uncatchable AccessViolationException if the entity pointer is stale.
        return NativeAPI.GetRefFromEntityPointer(pointer);
    }

}
