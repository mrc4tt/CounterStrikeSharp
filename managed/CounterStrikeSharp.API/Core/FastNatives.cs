using System;
using System.Runtime.InteropServices;

namespace CounterStrikeSharp.API.Core;

/// <summary>
/// Direct function pointers into the native core for the hottest natives, fetched once through
/// GET_FAST_NATIVE_TABLE (src/scripting/natives/natives_fastpath.cpp). A ScriptContext native costs
/// ~35-40 ns of marshalling before its handler runs, which for a handler of a few nanoseconds is the
/// whole cost; these calls skip it.
/// </summary>
/// <remarks>
/// The entries never throw and never report errors: a failure comes back as a null/false result and
/// the caller falls back to the ScriptContext native, which raises the usual exception. When the core
/// is older than this API (no GET_FAST_NATIVE_TABLE, or a shorter table) <see cref="Available"/> is false
/// and every caller takes its ScriptContext path.
/// </remarks>
internal static unsafe class FastNatives
{
    // Mirrors FastNativeTable in natives_fastpath.cpp. Append-only.
    //
    // [SuppressGCTransition] skips the cooperative->preemptive GC mode switch of an unmanaged call
    // (most of what is left of the cost once the ScriptContext is gone). Only for entries that are a
    // few loads: they must not block, take locks, call back into managed code or run engine code of
    // unknown length, because the GC cannot suspend this thread while they run. SetStateChanged and
    // NetworkStateChanged run engine virtuals, so they keep the transition.
    [StructLayout(LayoutKind.Sequential)]
    private struct Table
    {
        public uint Version;
        public uint Size;
        public delegate* unmanaged[SuppressGCTransition]<ushort, IntPtr> GetConvarValueAddress;
        public delegate* unmanaged<IntPtr, uint, uint, uint, void> SetStateChanged;
        public delegate* unmanaged<IntPtr, uint, uint, uint, void> NetworkStateChanged;
        public delegate* unmanaged[SuppressGCTransition]<float> CurrentTime;
        public delegate* unmanaged[SuppressGCTransition]<int> TickCount;
    }

    private static readonly Table* s_table = Load();

    public static bool Available
    {
        get => s_table != null;
    }

    private static Table* Load()
    {
        IntPtr pointer;
        try
        {
            pointer = NativeAPI.GetFastNativeTable();
        }
        catch (NativeException)
        {
            return null; // core predates the fast table
        }

        var table = (Table*)pointer;
        if (table == null || table->Size < sizeof(Table)) return null;
        return table;
    }

    /// <summary>The ConVar's value storage, or <see cref="IntPtr.Zero"/> when the access index or its data is invalid.</summary>
    public static IntPtr GetConvarValueAddress(ushort accessIndex)
    {
        var table = s_table;
        return table != null ? table->GetConvarValueAddress(accessIndex) : NativeAPI.GetConvarValueAddress(accessIndex);
    }

    public static void SetStateChanged(IntPtr entity, uint offset, uint arrayIndex, uint pathIndex)
    {
        var table = s_table;
        if (table != null)
            table->SetStateChanged(entity, offset, arrayIndex, pathIndex);
        else
            NativeAPI.SchemaSetStateChanged(entity, offset, arrayIndex, pathIndex);
    }

    public static float CurrentTime()
    {
        var table = s_table;
        return table != null ? table->CurrentTime() : NativeAPI.GetCurrentTime();
    }

    public static int TickCount()
    {
        var table = s_table;
        return table != null ? table->TickCount() : NativeAPI.GetTickCount();
    }

    public static void NetworkStateChanged(IntPtr chainEntity, uint offset, uint arrayIndex, uint pathIndex)
    {
        var table = s_table;
        if (table != null)
            table->NetworkStateChanged(chainEntity, offset, arrayIndex, pathIndex);
        else
            NativeAPI.SchemaNetworkStateChanged(chainEntity, offset, arrayIndex, pathIndex);
    }
}
