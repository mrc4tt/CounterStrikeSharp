using System.Runtime.InteropServices;
using System.Threading;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;

namespace CounterStrikeSharp.API;

public abstract class NativeEntity : NativeObject
{
    // The raw entity handle, captured at construction. The CEntityHandle wrapper used to be built
    // eagerly for every entity wrapper (every CHandle<T>.Value, every GetPlayers() entry...): it was
    // the single largest allocation by type on a profiled 20-player server, though almost every
    // caller only needs the raw value (IsValid, Index, Handle, Equals). It is now created on the
    // first EntityHandle read and the same instance is returned afterwards, as before.
    private readonly uint _rawEntityHandle;
    private CEntityHandle? _entityHandle;

    public new IntPtr Handle => EntitySystem.GetEntityPointerByRawHandle(RawEntityHandle);

    public CEntityHandle EntityHandle
    {
        get
        {
            var handle = _entityHandle;
            if (handle != null) return handle;

            Interlocked.CompareExchange(ref _entityHandle, new CEntityHandle(_rawEntityHandle), null);
            return _entityHandle!;
        }
    }

    // A plugin can still write EntityHandle.Raw, so once the wrapper exists it is the source of truth.
    internal uint RawEntityHandle => _entityHandle?.Raw ?? _rawEntityHandle;

    public NativeEntity(IntPtr pointer) : base(pointer)
    {
        _rawEntityHandle = EntitySystem.GetRawHandleFromEntityPointer(pointer);
    }

    public NativeEntity(uint rawHandle) : base(EntitySystem.GetEntityByHandle(rawHandle) ?? IntPtr.Zero)
    {
        _rawEntityHandle = rawHandle;
    }
}
