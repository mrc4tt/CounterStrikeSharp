using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Modules.Utils;

namespace CounterStrikeSharp.API.Core;

public partial class CEntityIOOutput 
{ 
    public EntityIOConnection_t? Connections => Utilities.GetPointer<EntityIOConnection_t>(Handle + 8); 
 
    public EntityIOOutputDesc_t Description => new(Marshal.ReadIntPtr(Handle + 16)); 
} 
 
public class EntityIOOutputDesc_t : NativeObject 
{ 
    public EntityIOOutputDesc_t(IntPtr pointer) : base(pointer) 
    { 
    } 
 
    public string Name => Utilities.ReadStringUtf8(Handle + 0); 
    public unsafe ref uint Flags => ref Unsafe.AsRef<uint>((void*)(Handle + 8)); 
    // EntOutput_t: m_pfnGetOutputOwner (a function pointer) sits at +16, the offset after it.
    public unsafe ref uint OutputOffset => ref Unsafe.AsRef<uint>((void*)(Handle + 24)); 
} 
 
public class EntityIOConnection_t : EntityIOConnectionDesc_t 
{ 
    public EntityIOConnection_t(IntPtr pointer) : base(pointer) 
    { 
    } 
 
    // A 24-byte CPulseInputParamMap (KeyValues3 + bool) follows the description at +40; reading
    // Next at the old +48 got KeyValues3 data and crashed the next string read.
    public unsafe ref bool MarkedForRemoval => ref Unsafe.AsRef<bool>((void*)(Handle + 64)); 
 
    public EntityIOConnection_t? Next => Utilities.GetPointer<EntityIOConnection_t>(Handle + 72); 
} 
 
public class EntityIOConnectionDesc_t : NativeObject 
{ 
    public EntityIOConnectionDesc_t(IntPtr pointer) : base(pointer) 
    { 
    } 
 
    public string TargetDesc => Utilities.ReadStringUtf8(Handle + 0); 
    public string TargetInput => Utilities.ReadStringUtf8(Handle + 8); 
    public string ValueOverride => Utilities.ReadStringUtf8(Handle + 16); 
    public CEntityHandle Target => new(Handle + 24); 
    public unsafe ref EntityIOTargetType_t TargetType => ref Unsafe.AsRef<EntityIOTargetType_t>((void*)(Handle + 28)); 
    public unsafe ref int TimesToFire => ref Unsafe.AsRef<int>((void*)(Handle + 32)); 
    public unsafe ref float Delay => ref Unsafe.AsRef<float>((void*)(Handle + 36)); 
}